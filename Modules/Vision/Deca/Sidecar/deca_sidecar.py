#!/usr/bin/env python3
"""Persistent JSON-lines adapter for the official DECA/FLAME implementation."""

from __future__ import annotations

import argparse
import base64
import contextlib
import json
import math
import os
import sys
import time
from typing import Any

import cv2
import numpy as np
import torch
import torch.nn.functional as functional


CROP_SIZE = 224
CROP_SCALE = 1.25
IDENTITY_FIT_FLAME_68 = "flame-68"
IDENTITY_FIT_MEDIAPIPE_SURFACE_ASSISTED = "mediapipe-surface-assisted"
MEDIAPIPE_EMBEDDING_FILE = "mediapipe_landmark_embedding.npz"
MEDIAPIPE_JAW = [234, 93, 132, 58, 172, 136, 150, 149, 176, 148, 152, 377, 400, 378, 379, 365, 397, 288, 361, 323, 454]
MEDIAPIPE_TO_FLAME_68 = {
    27: 168, 28: 6, 29: 197, 30: 1,
    31: 98, 32: 97, 33: 2, 34: 326, 35: 327,
    36: 33, 37: 160, 38: 158, 39: 133, 40: 153, 41: 144,
    42: 362, 43: 385, 44: 387, 45: 263, 46: 373, 47: 380,
    48: 61, 49: 185, 50: 40, 51: 0, 52: 267, 53: 409,
    54: 291, 55: 375, 56: 321, 57: 17, 58: 84, 59: 146,
    60: 78, 61: 191, 62: 13, 63: 415, 64: 308, 65: 324,
    66: 14, 67: 95,
}


def _flatten(values: Any) -> list[float]:
    return np.asarray(values, dtype=np.float32).reshape(-1).astype(float).tolist()


