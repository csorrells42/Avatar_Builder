import argparse
import json
import math
import mmap
import sys
import time
import traceback


DETECTOR_SIZE = 128
LANDMARK_SIZE = 256
LANDMARK_COUNT = 478
FACE_CROP_SCALE = 1.5
MIN_FACE_SCORE = 0.30
MIN_PRESENCE_SCORE = 0.30


def elapsed_ms(started):
    return round((time.perf_counter() - started) * 1000.0, 4)


def sigmoid(value):
    if value >= 0.0:
        exponential = math.exp(-min(value, 100.0))
        return 1.0 / (1.0 + exponential)
    exponential = math.exp(max(value, -100.0))
    return exponential / (1.0 + exponential)


def write_response(response):
    sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
    sys.stdout.flush()


class SharedMemoryFrameReader:
    def __init__(self):
        self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0
        self._image_byte_length = 0
        self._width = 0
        self._height = 0
        self._stride = 0
        self._pixel_format = ""

    def read_rgb(self, cv2, np, request):
        name = request.get("sharedMemoryName")
        if name:
            capacity_bytes = int(request.get("sharedMemoryCapacityBytes", 0))
            image_byte_length = int(request.get("imageByteLength", 0))
            width = int(request.get("imageWidth", 0))
            height = int(request.get("imageHeight", 0))
            stride = int(request.get("imageStride", 0))
            pixel_format = str(request.get("imagePixelFormat", ""))
            if capacity_bytes <= 0 or capacity_bytes > 512 * 1024 * 1024:
                raise ValueError(f"invalid shared memory capacity: {capacity_bytes}")
            self._ensure_mapping(str(name), capacity_bytes)
            self._image_byte_length = image_byte_length
            self._width = width
            self._height = height
            self._stride = stride
            self._pixel_format = pixel_format
        elif self._mapping is None:
            raise ValueError("shared memory transport is not initialized")
        else:
            capacity_bytes = self._capacity_bytes
            image_byte_length = self._image_byte_length
            width = self._width
            height = self._height
            stride = self._stride
            pixel_format = self._pixel_format

        if width <= 0 or height <= 0 or stride < width * 4:
            raise ValueError(f"invalid BGRA dimensions: {width}x{height}, stride {stride}")
        if image_byte_length != stride * height or image_byte_length > capacity_bytes:
            raise ValueError(
                f"invalid shared image length: {image_byte_length}, expected {stride * height}"
            )
        if pixel_format.upper() != "BGRA32":
            raise ValueError(f"unsupported shared image format: {pixel_format}")

        pixel_rows = np.ndarray(
            shape=(height, stride),
            dtype=np.uint8,
            buffer=self._mapping,
            offset=0,
        )
        bgra = pixel_rows[:, : width * 4].reshape((height, width, 4))
        return cv2.cvtColor(bgra, cv2.COLOR_BGRA2RGB)

    def close(self):
        if self._mapping is not None:
            self._mapping.close()
            self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0
        self._image_byte_length = 0
        self._width = 0
        self._height = 0
        self._stride = 0
        self._pixel_format = ""

    def _ensure_mapping(self, name, capacity_bytes):
        if (
            self._mapping is not None
            and self._mapping_name == name
            and self._capacity_bytes == capacity_bytes
        ):
            return

        self.close()
        self._mapping = mmap.mmap(
            -1,
            capacity_bytes,
            tagname=name,
            access=mmap.ACCESS_READ,
        )
        self._mapping_name = name
        self._capacity_bytes = capacity_bytes


