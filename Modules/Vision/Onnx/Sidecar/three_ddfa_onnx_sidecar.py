import argparse
import base64
import json
import math
import os
import pickle
import sys
import traceback
import time
import importlib.util
import types


def patch_torch_onnx_export_for_3ddfa():
    try:
        import torch
    except Exception:
        return

    if getattr(torch.onnx.export, "_avatar_builder_legacy_default", False):
        return

    original_export = torch.onnx.export

    def export_with_legacy_default(*args, **kwargs):
        kwargs.setdefault("dynamo", False)
        return original_export(*args, **kwargs)

    export_with_legacy_default._avatar_builder_legacy_default = True
    torch.onnx.export = export_with_legacy_default


def install_python_nms_fallback(repo_path):
    """Expose 3DDFA's bundled NumPy NMS under the optional Cython module name."""
    module_name = "FaceBoxes.utils.nms.cpu_nms"
    if module_name in sys.modules:
        return

    fallback_path = os.path.join(
        repo_path, "FaceBoxes", "utils", "nms", "py_cpu_nms.py")
    if not os.path.isfile(fallback_path):
        return

    spec = importlib.util.spec_from_file_location(
        "avatar_builder_faceboxes_python_nms", fallback_path)
    if spec is None or spec.loader is None:
        return

    fallback = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fallback)
    compatibility_module = types.ModuleType(module_name)
    compatibility_module.cpu_nms = fallback.py_cpu_nms

    def cpu_soft_nms(dets, sigma=0.5, nt=0.3, threshold=0.001, method=0):
        del sigma, threshold, method
        return fallback.py_cpu_nms(dets, nt)

    compatibility_module.cpu_soft_nms = cpu_soft_nms
    sys.modules[module_name] = compatibility_module


def load_runtime(repo_path, config_path):
    sys.path.insert(0, repo_path)

    import cv2
    import numpy as np
    import yaml
    from TDDFA_ONNX import TDDFA_ONNX
    from utils.pose import calc_pose
    from utils.tddfa_util import _parse_param

    face_boxes_class = None
    startup_warnings = []
    try:
        install_python_nms_fallback(repo_path)
        from FaceBoxes.FaceBoxes_ONNX import FaceBoxes_ONNX

        face_boxes_class = FaceBoxes_ONNX
    except Exception as exc:
        startup_warnings.append(
            "3DDFA FaceBoxes unavailable; caller-supplied face boxes still work, but 3DDFA cannot own live face-box detection. "
            f"FaceBoxes import error: {exc}"
        )

    with open(config_path, "r", encoding="utf-8") as handle:
        cfg = yaml.safe_load(handle)

    old_cwd = os.getcwd()
    try:
        os.chdir(repo_path)
        patch_torch_onnx_export_for_3ddfa()
        face_boxes = face_boxes_class() if face_boxes_class is not None else None
        tddfa = TDDFA_ONNX(**cfg)
    finally:
        os.chdir(old_cwd)

    dense_triangles = load_dense_triangles(repo_path, np, startup_warnings)

    return cv2, np, face_boxes, tddfa, calc_pose, _parse_param, dense_triangles, startup_warnings


def load_dense_triangles(repo_path, np, startup_warnings):
    path = os.path.join(repo_path, "configs", "tri.pkl")
    try:
        with open(path, "rb") as handle:
            triangles = pickle.load(handle)
        triangles = np.asarray(triangles, dtype=np.int32)
        if len(triangles.shape) == 2 and triangles.shape[0] == 3:
            return triangles
        startup_warnings.append(f"3DDFA topology ignored: expected 3xN triangle array, got {triangles.shape}")
    except Exception as exc:
        startup_warnings.append(f"3DDFA topology unavailable: {exc}")
    return None


