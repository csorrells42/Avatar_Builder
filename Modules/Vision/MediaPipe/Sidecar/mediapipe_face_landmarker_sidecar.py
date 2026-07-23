import argparse
import json
import mmap
import sys
import time
import traceback


def load_runtime(model_path):
    import cv2
    import mediapipe as mp
    import numpy as np
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision

    base_options = python.BaseOptions(model_asset_path=model_path)
    options = vision.FaceLandmarkerOptions(
        base_options=base_options,
        running_mode=vision.RunningMode.VIDEO,
        num_faces=1,
        min_face_detection_confidence=0.30,
        min_face_presence_confidence=0.30,
        min_tracking_confidence=0.30,
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
    )
    landmarker = vision.FaceLandmarker.create_from_options(options)
    return mp, cv2, np, landmarker


class SharedMemoryFrameReader:
    def __init__(self):
        self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0

    def read_image(self, mp, cv2, np, request):
        name = str(request.get("sharedMemoryName", ""))
        capacity_bytes = int(request.get("sharedMemoryCapacityBytes", 0))
        image_byte_length = int(request.get("imageByteLength", 0))
        width = int(request.get("imageWidth", 0))
        height = int(request.get("imageHeight", 0))
        stride = int(request.get("imageStride", 0))
        pixel_format = str(request.get("imagePixelFormat", ""))

        if not name:
            raise ValueError("shared memory name is missing")
        if capacity_bytes <= 0 or capacity_bytes > 512 * 1024 * 1024:
            raise ValueError(f"invalid shared memory capacity: {capacity_bytes}")
        if width <= 0 or height <= 0 or stride < width * 4:
            raise ValueError(f"invalid BGRA dimensions: {width}x{height}, stride {stride}")
        if image_byte_length != stride * height or image_byte_length > capacity_bytes:
            raise ValueError(
                f"invalid shared image length: {image_byte_length}, expected {stride * height}"
            )
        if pixel_format.upper() != "BGRA32":
            raise ValueError(f"unsupported shared image format: {pixel_format}")

        self._ensure_mapping(name, capacity_bytes)
        pixel_rows = np.ndarray(
            shape=(height, stride),
            dtype=np.uint8,
            buffer=self._mapping,
            offset=0,
        )
        bgra = pixel_rows[:, : width * 4].reshape((height, width, 4))
        rgb = cv2.cvtColor(bgra, cv2.COLOR_BGRA2RGB)
        return mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)

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
            access=mmap.ACCESS_READ,
        )
        self._mapping_name = name
        self._capacity_bytes = capacity_bytes


def handle_request(mp, cv2, np, landmarker, frame_reader, request):
    total_started = time.perf_counter()
    request_id = request.get("requestId", "")
    transport_started = time.perf_counter()
    image = frame_reader.read_image(mp, cv2, np, request)
    transport_ms = elapsed_ms(transport_started)
    inference_started = time.perf_counter()
    timestamp_ms = int(request.get("timestampMilliseconds", 0))
    result = landmarker.detect_for_video(image, timestamp_ms)
    inference_ms = elapsed_ms(inference_started)
    if not result.face_landmarks:
        return {
            "requestId": request_id,
            "ok": True,
            "hasFace": False,
            "status": "MediaPipe sidecar searching",
            "landmarks": [],
            "timingsMilliseconds": {
                "decode": transport_ms,
                "sharedMemoryRead": transport_ms,
                "inference": inference_ms,
                "total": elapsed_ms(total_started),
            },
        }

    landmarks = [
        {"x": landmark.x, "y": landmark.y, "z": landmark.z}
        for landmark in result.face_landmarks[0]
    ]
    blendshapes = []
    if result.face_blendshapes:
        blendshapes = [
            {
                "categoryName": category.category_name,
                "score": float(category.score),
            }
            for category in result.face_blendshapes[0]
        ]
    facial_transformation_matrix = []
    if result.facial_transformation_matrixes:
        try:
            facial_transformation_matrix = (
                np.asarray(result.facial_transformation_matrixes[0], dtype=float)
                .reshape(-1)
                .tolist()
            )
        except Exception:
            facial_transformation_matrix = []
    return {
        "requestId": request_id,
        "ok": True,
        "hasFace": True,
        "status": f"MediaPipe dense landmark lock ({len(landmarks)} points, {len(blendshapes)} blendshapes)",
        "landmarks": landmarks,
        "blendshapes": blendshapes,
        "facialTransformationMatrix": facial_transformation_matrix,
        "timingsMilliseconds": {
            "decode": transport_ms,
            "sharedMemoryRead": transport_ms,
            "inference": inference_ms,
            "total": elapsed_ms(total_started),
        },
    }


def elapsed_ms(started):
    return round((time.perf_counter() - started) * 1000.0, 4)


def write_response(response):
    sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser(description="Avatar Builder MediaPipe Face Landmarker sidecar")
    parser.add_argument("--model", required=True, help="Path to face_landmarker.task")
    args = parser.parse_args()

    try:
        mp, cv2, np, landmarker = load_runtime(args.model)
    except Exception as exc:
        sys.stderr.write(f"MediaPipe sidecar startup failed: {exc}\n")
        sys.stderr.write(traceback.format_exc())
        sys.stderr.flush()
        return 3

    frame_reader = SharedMemoryFrameReader()
    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue

            request_id = ""
            try:
                request = json.loads(line)
                request_id = request.get("requestId", "")
                write_response(handle_request(mp, cv2, np, landmarker, frame_reader, request))
            except Exception as exc:
                write_response(
                    {
                        "requestId": request_id,
                        "ok": False,
                        "hasFace": False,
                        "status": f"MediaPipe sidecar request failed: {exc}",
                        "landmarks": [],
                    }
                )
    finally:
        frame_reader.close()
        landmarker.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