class SharedMemoryLandmarkWriter:
    MAXIMUM_LANDMARK_COUNT = 512
    VALUES_PER_LANDMARK = 3

    def __init__(self):
        self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0

    def configure(self, request):
        name = request.get("landmarkSharedMemoryName")
        if name:
            capacity_bytes = int(request.get("landmarkSharedMemoryCapacityBytes", 0))
            maximum_required_bytes = (
                self.MAXIMUM_LANDMARK_COUNT * self.VALUES_PER_LANDMARK * 4
            )
            if (
                maximum_required_bytes > capacity_bytes
                or capacity_bytes <= 0
                or capacity_bytes > 1024 * 1024
            ):
                raise ValueError(f"invalid landmark shared memory capacity: {capacity_bytes}")
            self._ensure_mapping(str(name), capacity_bytes)
        elif self._mapping is None:
            raise ValueError("landmark shared memory transport is not initialized")

    def write(self, np, landmarks):
        if self._mapping is None:
            raise ValueError("landmark shared memory transport is not initialized")
        required_bytes = len(landmarks) * self.VALUES_PER_LANDMARK * 4
        if required_bytes > self._capacity_bytes:
            raise ValueError(f"invalid landmark shared memory capacity: {self._capacity_bytes}")
        if len(landmarks) > self.MAXIMUM_LANDMARK_COUNT:
            raise ValueError(f"invalid landmark count: {len(landmarks)}")

        values = np.ndarray(
            shape=(len(landmarks), self.VALUES_PER_LANDMARK),
            dtype=np.float32,
            buffer=self._mapping,
            offset=0,
        )
        for index, landmark in enumerate(landmarks):
            values[index, 0] = landmark["x"]
            values[index, 1] = landmark["y"]
            values[index, 2] = landmark["z"]

    def close(self):
        if self._mapping is not None:
            self._mapping.close()
            self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0

    def _ensure_mapping(self, name, capacity_bytes):
        if (
            self._mapping is not None
            and self._mapping_name == name
            and self._capacity_bytes == capacity_bytes
        ):
            return

        self.close()
        self._mapping = mmap.mmap(
            -1,
            capacity_bytes,
            tagname=name,
            access=mmap.ACCESS_WRITE,
        )
        self._mapping_name = name
        self._capacity_bytes = capacity_bytes


def create_session(ort, model_path):
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    session = ort.InferenceSession(
        model_path,
        sess_options=options,
        providers=["DmlExecutionProvider", "CPUExecutionProvider"],
    )
    if "DmlExecutionProvider" not in session.get_providers():
        raise RuntimeError("ONNX Runtime did not activate the DirectML execution provider")
    return session


def calculate_scale(layer_index, layer_count, minimum, maximum):
    if layer_count == 1:
        return (minimum + maximum) * 0.5
    return minimum + (maximum - minimum) * layer_index / (layer_count - 1)


def generate_detector_anchors(np):
    strides = (8, 16, 16, 16)
    minimum_scale = 0.1484375
    maximum_scale = 0.75
    anchors = []
    layer = 0

    while layer < len(strides):
        same_stride_end = layer
        while (
            same_stride_end < len(strides)
            and strides[same_stride_end] == strides[layer]
        ):
            same_stride_end += 1

        anchor_scales = []
        for current_layer in range(layer, same_stride_end):
            scale = calculate_scale(
                current_layer,
                len(strides),
                minimum_scale,
                maximum_scale,
            )
            anchor_scales.append(scale)
            next_scale = (
                calculate_scale(
                    current_layer + 1,
                    len(strides),
                    minimum_scale,
                    maximum_scale,
                )
                if current_layer < len(strides) - 1
                else 1.0
            )
            anchor_scales.append(math.sqrt(scale * next_scale))

        stride = strides[layer]
        feature_map_size = int(math.ceil(DETECTOR_SIZE / stride))
        for y in range(feature_map_size):
            center_y = (y + 0.5) / feature_map_size
            for x in range(feature_map_size):
                center_x = (x + 0.5) / feature_map_size
                for _ in anchor_scales:
                    anchors.append((center_x, center_y))

        layer = same_stride_end

    if len(anchors) != 896:
        raise RuntimeError(f"detector anchor generation produced {len(anchors)}, expected 896")
    return np.asarray(anchors, dtype=np.float32)


def letterbox_detector_input(cv2, np, rgb):
    height, width = rgb.shape[:2]
    scale = min(DETECTOR_SIZE / width, DETECTOR_SIZE / height)
    resized_width = max(1, int(round(width * scale)))
    resized_height = max(1, int(round(height * scale)))
    resized = cv2.resize(
        rgb,
        (resized_width, resized_height),
        interpolation=cv2.INTER_AREA,
    )
    left = (DETECTOR_SIZE - resized_width) // 2
    top = (DETECTOR_SIZE - resized_height) // 2
    tensor = np.zeros((DETECTOR_SIZE, DETECTOR_SIZE, 3), dtype=np.float32)
    tensor[top : top + resized_height, left : left + resized_width] = resized
    tensor = (tensor - 127.5) / 127.5
    transform = {
        "scale": scale,
        "left": left,
        "top": top,
        "frameWidth": width,
        "frameHeight": height,
    }
    return tensor[np.newaxis, ...], transform


