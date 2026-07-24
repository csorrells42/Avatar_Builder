import argparse
import importlib.util
import json
import pathlib
import statistics
import time


def load_gpu_module(repo_root):
    script_path = (
        repo_root
        / "Modules"
        / "Vision"
        / "MediaPipe"
        / "Sidecar"
        / "mediapipe_face_landmarker_directml_sidecar.py"
    )
    spec = importlib.util.spec_from_file_location("avatar_builder_mediapipe_gpu", script_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def bounding_box(points):
    return (
        min(point[0] for point in points),
        min(point[1] for point in points),
        max(point[0] for point in points),
        max(point[1] for point in points),
    )


def box_iou(first, second):
    intersection_width = max(0.0, min(first[2], second[2]) - max(first[0], second[0]))
    intersection_height = max(0.0, min(first[3], second[3]) - max(first[1], second[1]))
    intersection = intersection_width * intersection_height
    first_area = max(0.0, first[2] - first[0]) * max(0.0, first[3] - first[1])
    second_area = max(0.0, second[2] - second[0]) * max(0.0, second[3] - second[1])
    union = first_area + second_area - intersection
    return intersection / union if union > 0.0 else 0.0


def percentile(values, fraction):
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round((len(ordered) - 1) * fraction)))
    return ordered[index]


def main():
    parser = argparse.ArgumentParser(
        description="Compare Avatar Builder's official CPU and DirectML MediaPipe paths"
    )
    parser.add_argument("--image", required=True)
    parser.add_argument("--iterations", type=int, default=50)
    args = parser.parse_args()

    import cv2
    import mediapipe as mp
    import numpy as np
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision

    repo_root = pathlib.Path(__file__).resolve().parents[2]
    task_model = (
        repo_root
        / "dependencies"
        / "vision"
        / "dense-face-landmarks"
        / "face_landmarker.task"
    )
    detector_model = (
        repo_root
        / "dependencies"
        / "vision"
        / "dense-face-landmarks"
        / "onnx"
        / "face_detector.onnx"
    )
    landmark_model = (
        repo_root
        / "dependencies"
        / "vision"
        / "dense-face-landmarks"
        / "onnx"
        / "face_landmarks_detector.onnx"
    )

    bgr = cv2.imread(args.image, cv2.IMREAD_COLOR)
    if bgr is None:
        raise RuntimeError(f"could not read comparison image: {args.image}")
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)

    options = vision.FaceLandmarkerOptions(
        base_options=python.BaseOptions(
            model_asset_path=str(task_model),
            delegate=python.BaseOptions.Delegate.CPU,
        ),
        running_mode=vision.RunningMode.IMAGE,
        num_faces=1,
        min_face_detection_confidence=0.30,
        min_face_presence_confidence=0.30,
        min_tracking_confidence=0.30,
    )

    with vision.FaceLandmarker.create_from_options(options) as cpu_landmarker:
        cpu_result = cpu_landmarker.detect(image)
        if not cpu_result.face_landmarks:
            raise RuntimeError("official CPU MediaPipe path did not find a face")
        cpu_samples = []
        for _ in range(max(1, args.iterations)):
            started = time.perf_counter()
            cpu_landmarker.detect(image)
            cpu_samples.append((time.perf_counter() - started) * 1000.0)

    cpu_points = [
        (float(point.x), float(point.y), float(point.z))
        for point in cpu_result.face_landmarks[0]
    ]

    gpu_module = load_gpu_module(repo_root)
    gpu_components = gpu_module.load_runtime(str(detector_model), str(landmark_model))
    gpu_runtime = gpu_module.Runtime(gpu_components)
    gpu_points, _, _, _ = gpu_runtime.landmarker.analyze(rgb)
    if gpu_points is None:
        raise RuntimeError("DirectML MediaPipe path did not find a face")
    gpu_samples = []
    for _ in range(max(1, args.iterations)):
        started = time.perf_counter()
        gpu_runtime.landmarker.analyze(rgb)
        gpu_samples.append((time.perf_counter() - started) * 1000.0)

    gpu_tuples = [
        (float(point["x"]), float(point["y"]), float(point["z"]))
        for point in gpu_points
    ]
    if len(cpu_points) != len(gpu_tuples):
        raise RuntimeError(
            f"landmark count mismatch: CPU {len(cpu_points)}, GPU {len(gpu_tuples)}"
        )

    height, width = rgb.shape[:2]
    pixel_errors = [
        math_distance(
            cpu_point[0] * width,
            cpu_point[1] * height,
            gpu_point[0] * width,
            gpu_point[1] * height,
        )
        for cpu_point, gpu_point in zip(cpu_points, gpu_tuples)
    ]
    cpu_box = bounding_box(cpu_points)
    gpu_box = bounding_box(gpu_tuples)
    cpu_width_pixels = (cpu_box[2] - cpu_box[0]) * width
    cpu_height_pixels = (cpu_box[3] - cpu_box[1]) * height
    face_diagonal = math_distance(0.0, 0.0, cpu_width_pixels, cpu_height_pixels)
    median_error = statistics.median(pixel_errors)
    result = {
        "ok": True,
        "image": str(pathlib.Path(args.image).resolve()),
        "landmarkCount": len(cpu_points),
        "boxIou": round(box_iou(cpu_box, gpu_box), 6),
        "medianPixelError": round(median_error, 4),
        "p95PixelError": round(percentile(pixel_errors, 0.95), 4),
        "medianFaceRelativeError": round(
            median_error / face_diagonal if face_diagonal > 0.0 else 0.0,
            6,
        ),
        "cpuAverageMilliseconds": round(statistics.mean(cpu_samples), 4),
        "cpuFramesPerSecond": round(1000.0 / statistics.mean(cpu_samples), 2),
        "gpuAverageMilliseconds": round(statistics.mean(gpu_samples), 4),
        "gpuFramesPerSecond": round(1000.0 / statistics.mean(gpu_samples), 2),
    }
    print(json.dumps(result, indent=2))


def math_distance(first_x, first_y, second_x, second_y):
    delta_x = first_x - second_x
    delta_y = first_y - second_y
    return (delta_x * delta_x + delta_y * delta_y) ** 0.5


if __name__ == "__main__":
    main()
