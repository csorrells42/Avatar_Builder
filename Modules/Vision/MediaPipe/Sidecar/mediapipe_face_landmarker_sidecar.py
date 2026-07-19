import argparse
import base64
import json
import sys
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
        running_mode=vision.RunningMode.IMAGE,
        num_faces=1,
        min_face_detection_confidence=0.30,
        min_face_presence_confidence=0.30,
        min_tracking_confidence=0.30,
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
    )
    landmarker = vision.FaceLandmarker.create_from_options(options)
    return mp, cv2, np, landmarker


def image_from_base64(mp, cv2, np, image_base64):
    image_bytes = base64.b64decode(image_base64)
    buffer = np.frombuffer(image_bytes, dtype=np.uint8)
    bgr = cv2.imdecode(buffer, cv2.IMREAD_COLOR)
    if bgr is None:
        raise ValueError("OpenCV could not decode the input image")

    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    return mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)


def handle_request(mp, cv2, np, landmarker, request):
    request_id = request.get("requestId", "")
    image = image_from_base64(mp, cv2, np, request.get("imageBase64", ""))
    result = landmarker.detect(image)
    if not result.face_landmarks:
        return {
            "requestId": request_id,
            "ok": True,
            "hasFace": False,
            "status": "MediaPipe sidecar searching",
            "landmarks": [],
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
    }


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

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        request_id = ""
        try:
            request = json.loads(line)
            request_id = request.get("requestId", "")
            write_response(handle_request(mp, cv2, np, landmarker, request))
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

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