def detector_point_to_frame(point, transform):
    detector_x = point[0] * DETECTOR_SIZE
    detector_y = point[1] * DETECTOR_SIZE
    frame_x = (detector_x - transform["left"]) / transform["scale"]
    frame_y = (detector_y - transform["top"]) / transform["scale"]
    return frame_x, frame_y


def decode_detector(np, regressors, scores, anchors, transform):
    raw_boxes = np.asarray(regressors, dtype=np.float32).reshape((-1, 16))
    raw_scores = np.asarray(scores, dtype=np.float32).reshape(-1)
    score_values = 1.0 / (1.0 + np.exp(-np.clip(raw_scores, -80.0, 80.0)))
    candidate_indices = np.flatnonzero(score_values >= MIN_FACE_SCORE)
    candidates = []

    for index in candidate_indices:
        raw = raw_boxes[index]
        anchor_x, anchor_y = anchors[index]
        center_x = raw[0] / DETECTOR_SIZE + anchor_x
        center_y = raw[1] / DETECTOR_SIZE + anchor_y
        width = raw[2] / DETECTOR_SIZE
        height = raw[3] / DETECTOR_SIZE
        minimum = detector_point_to_frame(
            (center_x - width * 0.5, center_y - height * 0.5),
            transform,
        )
        maximum = detector_point_to_frame(
            (center_x + width * 0.5, center_y + height * 0.5),
            transform,
        )
        keypoints = []
        for keypoint_index in range(6):
            offset = 4 + keypoint_index * 2
            keypoints.append(
                detector_point_to_frame(
                    (
                        raw[offset] / DETECTOR_SIZE + anchor_x,
                        raw[offset + 1] / DETECTOR_SIZE + anchor_y,
                    ),
                    transform,
                )
            )
        candidates.append(
            {
                "score": float(score_values[index]),
                "box": (
                    float(minimum[0]),
                    float(minimum[1]),
                    float(maximum[0]),
                    float(maximum[1]),
                ),
                "keypoints": keypoints,
            }
        )

    if not candidates:
        return None
    return max(candidates, key=lambda candidate: candidate["score"])


def roi_from_detection(detection):
    minimum_x, minimum_y, maximum_x, maximum_y = detection["box"]
    first_eye = detection["keypoints"][0]
    second_eye = detection["keypoints"][1]
    left_eye, right_eye = sorted((first_eye, second_eye), key=lambda point: point[0])
    angle = math.atan2(right_eye[1] - left_eye[1], right_eye[0] - left_eye[0])
    return {
        "centerX": (minimum_x + maximum_x) * 0.5,
        "centerY": (minimum_y + maximum_y) * 0.5,
        "size": max(maximum_x - minimum_x, maximum_y - minimum_y)
        * FACE_CROP_SCALE,
        "angle": angle,
    }


def roi_from_landmarks(landmarks, frame_width, frame_height):
    pixel_x = [landmark["x"] * frame_width for landmark in landmarks]
    pixel_y = [landmark["y"] * frame_height for landmark in landmarks]
    left_eye = landmarks[33]
    right_eye = landmarks[263]
    angle = math.atan2(
        right_eye["y"] * frame_height - left_eye["y"] * frame_height,
        right_eye["x"] * frame_width - left_eye["x"] * frame_width,
    )
    minimum_x = min(pixel_x)
    maximum_x = max(pixel_x)
    minimum_y = min(pixel_y)
    maximum_y = max(pixel_y)
    return {
        "centerX": (minimum_x + maximum_x) * 0.5,
        "centerY": (minimum_y + maximum_y) * 0.5,
        "size": max(maximum_x - minimum_x, maximum_y - minimum_y)
        * FACE_CROP_SCALE,
        "angle": angle,
    }


def crop_landmark_input(cv2, np, rgb, roi):
    center_x = roi["centerX"]
    center_y = roi["centerY"]
    half_size = roi["size"] * 0.5
    cosine = math.cos(roi["angle"])
    sine = math.sin(roi["angle"])
    right = np.asarray((cosine, sine), dtype=np.float32)
    down = np.asarray((-sine, cosine), dtype=np.float32)
    center = np.asarray((center_x, center_y), dtype=np.float32)

    source = np.asarray(
        (
            center - right * half_size - down * half_size,
            center + right * half_size - down * half_size,
            center + right * half_size + down * half_size,
            center - right * half_size + down * half_size,
        ),
        dtype=np.float32,
    )
    destination = np.asarray(
        (
            (0.0, 0.0),
            (LANDMARK_SIZE - 1.0, 0.0),
            (LANDMARK_SIZE - 1.0, LANDMARK_SIZE - 1.0),
            (0.0, LANDMARK_SIZE - 1.0),
        ),
        dtype=np.float32,
    )
    frame_to_crop = cv2.getPerspectiveTransform(source, destination)
    crop_to_frame = cv2.getPerspectiveTransform(destination, source)
    crop = cv2.warpPerspective(
        rgb,
        frame_to_crop,
        (LANDMARK_SIZE, LANDMARK_SIZE),
        flags=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0),
    )
    tensor = crop.astype(np.float32) / 255.0
    return tensor[np.newaxis, ...], crop_to_frame


