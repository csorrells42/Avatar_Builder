import argparse
import json
import mmap
import pathlib
import subprocess
import sys
import time
import uuid


def aligned_capacity(required_bytes):
    alignment = 4 * 1024 * 1024
    return max(alignment, ((required_bytes + alignment - 1) // alignment) * alignment)


def main():
    parser = argparse.ArgumentParser(
        description="Exercise the DirectML MediaPipe shared-memory sidecar protocol"
    )
    parser.add_argument("--image", required=True)
    args = parser.parse_args()

    import cv2
    import numpy as np

    repo_root = pathlib.Path(__file__).resolve().parents[2]
    sidecar = (
        repo_root
        / "Modules"
        / "Vision"
        / "MediaPipe"
        / "Sidecar"
        / "mediapipe_face_landmarker_directml_sidecar.py"
    )
    detector = (
        repo_root
        / "dependencies"
        / "vision"
        / "dense-face-landmarks"
        / "onnx"
        / "face_detector.onnx"
    )
    landmarker = (
        repo_root
        / "dependencies"
        / "vision"
        / "dense-face-landmarks"
        / "onnx"
        / "face_landmarks_detector.onnx"
    )

    bgr = cv2.imread(args.image, cv2.IMREAD_COLOR)
    if bgr is None:
        raise RuntimeError(f"could not read transport smoke image: {args.image}")
    bgra = np.ascontiguousarray(cv2.cvtColor(bgr, cv2.COLOR_BGR2BGRA))
    height, width = bgra.shape[:2]
    stride = width * 4
    image_bytes = bgra.tobytes()
    capacity = aligned_capacity(len(image_bytes))
    mapping_name = f"AvatarBuilder.MediaPipe.TransportSmoke.{uuid.uuid4().hex}"

    process = subprocess.Popen(
        [
            sys.executable,
            str(sidecar),
            "--detector-model",
            str(detector),
            "--landmarker-model",
            str(landmarker),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )

    mapping = mmap.mmap(
        -1,
        capacity,
        tagname=mapping_name,
        access=mmap.ACCESS_WRITE,
    )
    try:
        mapping.seek(0)
        mapping.write(image_bytes)
        mapping.flush()
        response = None
        round_trip_samples = []
        sidecar_samples = []
        for request_number in range(1, 9):
            request = {
                "requestId": f"transport-smoke-{request_number:06d}",
                "sharedMemoryName": mapping_name,
                "sharedMemoryCapacityBytes": capacity,
                "imageByteLength": len(image_bytes),
                "imageWidth": width,
                "imageHeight": height,
                "imageStride": stride,
                "imagePixelFormat": "BGRA32",
                "capturedAtUtc": "2026-07-22T00:00:00.0000000Z",
                "timestampMilliseconds": request_number,
            }
            started = time.perf_counter()
            process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
            process.stdin.flush()
            response_line = process.stdout.readline()
            elapsed = (time.perf_counter() - started) * 1000.0
            if not response_line:
                error = process.stderr.read().strip()
                raise RuntimeError(f"DirectML sidecar returned no response: {error}")
            response = json.loads(response_line)
            landmark_count = len(response.get("landmarks", []))
            if not response.get("ok") or not response.get("hasFace"):
                raise RuntimeError(
                    response.get("status", "DirectML sidecar did not find a face")
                )
            if landmark_count != 478:
                raise RuntimeError(
                    f"DirectML sidecar returned {landmark_count} landmarks, expected 478"
                )
            round_trip_samples.append(elapsed)
            sidecar_samples.append(
                float(response.get("timingsMilliseconds", {}).get("total", 0.0))
            )

        steady_round_trip = round_trip_samples[2:]
        steady_sidecar = sidecar_samples[2:]
        print(
            json.dumps(
                {
                    "ok": True,
                    "landmarkCount": len(response.get("landmarks", [])),
                    "firstRoundTripMilliseconds": round(round_trip_samples[0], 4),
                    "steadyRoundTripMilliseconds": round(
                        sum(steady_round_trip) / len(steady_round_trip),
                        4,
                    ),
                    "steadyFramesPerSecond": round(
                        1000.0 / (sum(steady_round_trip) / len(steady_round_trip)),
                        2,
                    ),
                    "steadySidecarMilliseconds": round(
                        sum(steady_sidecar) / len(steady_sidecar),
                        4,
                    ),
                    "status": response.get("status", ""),
                },
                indent=2,
            )
        )
    finally:
        mapping.close()
        if process.stdin:
            process.stdin.close()
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


if __name__ == "__main__":
    main()