def image_from_base64(cv2, np, image_base64):
    image_bytes = base64.b64decode(image_base64)
    buffer = np.frombuffer(image_bytes, dtype=np.uint8)
    image = cv2.imdecode(buffer, cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("OpenCV could not decode the input image")
    return image


def requested_box_to_pixels(box, width, height):
    if not box:
        return None

    left = float(box.get("left", 0.0))
    top = float(box.get("top", 0.0))
    right = float(box.get("right", 0.0))
    bottom = float(box.get("bottom", 0.0))
    confidence = float(box.get("confidence", 1.0))
    if box.get("normalized", True):
        left *= width
        right *= width
        top *= height
        bottom *= height

    left = max(0.0, min(float(width - 1), left))
    right = max(0.0, min(float(width - 1), right))
    top = max(0.0, min(float(height - 1), top))
    bottom = max(0.0, min(float(height - 1), bottom))
    if right <= left or bottom <= top:
        return None
    return [left, top, right, bottom, max(0.01, min(1.0, confidence))]


def choose_face_box(cv2, face_boxes, image, request_box):
    height, width = image.shape[:2]
    requested = requested_box_to_pixels(request_box, width, height)
    if requested is not None:
        return requested

    if face_boxes is None:
        return None

    detection_image = image
    detection_scale = 1.0
    maximum_dimension = max(width, height)
    if maximum_dimension > 640:
        detection_scale = 640.0 / maximum_dimension
        detection_image = cv2.resize(
            image,
            (max(1, round(width * detection_scale)), max(1, round(height * detection_scale))),
            interpolation=cv2.INTER_AREA)

    boxes = face_boxes(detection_image)
    if boxes is None or len(boxes) == 0:
        return None

    boxes = sorted(boxes, key=lambda item: (float(item[4]) if len(item) >= 5 else 1.0), reverse=True)
    selected = list(map(float, boxes[0]))
    if len(selected) < 5:
        selected.append(1.0)
    if detection_scale != 1.0:
        selected[0] /= detection_scale
        selected[1] /= detection_scale
        selected[2] /= detection_scale
        selected[3] /= detection_scale
    return selected[:5]


def vertices_to_json(vertices, stride=1):
    if vertices is None:
        return []

    stride = max(1, int(stride))
    result = []
    count = int(vertices.shape[1]) if len(vertices.shape) > 1 else 0
    for index in range(0, count, stride):
        result.append(
            {
                "index": int(index),
                "x": finite_float(vertices[0, index]),
                "y": finite_float(vertices[1, index]),
                "z": finite_float(vertices[2, index]),
            }
        )
    return result


def sampled_dense_mesh_to_json(vertices, triangles, stride=72, return_all_vertices=False):
    if vertices is None:
        return [], []

    vertex_count = int(vertices.shape[1]) if len(vertices.shape) > 1 else 0
    if vertex_count <= 0:
        return [], []

    stride = max(1, int(stride))
    if triangles is None:
        selected = set(range(vertex_count)) if return_all_vertices else set(range(0, vertex_count, stride))
        return vertices_to_json_for_indices(vertices, sorted(selected)), []

    selected = set(range(vertex_count)) if return_all_vertices else set()
    edges = set()
    triangle_count = int(triangles.shape[1])
    triangle_stride = 1 if return_all_vertices else stride
    for triangle_index in range(0, triangle_count, triangle_stride):
        a = int(triangles[0, triangle_index])
        b = int(triangles[1, triangle_index])
        c = int(triangles[2, triangle_index])
        if not (0 <= a < vertex_count and 0 <= b < vertex_count and 0 <= c < vertex_count):
            continue
        selected.update((a, b, c))
        add_edge(edges, a, b)
        add_edge(edges, b, c)
        add_edge(edges, c, a)

    return vertices_to_json_for_indices(vertices, sorted(selected)), [
        {"fromIndex": int(a), "toIndex": int(b)}
        for a, b in sorted(edges)
        if a in selected and b in selected
    ]


def dense_mesh_to_compact_json(np, vertices, triangles, include_topology=True):
    if vertices is None:
        return [], []

    vertex_count = int(vertices.shape[1]) if len(vertices.shape) > 1 else 0
    if vertex_count <= 0:
        return [], []

    coordinates = np.asarray(vertices, dtype=np.float64).T.reshape(-1)
    coordinates = np.nan_to_num(coordinates, nan=0.0, posinf=0.0, neginf=0.0)
    if triangles is None or not include_topology:
        return coordinates.tolist(), []

    triangle_rows = np.asarray(triangles, dtype=np.int32).T
    valid = np.all((triangle_rows >= 0) & (triangle_rows < vertex_count), axis=1)
    triangle_rows = triangle_rows[valid]
    if triangle_rows.size == 0:
        return coordinates.tolist(), []

    edge_pairs = np.concatenate(
        (
            triangle_rows[:, [0, 1]],
            triangle_rows[:, [1, 2]],
            triangle_rows[:, [2, 0]],
        ),
        axis=0)
    edge_pairs.sort(axis=1)
    unique_edges = np.unique(edge_pairs, axis=0)
    return coordinates.tolist(), unique_edges.reshape(-1).astype(np.int32).tolist()


def vertices_to_compact_json(np, vertices):
    if vertices is None:
        return []
    coordinates = np.asarray(vertices, dtype=np.float64).T.reshape(-1)
    return np.nan_to_num(coordinates, nan=0.0, posinf=0.0, neginf=0.0).tolist()


def add_edge(edges, a, b):
    if a == b:
        return
    edges.add((min(a, b), max(a, b)))


def vertices_to_json_for_indices(vertices, indices):
    result = []
    for index in indices:
        result.append(
            {
                "index": int(index),
                "x": finite_float(vertices[0, index]),
                "y": finite_float(vertices[1, index]),
                "z": finite_float(vertices[2, index]),
            }
        )
    return result


def finite_float(value):
    value = float(value)
    if math.isnan(value) or math.isinf(value):
        return 0.0
    return value


def split_coefficients(param):
    values = [finite_float(value) for value in param.reshape(-1).tolist()]
    return {
        "camera": values[:12],
        "shape": values[12:52],
        "expression": values[52:62],
    }


def handle_request(cv2, np, face_boxes, tddfa, calc_pose, parse_param, dense_triangles, startup_warnings, request):
    total_started = time.perf_counter()
    request_id = request.get("requestId", "")
    captured_at_utc = request.get("capturedAtUtc", "")
    mode = request.get("mode", "tracking")
    if mode not in ("faceBoxOnly", "tracking", "preview", "full", "model"):
        mode = "tracking"
    if mode == "model":
        dense_started = time.perf_counter()
        alpha_shape = np.zeros(
            (tddfa.w_shp_base.shape[1], 1),
            dtype=np.float32)
        alpha_expression = np.zeros(
            (tddfa.w_exp_base.shape[1], 1),
            dtype=np.float32)
        canonical_identity = tddfa.bfm_session.run(
            None,
            {
                "R": np.eye(3, dtype=np.float32),
                "offset": np.zeros((3, 1), dtype=np.float32),
                "alpha_shp": alpha_shape,
                "alpha_exp": alpha_expression,
            },
        )[0]
        canonical_sparse = (
            tddfa.u_base
            + tddfa.w_shp_base @ alpha_shape
            + tddfa.w_exp_base @ alpha_expression
        ).reshape(3, -1, order="F")
        canonical_identity_coordinates, dense_edge_indices = dense_mesh_to_compact_json(
            np,
            canonical_identity,
            dense_triangles,
            include_topology=bool(request.get("includeTopology", True)))
        dense_count = int(canonical_identity.shape[1])
        dense_ms = elapsed_ms(dense_started)
        return {
            "requestId": request_id,
            "ok": True,
            "hasFace": True,
            "status": f"3DDFA/ONNX neutral dense model ({dense_count} vertices)",
            "backend": "3DDFA_V2 ONNX",
            "capturedAtUtc": captured_at_utc,
            "mode": mode,
            "trustDecision": "Neutral 3DDFA topology is ready for the saved MediaPipe geometry",
            "reconstructionConfidencePercent": 100.0,
            "denseVertexCount": dense_count,
            "denseSampleStride": 1,
            "denseVertices": [],
            "canonicalIdentityVertices": [],
            "denseEdges": [],
            "denseVertexCoordinates": [],
            "canonicalIdentityCoordinates": canonical_identity_coordinates,
            "canonicalSparseLandmarks": vertices_to_json(canonical_sparse, 1),
            "denseEdgeIndices": dense_edge_indices,
            "warnings": startup_warnings,
            "timingsMilliseconds": stage_timings(
                dense=dense_ms,
                total=elapsed_ms(total_started)),
        }
    decode_started = time.perf_counter()
    image = image_from_base64(cv2, np, request.get("imageBase64", ""))
    decode_ms = elapsed_ms(decode_started)
    face_box_started = time.perf_counter()
    face_box = choose_face_box(cv2, face_boxes, image, request.get("faceBox"))
    face_box_ms = elapsed_ms(face_box_started)
    if face_box is None:
        return {
            "requestId": request_id,
            "ok": True,
            "hasFace": False,
            "status": "3DDFA/ONNX searching",
            "backend": "3DDFA_V2 ONNX",
            "capturedAtUtc": captured_at_utc,
            "mode": mode,
            "trustDecision": "no face box available for dense reconstruction",
            "warnings": startup_warnings,
            "timingsMilliseconds": stage_timings(
                decode=decode_ms,
                face_box=face_box_ms,
                total=elapsed_ms(total_started)),
        }


    confidence = float(face_box[4]) * 100.0
    response_face_box = {
        "left": finite_float(face_box[0]),
        "top": finite_float(face_box[1]),
        "right": finite_float(face_box[2]),
        "bottom": finite_float(face_box[3]),
        "normalized": False,
        "confidence": finite_float(face_box[4]),
    }
    if mode == "faceBoxOnly":
        return {
            "requestId": request_id,
            "ok": True,
            "hasFace": True,
            "status": "3DDFA FaceBoxes lock",
            "backend": "3DDFA_V2 ONNX",
            "capturedAtUtc": captured_at_utc,
            "mode": mode,
            "trustDecision": "FaceBoxes located a face; reconstruction was intentionally skipped",
            "reconstructionConfidencePercent": finite_float(confidence),
            "faceBox": response_face_box,
            "warnings": startup_warnings,
            "timingsMilliseconds": stage_timings(
                decode=decode_ms,
                face_box=face_box_ms,
                total=elapsed_ms(total_started)),
        }

    parameter_started = time.perf_counter()
    param_list, roi_box_list = tddfa(image, [face_box])
    parameter_ms = elapsed_ms(parameter_started)
    if not param_list:
        return {
            "requestId": request_id,
            "ok": True,
            "hasFace": False,
            "status": "3DDFA/ONNX did not return parameters",
            "backend": "3DDFA_V2 ONNX",
            "capturedAtUtc": captured_at_utc,
            "mode": mode,
            "trustDecision": "3DDFA parameter solve failed for this frame",
            "warnings": startup_warnings,
            "timingsMilliseconds": stage_timings(
                decode=decode_ms,
                face_box=face_box_ms,
                parameters=parameter_ms,
                total=elapsed_ms(total_started)),
        }

    param = np.asarray(param_list[0])
    roi_box = [finite_float(value) for value in roi_box_list[0]]
    sparse_started = time.perf_counter()
    sparse = tddfa.recon_vers([param], [roi_box_list[0]], dense_flag=False)[0]
    sparse_ms = elapsed_ms(sparse_started)
    pose_started = time.perf_counter()
    _, pose = calc_pose(param)
    coefficients = split_coefficients(param)
    pose_ms = elapsed_ms(pose_started)

    dense = None
    canonical_identity = None
    canonical_sparse = None
    dense_count = 0
    dense_ms = 0.0
    if mode in ("preview", "full"):
        dense_started = time.perf_counter()
        dense = tddfa.recon_vers([param], [roi_box_list[0]], dense_flag=True)[0]
        dense_ms = elapsed_ms(dense_started)
        dense_count = int(dense.shape[1]) if len(dense.shape) > 1 else 0
        if mode == "full":
            _, _, alpha_shape, alpha_expression = parse_param(param)
            canonical_identity = tddfa.bfm_session.run(
                None,
                {
                    "R": np.eye(3, dtype=np.float32),
                    "offset": np.zeros((3, 1), dtype=np.float32),
                    "alpha_shp": alpha_shape.astype(np.float32),
                    "alpha_exp": np.zeros_like(alpha_expression, dtype=np.float32),
                },
            )[0]
            canonical_sparse = (
                tddfa.u_base
                + tddfa.w_shp_base @ alpha_shape
                + tddfa.w_exp_base @ np.zeros_like(alpha_expression, dtype=np.float32)
            ).reshape(3, -1, order="F")
    stride = int(request.get("denseSampleStride", 24) or 24)
    include_topology = bool(request.get("includeTopology", True))
    return_dense_vertices = mode == "full"
    serialize_started = time.perf_counter()
    dense_vertex_coordinates = []
    canonical_identity_coordinates = []
    dense_edge_indices = []
    if return_dense_vertices:
        dense_vertex_coordinates, dense_edge_indices = dense_mesh_to_compact_json(
            np,
            dense,
            dense_triangles,
            include_topology=include_topology)
        canonical_identity_coordinates = vertices_to_compact_json(np, canonical_identity)
        dense_vertices = []
        dense_edges = []
        canonical_identity_vertices = []
    else:
        dense_vertices, dense_edges = sampled_dense_mesh_to_json(
            dense,
            dense_triangles,
            stride=stride,
            return_all_vertices=False)
        canonical_identity_vertices = []
    serialize_ms = elapsed_ms(serialize_started)
    if dense_count > 0:
        confidence = min(100.0, max(0.0, confidence * 0.85 + 15.0))

    lock_description = "tracking lock"
    if mode == "preview":
        lock_description = f"preview reconstruction lock ({dense_count} vertices)"
    elif mode == "full":
        lock_description = f"full reconstruction lock ({dense_count} vertices)"

    return {
        "requestId": request_id,
        "ok": True,
        "hasFace": True,
        "status": f"3DDFA/ONNX {lock_description}",
        "backend": "3DDFA_V2 ONNX",
        "capturedAtUtc": captured_at_utc,
        "mode": mode,
        "trustDecision": "3DDFA reconstruction and pose are available; the caller controls which live face-box tracker owns this frame",
        "reconstructionConfidencePercent": finite_float(confidence),
        "pose": {
            "aRotationAroundXDegrees": finite_float(pose[1] if len(pose) > 1 else 0.0),
            "bRotationAroundYDegrees": finite_float(pose[0] if len(pose) > 0 else 0.0),
            "cRotationAroundZDegrees": finite_float(pose[2] if len(pose) > 2 else 0.0),
            "source": "3DDFA_V2 ONNX calc_pose",
        },
        "faceBox": response_face_box,
        "roiBox": roi_box,
        "denseVertexCount": dense_count,
        "denseSampleStride": 1 if return_dense_vertices else stride,
        "denseVertices": dense_vertices,
        "canonicalIdentityVertices": canonical_identity_vertices,
        "denseEdges": dense_edges,
        "denseVertexCoordinates": dense_vertex_coordinates,
        "canonicalIdentityCoordinates": canonical_identity_coordinates,
        "canonicalSparseLandmarks": vertices_to_json(canonical_sparse, 1),
        "denseEdgeIndices": dense_edge_indices,
        "sparseLandmarks": vertices_to_json(sparse, 1),
        "cameraMatrixCoefficients": coefficients["camera"],
        "shapeCoefficients": coefficients["shape"],
        "expressionCoefficients": coefficients["expression"],
        "warnings": startup_warnings,
        "timingsMilliseconds": stage_timings(
            decode=decode_ms,
            face_box=face_box_ms,
            parameters=parameter_ms,
            sparse=sparse_ms,
            dense=dense_ms,
            pose=pose_ms,
            serialize=serialize_ms,
            total=elapsed_ms(total_started)),
    }


def elapsed_ms(started):
    return round((time.perf_counter() - started) * 1000.0, 4)


def stage_timings(**values):
    return {key: round(float(value), 4) for key, value in values.items()}


def write_response(response):
    sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser(description="Avatar Builder 3DDFA_V2 ONNX sidecar")
    parser.add_argument("--repo", required=True, help="Path to cloned 3DDFA_V2 repository")
    parser.add_argument("--config", required=True, help="Path to 3DDFA config yml")
    args = parser.parse_args()

    try:
        cv2, np, face_boxes, tddfa, calc_pose, parse_param, dense_triangles, startup_warnings = load_runtime(args.repo, args.config)
    except Exception as exc:
        sys.stderr.write(f"3DDFA/ONNX sidecar startup failed: {exc}\n")
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
            write_response(handle_request(cv2, np, face_boxes, tddfa, calc_pose, parse_param, dense_triangles, startup_warnings, request))
        except Exception as exc:
            write_response(
                {
                    "requestId": request_id,
                    "ok": False,
                    "hasFace": False,
                    "status": f"3DDFA/ONNX sidecar request failed: {exc}",
                    "backend": "3DDFA_V2 ONNX",
                    "trustDecision": "3DDFA request failed; do not use this frame for reconstruction trust",
                    "warnings": [traceback.format_exc(limit=3)],
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