def project_landmarks(cv2, np, raw_landmarks, crop_to_frame, roi, frame_shape):
    values = np.asarray(raw_landmarks, dtype=np.float32).reshape((-1, 3))
    if values.shape[0] != LANDMARK_COUNT:
        raise RuntimeError(
            f"landmark model returned {values.shape[0]} points, expected {LANDMARK_COUNT}"
        )
    crop_points = values[:, :2].reshape((-1, 1, 2))
    frame_points = cv2.perspectiveTransform(crop_points, crop_to_frame).reshape((-1, 2))
    frame_height, frame_width = frame_shape[:2]
    depth_scale = roi["size"] / (LANDMARK_SIZE * frame_width)
    landmarks = []
    for index in range(LANDMARK_COUNT):
        landmarks.append(
            {
                "x": float(frame_points[index, 0] / frame_width),
                "y": float(frame_points[index, 1] / frame_height),
                "z": float(values[index, 2] * depth_scale),
            }
        )
    return landmarks


class DirectMlFaceLandmarker:
    def __init__(self, cv2, np, detector_session, landmark_session):
        self._cv2 = cv2
        self._np = np
        self._detector = detector_session
        self._landmarker = landmark_session
        self._detector_input = detector_session.get_inputs()[0].name
        self._landmark_input = landmark_session.get_inputs()[0].name
        self._anchors = generate_detector_anchors(np)
        self._tracked_roi = None

    def analyze(self, rgb, collect_diagnostics=False):
        timings = {} if collect_diagnostics else None
        detection = None

        if self._tracked_roi is None:
            detector_started = time.perf_counter() if collect_diagnostics else 0.0
            tensor, transform = letterbox_detector_input(self._cv2, self._np, rgb)
            regressors, scores = self._detector.run(
                None,
                {self._detector_input: tensor},
            )
            detection = decode_detector(
                self._np,
                regressors,
                scores,
                self._anchors,
                transform,
            )
            if collect_diagnostics:
                timings["detector"] = elapsed_ms(detector_started)
            if detection is None:
                return None, timings, 0.0, 0.0
            roi = roi_from_detection(detection)
        else:
            roi = self._tracked_roi
            if collect_diagnostics:
                timings["detector"] = 0.0

        crop_started = time.perf_counter() if collect_diagnostics else 0.0
        tensor, crop_to_frame = crop_landmark_input(self._cv2, self._np, rgb, roi)
        if collect_diagnostics:
            timings["crop"] = elapsed_ms(crop_started)

        landmark_started = time.perf_counter() if collect_diagnostics else 0.0
        outputs = self._landmarker.run(
            None,
            {self._landmark_input: tensor},
        )
        if collect_diagnostics:
            timings["landmarker"] = elapsed_ms(landmark_started)
        raw_landmarks = outputs[0]
        presence_raw = float(self._np.asarray(outputs[1]).reshape(-1)[0])
        presence = sigmoid(presence_raw)
        if presence < MIN_PRESENCE_SCORE:
            self._tracked_roi = None
            return None, timings, 0.0 if detection is None else detection["score"], presence

        project_started = time.perf_counter() if collect_diagnostics else 0.0
        landmarks = project_landmarks(
            self._cv2,
            self._np,
            raw_landmarks,
            crop_to_frame,
            roi,
            rgb.shape,
        )
        self._tracked_roi = roi_from_landmarks(
            landmarks,
            rgb.shape[1],
            rgb.shape[0],
        )
        if collect_diagnostics:
            timings["project"] = elapsed_ms(project_started)
        detector_score = 0.0 if detection is None else detection["score"]
        return landmarks, timings, detector_score, presence


def load_runtime(detector_model, landmarker_model):
    import cv2
    import numpy as np
    import onnxruntime as ort

    if "DmlExecutionProvider" not in ort.get_available_providers():
        raise RuntimeError(
            "DirectML is unavailable; install onnxruntime-directml in the MediaPipe environment"
        )
    detector = create_session(ort, detector_model)
    landmarker = create_session(ort, landmarker_model)
    return cv2, np, ort, detector, landmarker