def _decode_image(encoded: str) -> np.ndarray:
    payload = base64.b64decode(encoded)
    image = cv2.imdecode(np.frombuffer(payload, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None or image.size == 0:
        raise ValueError("DECA could not decode the supplied image.")
    return cv2.cvtColor(image, cv2.COLOR_BGR2RGB)


def _resolve_box(face_box: dict[str, Any], width: int, height: int) -> tuple[float, float, float, float, float]:
    if not face_box:
        raise ValueError("DECA requires the selected live tracker face box.")
    left = float(face_box.get("left", 0.0))
    top = float(face_box.get("top", 0.0))
    right = float(face_box.get("right", 0.0))
    bottom = float(face_box.get("bottom", 0.0))
    if bool(face_box.get("normalized", True)):
        left *= width
        right *= width
        top *= height
        bottom *= height
    left = float(np.clip(left, 0.0, max(0.0, width - 1.0)))
    right = float(np.clip(right, left + 1.0, max(1.0, width)))
    top = float(np.clip(top, 0.0, max(0.0, height - 1.0)))
    bottom = float(np.clip(bottom, top + 1.0, max(1.0, height)))
    confidence = float(np.clip(face_box.get("confidence", 1.0), 0.01, 1.0))
    return left, top, right, bottom, confidence


def _crop_transform(face_box: dict[str, Any], width: int, height: int) -> tuple[np.ndarray, np.ndarray]:
    left, top, right, bottom, _ = _resolve_box(face_box, width, height)
    # Match DECA TestData.bbox2point exactly. Its encoder was trained with an
    # average-dimension square crop whose center is shifted slightly downward.
    old_size = ((right - left) + (bottom - top)) * 0.5
    center = np.array(
        [(left + right) * 0.5, (top + bottom) * 0.5 + old_size * 0.12],
        dtype=np.float32)
    size = old_size * CROP_SCALE
    half = size * 0.5
    source = np.array(
        [[center[0] - half, center[1] - half],
         [center[0] - half, center[1] + half],
         [center[0] + half, center[1] - half]],
        dtype=np.float32)
    destination = np.array(
        [[0.0, 0.0], [0.0, CROP_SIZE - 1.0], [CROP_SIZE - 1.0, 0.0]],
        dtype=np.float32)
    transform = cv2.getAffineTransform(source, destination)
    return transform, cv2.invertAffineTransform(transform)


def _crop_face(image: np.ndarray, face_box: dict[str, Any]) -> tuple[np.ndarray, np.ndarray]:
    height, width = image.shape[:2]
    transform, inverse_transform = _crop_transform(face_box, width, height)
    crop = cv2.warpAffine(
        image,
        transform,
        (CROP_SIZE, CROP_SIZE),
        flags=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_REFLECT_101)
    return crop, inverse_transform


def _to_original_image(points: np.ndarray, inverse_transform: np.ndarray) -> np.ndarray:
    normalized = np.asarray(points, dtype=np.float32).copy()
    xy = np.empty((normalized.shape[0], 2), dtype=np.float32)
    xy[:, 0] = (normalized[:, 0] + 1.0) * 0.5 * (CROP_SIZE - 1.0)
    xy[:, 1] = (normalized[:, 1] + 1.0) * 0.5 * (CROP_SIZE - 1.0)
    mapped = cv2.transform(xy.reshape(1, -1, 2), inverse_transform)[0]
    return np.column_stack((mapped, normalized[:, 2])).astype(np.float32)


def _resample_polyline(points: np.ndarray, sample_count: int) -> np.ndarray:
    segments = np.linalg.norm(np.diff(points, axis=0), axis=1)
    cumulative = np.concatenate(([0.0], np.cumsum(segments)))
    if cumulative[-1] <= 1e-6:
        raise ValueError("Identity fitting jaw contour had no measurable length.")
    targets = np.linspace(0.0, cumulative[-1], sample_count)
    result = np.empty((sample_count, 2), dtype=np.float32)
    for index, target in enumerate(targets):
        segment = min(int(np.searchsorted(cumulative, target, side="right")), len(points) - 1)
        start = max(0, segment - 1)
        length = cumulative[segment] - cumulative[start]
        amount = 0.0 if length <= 1e-6 else (target - cumulative[start]) / length
        result[index] = points[start] + (points[segment] - points[start]) * amount
    return result


def _identity_fit_targets(
        frame: dict[str, Any],
        assisted: bool = False) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    width = int(frame.get("frameWidthPixels", 0))
    height = int(frame.get("frameHeightPixels", 0))
    coordinates = np.asarray(frame.get("observedLandmarkCoordinates") or [], dtype=np.float32)
    if width <= 0 or height <= 0 or coordinates.size < 468 * 3:
        raise ValueError("Identity fitting requires the same-frame MediaPipe landmarks and source dimensions.")
    observed = coordinates.reshape(-1, 3)
    image_points = np.column_stack((observed[:, 0] * width, observed[:, 1] * height)).astype(np.float32)
    targets = np.zeros((68, 2), dtype=np.float32)
    mask = np.zeros(68, dtype=np.float32)
    weights = np.ones(68, dtype=np.float32)

    jaw = _resample_polyline(image_points[MEDIAPIPE_JAW], 17)
    targets[:17] = jaw
    mask[:17] = 1.0
    if assisted:
        # The embedded MediaPipe map does not contain the face oval. Preserve the
        # full jaw guide and give the lower center enough authority to shape the
        # chin instead of letting the much denser eye region dominate the fit.
        weights[:17] = np.asarray(
            [1.4, 1.5, 1.6, 1.8, 2.0, 2.2, 2.5, 2.8, 3.2,
             2.8, 2.5, 2.2, 2.0, 1.8, 1.6, 1.5, 1.4],
            dtype=np.float32)
    for flame_index, media_pipe_index in MEDIAPIPE_TO_FLAME_68.items():
        if media_pipe_index >= image_points.shape[0]:
            continue
        targets[flame_index] = image_points[media_pipe_index]
        mask[flame_index] = 1.0
        if 27 <= flame_index <= 47:
            weights[flame_index] = 1.35
        elif assisted and 48 <= flame_index <= 67:
            weights[flame_index] = 1.5

    transform, _ = _crop_transform(frame.get("faceBox") or {}, width, height)
    crop_points = cv2.transform(targets.reshape(1, -1, 2), transform)[0]
    targets = crop_points / float(CROP_SIZE - 1) * 2.0 - 1.0
    return targets, mask, weights


def _media_pipe_surface_targets(
        frame: dict[str, Any],
        landmark_indices: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    width = int(frame.get("frameWidthPixels", 0))
    height = int(frame.get("frameHeightPixels", 0))
    coordinates = np.asarray(frame.get("observedLandmarkCoordinates") or [], dtype=np.float32)
    if width <= 0 or height <= 0 or coordinates.size < 468 * 3:
        raise ValueError("MediaPipe surface fitting requires same-frame landmarks and source dimensions.")
    observed = coordinates.reshape(-1, 3)
    if landmark_indices.size == 0 or int(landmark_indices.max()) >= observed.shape[0]:
        raise ValueError("MediaPipe surface embedding references an unavailable landmark.")
    image_points = np.column_stack(
        (observed[landmark_indices, 0] * width,
         observed[landmark_indices, 1] * height)).astype(np.float32)
    transform, _ = _crop_transform(frame.get("faceBox") or {}, width, height)
    crop_points = cv2.transform(image_points.reshape(1, -1, 2), transform)[0]
    targets = crop_points / float(CROP_SIZE - 1) * 2.0 - 1.0
    weights = np.ones(landmark_indices.size, dtype=np.float32)
    # Mouth correspondences benefit the lower-face experiment without
    # suppressing any of the remaining embedded points.
    mouth_indices = {
        0, 13, 14, 17, 37, 39, 40, 61, 78, 80, 81, 82, 84, 87, 88,
        91, 95, 146, 178, 181, 185, 191, 267, 269, 270, 291, 308,
        310, 311, 312, 314, 317, 318, 321, 324, 375, 402, 405, 409, 415
    }
    for index, media_pipe_index in enumerate(landmark_indices):
        if int(media_pipe_index) in mouth_indices:
            weights[index] = 1.4
    return targets, weights


def _normalize_identity_vertices(vertices: np.ndarray) -> np.ndarray:
    values = np.asarray(vertices, dtype=np.float64)
    minimum = values.min(axis=0)
    maximum = values.max(axis=0)
    center = (minimum + maximum) * 0.5
    scale = max(1e-6, maximum[0] - minimum[0], maximum[1] - minimum[1])
    return (values - center) / scale


def _rotation_vector_to_abc(rotation_vector: np.ndarray) -> tuple[float, float, float]:
    matrix, _ = cv2.Rodrigues(np.asarray(rotation_vector, dtype=np.float64).reshape(3, 1))
    planar = math.sqrt(matrix[0, 0] * matrix[0, 0] + matrix[1, 0] * matrix[1, 0])
    if planar > 1e-7:
        a = math.atan2(matrix[2, 1], matrix[2, 2])
        b = math.atan2(-matrix[2, 0], planar)
        c = math.atan2(matrix[1, 0], matrix[0, 0])
    else:
        a = math.atan2(-matrix[1, 2], matrix[1, 1])
        b = math.atan2(-matrix[2, 0], planar)
        c = 0.0
    return tuple(math.degrees(value) for value in (a, b, c))


def _unique_edges(faces: np.ndarray) -> list[int]:
    edges: set[tuple[int, int]] = set()
    for a, b, c in np.asarray(faces, dtype=np.int32).reshape(-1, 3):
        for left, right in ((a, b), (b, c), (c, a)):
            edge = (int(left), int(right)) if left < right else (int(right), int(left))
            edges.add(edge)
    return [index for edge in sorted(edges) for index in edge]


class DecaWorker:
    def __init__(self, repository: str, model_path: str) -> None:
        sys.path.insert(0, repository)
        from decalib.models.encoders import ResnetEncoder
        from decalib.models.FLAME import FLAME
        from decalib.utils.config import cfg as deca_config

        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.parameter_names = list(deca_config.model.param_list)
        self.parameter_sizes = {
            name: int(deca_config.model.get("n_" + name))
            for name in self.parameter_names
        }
        parameter_count = sum(self.parameter_sizes.values())
        self.encoder = ResnetEncoder(outsize=parameter_count).to(self.device)
        self.flame = FLAME(deca_config.model).to(self.device)
        checkpoint = torch.load(model_path, map_location=self.device, weights_only=False)
        self._copy_state_dict(self.encoder.state_dict(), checkpoint["E_flame"])
        self.encoder.eval()
        self.flame.eval()
        faces = self.flame.faces_tensor.detach().cpu().numpy()
        self.topology = _unique_edges(faces)
        module_embedding_path = os.path.abspath(os.path.join(
            os.path.dirname(__file__), "..", "Resources", MEDIAPIPE_EMBEDDING_FILE))
        repository_embedding_path = os.path.join(repository, "data", MEDIAPIPE_EMBEDDING_FILE)
        embedding_path = (
            module_embedding_path
            if os.path.isfile(module_embedding_path)
            else repository_embedding_path)
        self.media_pipe_landmark_indices = np.empty(0, dtype=np.int64)
        self.media_pipe_face_vertices: torch.Tensor | None = None
        self.media_pipe_barycentric: torch.Tensor | None = None
        if os.path.isfile(embedding_path):
            embedding = np.load(embedding_path, allow_pickle=False)
            face_indices = np.asarray(embedding["lmk_face_idx"], dtype=np.int64).reshape(-1)
            barycentric = np.asarray(embedding["lmk_b_coords"], dtype=np.float32).reshape(-1, 3)
            landmark_indices = np.asarray(embedding["landmark_indices"], dtype=np.int64).reshape(-1)
            if face_indices.size != barycentric.shape[0] or face_indices.size != landmark_indices.size:
                raise ValueError("MediaPipe FLAME embedding arrays have inconsistent lengths.")
            if face_indices.size == 0 or int(face_indices.max()) >= faces.shape[0]:
                raise ValueError("MediaPipe FLAME embedding references an unavailable surface triangle.")
            self.media_pipe_landmark_indices = landmark_indices
            self.media_pipe_face_vertices = torch.as_tensor(
                faces[face_indices], dtype=torch.long, device=self.device)
            self.media_pipe_barycentric = torch.as_tensor(
                barycentric, dtype=torch.float32, device=self.device).reshape(1, -1, 3, 1)

    @staticmethod
    def _copy_state_dict(current: dict[str, Any], pretrained: dict[str, Any]) -> None:
        copied = 0
        for name, destination in current.items():
            source = pretrained.get(name)
            if source is None or tuple(source.shape) != tuple(destination.shape):
                continue
            destination.copy_(source)
            copied += 1
        if copied == 0:
            raise ValueError("DECA checkpoint did not contain compatible E_flame encoder weights.")

    def _decompose_code(self, parameters: torch.Tensor) -> dict[str, torch.Tensor]:
        result: dict[str, torch.Tensor] = {}
        offset = 0
        for name in self.parameter_names:
            size = self.parameter_sizes[name]
            result[name] = parameters[:, offset:offset + size]
            offset += size
        if "light" in result:
            result["light"] = result["light"].reshape(result["light"].shape[0], 9, 3)
        return result

    @staticmethod
    def _orthographic_projection(
            vertices: torch.Tensor,
            camera: torch.Tensor) -> torch.Tensor:
        camera = camera.reshape(-1, 1, 3)
        translated = torch.cat(
            (vertices[:, :, :2] + camera[:, :, 1:], vertices[:, :, 2:]),
            dim=2)
        return camera[:, :, :1] * translated

    def _embedded_media_pipe_surface(self, vertices: torch.Tensor) -> torch.Tensor:
        if self.media_pipe_face_vertices is None or self.media_pipe_barycentric is None:
            raise ValueError(
                "MediaPipe-assisted fitting requires data/mediapipe_landmark_embedding.npz.")
        triangle_vertices = vertices[:, self.media_pipe_face_vertices, :]
        return (triangle_vertices * self.media_pipe_barycentric).sum(dim=2)

    def _advance_recurrent_model(
            self,
            request: dict[str, Any],
            codedict: dict[str, torch.Tensor]) -> tuple[torch.Tensor, int, int, float, float, float]:
        """Advance model[n-1] against frame[n] without averaging coefficient vectors."""
        raw_shape = codedict["shape"].detach()
        shape_count = self.parameter_sizes["shape"]
        previous_values = np.asarray(
            request.get("previousModelShapeCoefficients") or [],
            dtype=np.float32)
        previous_sequence = max(0, int(request.get("previousModelSequenceNumber", 0)))
        initial_shape = (
            torch.as_tensor(previous_values, device=self.device).reshape(1, -1)
            if previous_values.size == shape_count
            else raw_shape.clone())
        anchor_values = np.asarray(
            request.get("identityAnchorShapeCoefficients") or [],
            dtype=np.float32)
        identity_anchor = (
            torch.as_tensor(anchor_values, device=self.device).reshape(1, -1)
            if anchor_values.size == shape_count
            else initial_shape.clone())

        frames = list(request.get("identityFrames") or [])
        if not frames:
            return initial_shape, previous_sequence + 1, 0, 0.0, 0.0, 0.0

        identity_fit_profile = str(
            request.get("identityFitProfile") or IDENTITY_FIT_FLAME_68).strip().lower()
        use_media_pipe_surface = identity_fit_profile == IDENTITY_FIT_MEDIAPIPE_SURFACE_ASSISTED
        target, mask, weights = _identity_fit_targets(frames[0], use_media_pipe_surface)
        target_tensor = torch.as_tensor(target, device=self.device).unsqueeze(0)
        mask_tensor = torch.as_tensor(mask, device=self.device).unsqueeze(0)
        weight_tensor = torch.as_tensor(weights, device=self.device).unsqueeze(0)
        combined_weights = mask_tensor * weight_tensor
        denominator = torch.clamp(combined_weights.sum() * 2.0, min=1.0)
        surface_target_tensor: torch.Tensor | None = None
        surface_weight_tensor: torch.Tensor | None = None
        surface_denominator: torch.Tensor | None = None
        if use_media_pipe_surface:
            if self.media_pipe_landmark_indices.size == 0:
                raise ValueError(
                    "MediaPipe-assisted fitting is selected but its FLAME surface embedding is missing.")
            surface_targets, surface_weights = _media_pipe_surface_targets(
                frames[0], self.media_pipe_landmark_indices)
            surface_target_tensor = torch.as_tensor(
                surface_targets, device=self.device).unsqueeze(0)
            surface_weight_tensor = torch.as_tensor(
                surface_weights, device=self.device).unsqueeze(0)
            surface_denominator = torch.clamp(
                surface_weight_tensor.sum() * 2.0, min=1.0)
        expression = codedict["exp"].detach()
        pose = codedict["pose"].detach()
        camera = codedict["cam"].detach()
        current_shape = torch.nn.Parameter(initial_shape.clone())
        optimizer = torch.optim.Adam([current_shape], lr=0.02)

        def projected_geometry() -> tuple[torch.Tensor, torch.Tensor | None]:
            vertices, landmarks2d, _ = self.flame(
                shape_params=current_shape,
                expression_params=expression,
                pose_params=pose)
            projected = self._orthographic_projection(landmarks2d, camera)
            projected = torch.stack((projected[:, :, 0], -projected[:, :, 1]), dim=2)
            if not use_media_pipe_surface:
                return projected, None
            embedded = self._embedded_media_pipe_surface(vertices)
            surface_projected = self._orthographic_projection(embedded, camera)
            surface_projected = torch.stack(
                (surface_projected[:, :, 0], -surface_projected[:, :, 1]), dim=2)
            return projected, surface_projected

        def fit_loss(
                projected: torch.Tensor,
                surface_projected: torch.Tensor | None) -> torch.Tensor:
            residual = functional.smooth_l1_loss(
                projected,
                target_tensor,
                beta=0.02,
                reduction="none")
            legacy_loss = (residual * combined_weights.unsqueeze(2)).sum() / denominator
            data_loss = legacy_loss
            if surface_projected is not None \
                    and surface_target_tensor is not None \
                    and surface_weight_tensor is not None \
                    and surface_denominator is not None:
                surface_residual = functional.smooth_l1_loss(
                    surface_projected,
                    surface_target_tensor,
                    beta=0.02,
                    reduction="none")
                surface_loss = (
                    surface_residual * surface_weight_tensor.unsqueeze(2)
                ).sum() / surface_denominator
                data_loss = legacy_loss * 0.5 + surface_loss * 0.5
            # The exact previous output is still this pass's initial state. The
            # separately persisted Standard Model anchor prevents one pose from
            # walking the person's identity arbitrarily far from accepted views.
            movement_penalty = (current_shape - identity_anchor).square().mean() * 0.002
            return data_loss + movement_penalty

        def rmse_percent(
                projected: torch.Tensor,
                surface_projected: torch.Tensor | None) -> float:
            squared = (projected - target_tensor).square().sum(dim=2)
            weighted_squared = (squared * combined_weights).sum()
            weight_total = combined_weights.sum()
            if surface_projected is not None \
                    and surface_target_tensor is not None \
                    and surface_weight_tensor is not None:
                surface_squared = (
                    surface_projected - surface_target_tensor).square().sum(dim=2)
                weighted_squared = weighted_squared + (
                    surface_squared * surface_weight_tensor).sum()
                weight_total = weight_total + surface_weight_tensor.sum()
            mean_squared = weighted_squared / torch.clamp(weight_total, min=1.0)
            jaw_width = torch.linalg.vector_norm(target_tensor[:, 16] - target_tensor[:, 0], dim=1)
            scale = torch.clamp(jaw_width.mean(), min=1e-4)
            return float((torch.sqrt(mean_squared) / scale * 100.0).detach().cpu())

        with torch.no_grad():
            initial_rmse = rmse_percent(*projected_geometry())
        maximum_iterations = int(np.clip(request.get("maximumIterations", 12), 4, 24))
        best_loss = float("inf")
        best_shape = initial_shape.detach().clone()
        completed_iterations = 0
        for iteration in range(maximum_iterations):
            optimizer.zero_grad(set_to_none=True)
            loss = fit_loss(*projected_geometry())
            if not torch.isfinite(loss):
                raise ValueError("Recurrent DECA/FLAME fitting produced a non-finite loss.")
            loss.backward()
            torch.nn.utils.clip_grad_norm_([current_shape], max_norm=5.0)
            optimizer.step()
            with torch.no_grad():
                current_shape.clamp_(-3.0, 3.0)
            completed_iterations = iteration + 1
            loss_value = float(loss.detach().cpu())
            if loss_value < best_loss:
                best_loss = loss_value
                best_shape = current_shape.detach().clone()

        with torch.no_grad():
            current_shape.copy_(best_shape)
            final_rmse = rmse_percent(*projected_geometry())
            coefficient_delta_rms = float(torch.sqrt(
                (best_shape - initial_shape).square().mean()).detach().cpu())
        return (
            best_shape,
            previous_sequence + 1,
            completed_iterations,
            initial_rmse,
            final_rmse,
            coefficient_delta_rms)

    def reconstruct(self, request: dict[str, Any]) -> dict[str, Any]:
        timings: dict[str, float] = {}
        started = time.perf_counter()
        image = _decode_image(str(request.get("imageBase64", "")))
        crop, inverse_transform = _crop_face(image, request.get("faceBox") or {})
        timings["decodeAndCrop"] = (time.perf_counter() - started) * 1000.0

        tensor = torch.from_numpy(crop).float().permute(2, 0, 1).unsqueeze(0) / 255.0
        tensor = tensor.to(self.device)
        inference_started = time.perf_counter()
        with torch.no_grad():
            codedict = self._decompose_code(self.encoder(tensor))
            raw_shape = codedict["shape"]
        maximum_passes = int(np.clip(request.get("pinnedStillMaximumPasses", 1), 1, 256))
        stable_passes_required = int(np.clip(
            request.get("pinnedStillStablePassesRequired", 1),
            1,
            maximum_passes))
        coefficient_delta_threshold = max(
            0.0,
            float(request.get("pinnedStillCoefficientDeltaThreshold", 0.0)))
        pass_request = dict(request)
        pinned_stable_passes = 0
        pinned_pass_count = 0
        fit_iterations = 0
        initial_rmse = 0.0
        final_rmse = 0.0
        coefficient_delta_rms = float("inf")
        current_shape = codedict["shape"].detach().clone()
        model_sequence = max(0, int(request.get("previousModelSequenceNumber", 0)))
        for pinned_pass in range(maximum_passes):
            (current_shape,
             model_sequence,
             pass_iterations,
             pass_initial_rmse,
             final_rmse,
             coefficient_delta_rms) = self._advance_recurrent_model(pass_request, codedict)
            if pinned_pass == 0:
                initial_rmse = pass_initial_rmse
            fit_iterations += pass_iterations
            pinned_pass_count = pinned_pass + 1
            if coefficient_delta_threshold > 0.0 \
                    and coefficient_delta_rms <= coefficient_delta_threshold:
                pinned_stable_passes += 1
            else:
                pinned_stable_passes = 0
            if pinned_stable_passes >= stable_passes_required:
                break
            pass_request["previousModelShapeCoefficients"] = _flatten(
                current_shape[0].detach().cpu().numpy())
            pass_request["previousModelSequenceNumber"] = model_sequence
        pinned_converged = (
            coefficient_delta_threshold > 0.0
            and pinned_stable_passes >= stable_passes_required)
        with torch.no_grad():
            vertices, landmarks2d, _ = self.flame(
                shape_params=current_shape,
                expression_params=codedict["exp"],
                pose_params=codedict["pose"])
            projected_vertices = self._orthographic_projection(vertices, codedict["cam"])
            projected_vertices[:, :, 1:] = -projected_vertices[:, :, 1:]
            projected_landmarks = self._orthographic_projection(landmarks2d, codedict["cam"])
            projected_landmarks[:, :, 1:] = -projected_landmarks[:, :, 1:]
            zero_expression = torch.zeros_like(codedict["exp"])
            zero_pose = torch.zeros_like(codedict["pose"])
            canonical, _, _ = self.flame(
                shape_params=current_shape,
                expression_params=zero_expression,
                pose_params=zero_pose)
            aligned_identity = self._orthographic_projection(vertices, codedict["cam"])
            aligned_identity[:, :, 1:] = -aligned_identity[:, :, 1:]
        if self.device == "cuda":
            torch.cuda.synchronize()
        timings["decaInference"] = (time.perf_counter() - inference_started) * 1000.0

        projected = _to_original_image(projected_vertices[0].detach().cpu().numpy(), inverse_transform)
        aligned_projected = _to_original_image(aligned_identity[0].detach().cpu().numpy(), inverse_transform)
        landmarks = _to_original_image(projected_landmarks[0].detach().cpu().numpy(), inverse_transform)
        canonical_vertices = canonical[0].detach().cpu().numpy()
        pose = codedict["pose"][0].detach().cpu().numpy()
        a, b, c = _rotation_vector_to_abc(pose[:3])
        timings["total"] = (time.perf_counter() - started) * 1000.0
        identity_fit_profile = str(
            request.get("identityFitProfile") or IDENTITY_FIT_FLAME_68).strip().lower()
        assisted = identity_fit_profile == IDENTITY_FIT_MEDIAPIPE_SURFACE_ASSISTED
        fit_description = (
            f"105 embedded MediaPipe surface points plus {len(MEDIAPIPE_JAW)} jaw points"
            if assisted
            else "the FLAME 68-point control map")
        return {
            "requestId": str(request.get("requestId", "")),
            "capturedAtUtc": str(request.get("capturedAtUtc", "")),
            "ok": True,
            "hasFace": True,
            "status": (
                f"DECA/FLAME reconstructed {projected.shape[0]:,} vertices on {self.device}; "
                f"model {model_sequence - 1} was the exact seed for model {model_sequence}; "
                f"fit used {fit_description}; "
                f"coefficient delta RMS {coefficient_delta_rms:.6f}; "
                f"{initial_rmse:.2f}% to {final_rmse:.2f}% landmark RMSE in "
                f"{fit_iterations} optimizer iterations across {pinned_pass_count} recurrent pass(es)."
            ),
            "backend": "DECA FLAME",
            "trustDecision": (
                "The previous complete FLAME model seeded this frame's MediaPipe-assisted surface fit; "
                "all bundled correspondences were active and no temporal coefficient averaging was applied."
                if assisted
                else "The previous complete FLAME model seeded this frame's fit; no temporal coefficient averaging was applied."
            ),
            "reconstructionConfidencePercent": 0.0,
            "pose": {
                "aRotationAroundXDegrees": a,
                "bRotationAroundYDegrees": b,
                "cRotationAroundZDegrees": c,
            },
            "projectedVertexCoordinates": _flatten(projected),
            "canonicalIdentityCoordinates": _flatten(canonical_vertices),
            "alignedIdentityProjectedCoordinates": _flatten(aligned_projected),
            "currentModelShapeCoefficients": _flatten(current_shape[0].detach().cpu().numpy()),
            "currentModelSequenceNumber": model_sequence,
            "currentModelCoefficientDeltaRms": coefficient_delta_rms,
            "pinnedStillConverged": pinned_converged,
            "pinnedStillPassCount": pinned_pass_count,
            "pinnedStillStablePassCount": pinned_stable_passes,
            "denseEdgeIndices": self.topology if bool(request.get("includeTopology")) else [],
            "sparseLandmarkCoordinates": _flatten(landmarks),
            "cameraMatrixCoefficients": _flatten(codedict["cam"][0].detach().cpu().numpy()),
            "shapeCoefficients": _flatten(current_shape[0].detach().cpu().numpy()),
            "expressionCoefficients": _flatten(codedict["exp"][0].detach().cpu().numpy()),
            "poseCoefficients": _flatten(codedict["pose"][0].detach().cpu().numpy()),
            "warnings": [
                "Recurrent identity fitting is experimental; each complete output becomes the exact initialization for the next loop.",
                ("MediaPipe surface assistance used all 105 bundled barycentric FLAME correspondences and the jaw guide."
                 if assisted
                 else "FLAME 68-point control fitting was used.")
            ],
            "timingsMilliseconds": timings,
        }

def _error_response(request: dict[str, Any], error: Exception) -> dict[str, Any]:
    return {
        "requestId": str(request.get("requestId", "")),
        "capturedAtUtc": str(request.get("capturedAtUtc", "")),
        "ok": False,
        "hasFace": False,
        "status": f"DECA recurrent reconstruction failed: {error}",
        "backend": "DECA FLAME",
        "trustDecision": "Do not retain this frame; DECA did not produce complete geometry.",
        "warnings": [str(error)],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--model", required=True)
    arguments = parser.parse_args()
    if not os.path.isfile(arguments.model):
        raise FileNotFoundError(arguments.model)
    # stdout is the JSON-lines protocol. Third-party model code is allowed to
    # print diagnostics only through stderr so it cannot corrupt a response.
    with contextlib.redirect_stdout(sys.stderr):
        worker = DecaWorker(os.path.abspath(arguments.repo), os.path.abspath(arguments.model))
    for line in sys.stdin:
        if not line.strip():
            continue
        request: dict[str, Any] = {}
        try:
            request = json.loads(line)
            with contextlib.redirect_stdout(sys.stderr):
                response = worker.reconstruct(request)
        except Exception as error:
            response = _error_response(request, error)
        sys.stdout.write(json.dumps(response, separators=(",", ":"), allow_nan=False) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