def handle_request(runtime, frame_reader, landmark_writer, request):
    cv2 = runtime.cv2
    np = runtime.np
    collect_diagnostics = bool(request.get("collectDiagnostics", False))
    total_started = time.perf_counter() if collect_diagnostics else 0.0
    transport_started = time.perf_counter() if collect_diagnostics else 0.0
    rgb = frame_reader.read_rgb(cv2, np, request)
    landmark_writer.configure(request)
    transport_ms = elapsed_ms(transport_started) if collect_diagnostics else 0.0
    landmarks, timings, detector_score, presence = runtime.landmarker.analyze(
        rgb,
        collect_diagnostics,
    )
    if collect_diagnostics:
        timings["decode"] = transport_ms
        timings["sharedMemoryRead"] = transport_ms
        timings["total"] = elapsed_ms(total_started)
    request_id = request.get("requestId", "")

    if landmarks is None:
        response = {
            "requestId": request_id,
            "ok": True,
            "hasFace": False,
            "landmarkCount": 0,
        }
        if collect_diagnostics:
            response["timingsMilliseconds"] = timings
        return response

    landmark_writer.write(np, landmarks)
    response = {
        "requestId": request_id,
        "ok": True,
        "hasFace": True,
        "landmarkCount": len(landmarks),
    }
    if collect_diagnostics:
        response["timingsMilliseconds"] = timings
    return response


class Runtime:
    def __init__(self, components):
        self.cv2, self.np, self.ort, detector, landmarker = components
        self.landmarker = DirectMlFaceLandmarker(
            self.cv2,
            self.np,
            detector,
            landmarker,
        )


def benchmark_image(runtime, image_path, iterations):
    bgr = runtime.cv2.imread(image_path, runtime.cv2.IMREAD_COLOR)
    if bgr is None:
        raise RuntimeError(f"could not read benchmark image: {image_path}")
    rgb = runtime.cv2.cvtColor(bgr, runtime.cv2.COLOR_BGR2RGB)
    samples = []
    result = None
    for index in range(max(2, iterations + 1)):
        started = time.perf_counter()
        result, _, _, _ = runtime.landmarker.analyze(rgb)
        duration = elapsed_ms(started)
        if index > 0:
            samples.append(duration)
    average = sum(samples) / len(samples)
    write_response(
        {
            "ok": result is not None,
            "provider": "DirectML",
            "landmarkCount": 0 if result is None else len(result),
            "averageMilliseconds": round(average, 4),
            "framesPerSecond": round(1000.0 / average, 2) if average > 0.0 else 0.0,
            "landmarks": [] if result is None else result,
        }
    )


def main():
    parser = argparse.ArgumentParser(
        description="Avatar Builder MediaPipe-compatible DirectML face landmarker sidecar"
    )
    parser.add_argument("--detector-model", required=True)
    parser.add_argument("--landmarker-model", required=True)
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--benchmark-image")
    parser.add_argument("--iterations", type=int, default=50)
    args = parser.parse_args()

    try:
        components = load_runtime(args.detector_model, args.landmarker_model)
        runtime = Runtime(components)
    except Exception as exc:
        sys.stderr.write(f"MediaPipe DirectML sidecar startup failed: {exc}\n")
        sys.stderr.write(traceback.format_exc())
        sys.stderr.flush()
        return 3

    if args.probe:
        write_response(
            {
                "ok": True,
                "delegate": "gpu",
                "provider": "DirectML",
                "status": "MediaPipe DirectML ONNX detector and landmarker ready",
            }
        )
        return 0

    if args.benchmark_image:
        benchmark_image(runtime, args.benchmark_image, args.iterations)
        return 0

    frame_reader = SharedMemoryFrameReader()
    landmark_writer = SharedMemoryLandmarkWriter()
    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            request_id = ""
            try:
                request = json.loads(line)
                request_id = request.get("requestId", "")
                write_response(handle_request(runtime, frame_reader, landmark_writer, request))
            except Exception as exc:
                write_response(
                    {
                        "requestId": request_id,
                        "ok": False,
                        "hasFace": False,
                        "status": f"MediaPipe DirectML sidecar request failed: {exc}",
                        "landmarkCount": 0,
                    }
                )
    finally:
        landmark_writer.close()
        frame_reader.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
