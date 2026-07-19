#!/usr/bin/env python3
"""Audit Avatar Builder's stored 3DDFA observations and derived identity mesh."""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import gzip
import json
import math
from pathlib import Path
from typing import Any, Iterable

import numpy as np


def read_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def read_observations(path: Path) -> dict[str, Any]:
    with gzip.open(path, "rt", encoding="utf-8-sig") as handle:
        return json.load(handle)


def vertices(item: dict[str, Any], key: str = "vertices") -> tuple[np.ndarray, np.ndarray]:
    ordered = sorted(item.get(key, []), key=lambda point: int(point["index"]))
    indices = np.asarray([int(point["index"]) for point in ordered], dtype=np.int32)
    points = np.asarray(
        [[float(point["x"]), float(point["y"]), float(point["z"])] for point in ordered],
        dtype=np.float64,
    )
    return indices, points


def center_and_scale(points: np.ndarray) -> np.ndarray:
    minimum = points.min(axis=0)
    maximum = points.max(axis=0)
    center = (minimum + maximum) * 0.5
    scale = max(0.0001, maximum[0] - minimum[0], maximum[1] - minimum[1])
    return (points - center) / scale


def csharp_inverse_abc(points: np.ndarray, a: float, b: float, c: float) -> np.ndarray:
    a, b, c = np.radians([-a, -b, -c])
    rx = np.asarray(
        [[1.0, 0.0, 0.0], [0.0, math.cos(a), -math.sin(a)], [0.0, math.sin(a), math.cos(a)]]
    )
    ry = np.asarray(
        [[math.cos(b), 0.0, math.sin(b)], [0.0, 1.0, 0.0], [-math.sin(b), 0.0, math.cos(b)]]
    )
    rz = np.asarray(
        [[math.cos(c), -math.sin(c), 0.0], [math.sin(c), math.cos(c), 0.0], [0.0, 0.0, 1.0]]
    )
    return points @ (rz @ ry @ rx).T


def kabsch_align(points: np.ndarray, target: np.ndarray) -> np.ndarray:
    source_center = points.mean(axis=0)
    target_center = target.mean(axis=0)
    source = points - source_center
    reference = target - target_center
    u, _, vt = np.linalg.svd(source.T @ reference)
    rotation = u @ vt
    if np.linalg.det(rotation) < 0.0:
        u[:, -1] *= -1.0
        rotation = u @ vt
    return source @ rotation + target_center


def weighted_mean(point_sets: list[np.ndarray], weights: np.ndarray) -> np.ndarray:
    return np.average(np.stack(point_sets), axis=0, weights=weights)


def ensemble_rms_percent(point_sets: list[np.ndarray], mean: np.ndarray, weights: np.ndarray) -> float:
    per_scan_squared = np.asarray([np.mean(np.sum((points - mean) ** 2, axis=1)) for points in point_sets])
    return float(math.sqrt(float(np.average(per_scan_squared, weights=weights))) * 100.0)


def generalized_procrustes(point_sets: list[np.ndarray], weights: np.ndarray) -> tuple[list[np.ndarray], np.ndarray]:
    mean = point_sets[0]
    aligned = point_sets
    for _ in range(4):
        aligned = [kabsch_align(points, mean) for points in point_sets]
        mean = weighted_mean(aligned, weights)
    return aligned, mean


def rms_percent(first: np.ndarray, second: np.ndarray) -> float:
    return float(math.sqrt(float(np.mean(np.sum((first - second) ** 2, axis=1)))) * 100.0)


def percentile(values: Iterable[float], fraction: float) -> float:
    materialized = list(values)
    return float(np.percentile(materialized, fraction * 100.0)) if materialized else 0.0


def finite_summary(values: Iterable[float]) -> dict[str, float]:
    materialized = np.asarray([value for value in values if math.isfinite(value)], dtype=np.float64)
    if materialized.size == 0:
        return {"minimum": 0.0, "median": 0.0, "maximum": 0.0, "range": 0.0}
    return {
        "minimum": round(float(materialized.min()), 6),
        "median": round(float(np.median(materialized)), 6),
        "maximum": round(float(materialized.max()), 6),
        "range": round(float(materialized.max() - materialized.min()), 6),
    }


def load_history(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    entries = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        try:
            entries.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return entries


def audit(profile: Path) -> dict[str, Any]:
    observation_set = read_observations(profile / "avatar_model_observations.json.gz")
    model = read_json(profile / "avatar_model.json")
    history = load_history(profile / "avatar_model_history.jsonl")
    alignment_path = profile / "Benchmarks" / "pose_alignment_summary.json"
    alignment = read_json(alignment_path) if alignment_path.exists() else {}
    observations = observation_set.get("observations", [])
    if not observations:
        raise RuntimeError("The profile has no stored observations to audit.")

    source_counts = collections.Counter(str(item.get("source", "")) for item in observations)
    request_ids = [str(item.get("requestId", "")) for item in observations]
    vertex_counts = [len(item.get("vertices", [])) for item in observations]
    index_sets = []
    basic_sets = []
    current_sets = []
    weights = []
    for item in observations:
        indices, raw = vertices(item)
        index_sets.append(indices)
        basic = center_and_scale(raw)
        basic_sets.append(basic)
        current_sets.append(
            csharp_inverse_abc(
                basic,
                float(item.get("aRotationAroundXDegrees", 0.0)),
                float(item.get("bRotationAroundYDegrees", 0.0)),
                float(item.get("cRotationAroundZDegrees", 0.0)),
            )
        )
        weights.append(max(0.0, float(item.get("identityWeightPercent", 0.0))) / 100.0)
    weight_array = np.asarray(weights, dtype=np.float64)
    if float(weight_array.sum()) <= 0.0:
        weight_array = np.ones(len(observations), dtype=np.float64)

    topology_count = len(observation_set.get("denseTopologyEdges", []))
    common_indices = all(np.array_equal(index_sets[0], item) for item in index_sets[1:])
    current_rounded = [np.round(points, 6) for points in current_sets]
    recomputed_model = np.round(weighted_mean(current_rounded, weight_array), 6)
    model_indices, stored_model = vertices(model.get("identity", {}), "meanDenseVertices")
    model_matches_indices = np.array_equal(index_sets[0], model_indices)
    model_recompute_rms = rms_percent(stored_model, recomputed_model) if model_matches_indices else math.inf
    model_recompute_max = (
        float(np.max(np.abs(stored_model - recomputed_model))) if model_matches_indices else math.inf
    )

    basic_mean = weighted_mean(basic_sets, weight_array)
    current_mean = weighted_mean(current_sets, weight_array)
    procrustes_sets, procrustes_mean = generalized_procrustes(basic_sets, weight_array)
    model_to_procrustes = rms_percent(kabsch_align(stored_model, procrustes_mean), procrustes_mean)
    per_scan_current = [rms_percent(points, current_mean) for points in current_sets]
    per_scan_procrustes = [rms_percent(points, procrustes_mean) for points in procrustes_sets]

    shape_coefficients = np.asarray([item.get("shapeCoefficients", []) for item in observations], dtype=np.float64)
    expression_coefficients = np.asarray(
        [item.get("expressionCoefficients", []) for item in observations], dtype=np.float64
    )
    shape_count = int(shape_coefficients.shape[1]) if shape_coefficients.ndim == 2 else 0
    expression_count = int(expression_coefficients.shape[1]) if expression_coefficients.ndim == 2 else 0
    shape_mean = shape_coefficients.mean(axis=0) if shape_count else np.asarray([])
    shape_std = shape_coefficients.std(axis=0) if shape_count else np.asarray([])
    expression_std = expression_coefficients.std(axis=0) if expression_count else np.asarray([])

    poses = {
        "aAroundXDegrees": finite_summary(float(item.get("aRotationAroundXDegrees", 0.0)) for item in observations),
        "bAroundYDegrees": finite_summary(float(item.get("bRotationAroundYDegrees", 0.0)) for item in observations),
        "cAroundZDegrees": finite_summary(float(item.get("cRotationAroundZDegrees", 0.0)) for item in observations),
    }
    warnings = [warning for item in observations for warning in item.get("warnings", [])]
    history_statuses = collections.Counter(str(item.get("status", "")) for item in history)
    history_movements = [float(item.get("overallVertexRmsFaceSpanPercent", 0.0)) for item in history]

    current_rms = ensemble_rms_percent(current_sets, current_mean, weight_array)
    no_rotation_rms = ensemble_rms_percent(basic_sets, basic_mean, weight_array)
    procrustes_rms = ensemble_rms_percent(procrustes_sets, procrustes_mean, weight_array)
    normalization_ratio = current_rms / max(procrustes_rms, 1e-9)
    rotation_effect_ratio = current_rms / max(no_rotation_rms, 1e-9)
    findings = []
    if model_recompute_rms <= 0.0002:
        findings.append("The stored identity mesh exactly matches the weighted mean produced by the current C# builder.")
    else:
        findings.append("The stored identity mesh does not reproduce from the stored observations; inspect persistence or build determinism.")
    if all("3DDFA" in source.upper() for source in source_counts):
        findings.append("Every stored identity observation declares a 3DDFA source; no MediaPipe vertex set is present.")
    if rotation_effect_ratio > 1.10:
        findings.append("Current A/B/C inverse rotation aligns the scans worse than centering/scaling alone.")
    if normalization_ratio > 1.50:
        findings.append("Current A/B/C normalization is materially worse than optimal rigid alignment and can blur or warp the accumulated face.")
    if len(observations) < 12:
        findings.append("The identity model is immature because it contains fewer than 12 accepted scans.")
    if len(set(vertex_counts)) != 1 or not common_indices:
        findings.append("Stored observations do not share one stable dense vertex topology.")
    if len(set(request_ids)) != len(request_ids):
        findings.append("Duplicate request IDs are present in the retained observation set.")

    return {
        "schemaVersion": "avatar-model-data-audit-v1",
        "evaluatedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "profileFolder": str(profile),
        "dataset": {
            "observationCount": len(observations),
            "capturedAtUtcMinimum": min(str(item.get("capturedAtUtc", "")) for item in observations),
            "capturedAtUtcMaximum": max(str(item.get("capturedAtUtc", "")) for item in observations),
            "sourceCounts": dict(source_counts),
            "vertexCounts": dict(collections.Counter(vertex_counts)),
            "topologyEdgeCount": topology_count,
            "commonVertexIndices": common_indices,
            "uniqueRequestIdCount": len(set(request_ids)),
            "warningCount": len(warnings),
            "warnings": sorted(set(warnings)),
        },
        "provenance": {
            "modelVertexCount": len(stored_model),
            "modelTopologyEdgeCount": len(model.get("identity", {}).get("topologyEdges", [])),
            "mediaPipeLandmarkSignaturePresent": any(count in (468, 478) for count in vertex_counts),
            "modelMatchesObservationIndices": model_matches_indices,
            "storedModelVsRecomputedRmsFaceSpanPercent": round(model_recompute_rms, 9),
            "storedModelVsRecomputedMaximumCoordinateDelta": round(model_recompute_max, 9),
        },
        "geometryAlignment": {
            "centerScaleOnlyEnsembleRmsFaceSpanPercent": round(no_rotation_rms, 6),
            "currentInverseAbcEnsembleRmsFaceSpanPercent": round(current_rms, 6),
            "optimalRigidEnsembleRmsFaceSpanPercent": round(procrustes_rms, 6),
            "currentVsNoRotationRmsRatio": round(rotation_effect_ratio, 6),
            "currentVsOptimalRmsRatio": round(normalization_ratio, 6),
            "storedModelVsOptimalRigidMeanRmsFaceSpanPercent": round(model_to_procrustes, 6),
            "perScanCurrentRmsFaceSpanPercent": [round(value, 6) for value in per_scan_current],
            "perScanOptimalRigidRmsFaceSpanPercent": [round(value, 6) for value in per_scan_procrustes],
        },
        "learningSignal": {
            "shapeCoefficientCount": shape_count,
            "meanShapeCoefficientL2Norm": round(float(np.linalg.norm(shape_mean)), 6),
            "meanShapeCoefficientStandardDeviation": round(float(shape_std.mean()), 6) if shape_count else 0.0,
            "maximumShapeCoefficientStandardDeviation": round(float(shape_std.max()), 6) if shape_count else 0.0,
            "expressionCoefficientCount": expression_count,
            "meanExpressionCoefficientStandardDeviation": round(float(expression_std.mean()), 6)
            if expression_count
            else 0.0,
            "identityWeightPercent": finite_summary(
                float(item.get("identityWeightPercent", 0.0)) for item in observations
            ),
            "reconstructionConfidencePercent": finite_summary(
                float(item.get("reconstructionConfidencePercent", 0.0)) for item in observations
            ),
            "poses": poses,
        },
        "history": {
            "entryCount": len(history),
            "statusCounts": dict(history_statuses),
            "latest": history[-1] if history else {},
            "meanModelMovementFaceSpanPercent": round(float(np.mean(history_movements)), 6)
            if history_movements
            else 0.0,
            "p95ModelMovementFaceSpanPercent": round(percentile(history_movements, 0.95), 6),
            "maximumModelMovementFaceSpanPercent": round(max(history_movements), 6)
            if history_movements
            else 0.0,
        },
        "poseAlignmentGate": alignment,
        "findings": findings,
    }


def markdown(report: dict[str, Any]) -> str:
    dataset = report["dataset"]
    provenance = report["provenance"]
    geometry = report["geometryAlignment"]
    learning = report["learningSignal"]
    history = report["history"]
    findings = "\n".join(f"- {item}" for item in report["findings"])
    return f"""# Avatar Model Data Audit

Evaluated `{report['evaluatedAtUtc']}` from `{report['profileFolder']}`.

## Findings

{findings}

## Dataset And Provenance

| Check | Result |
| --- | ---: |
| Stored observations | {dataset['observationCount']} |
| Observation sources | `{json.dumps(dataset['sourceCounts'], sort_keys=True)}` |
| Vertices per observation | `{json.dumps(dataset['vertexCounts'], sort_keys=True)}` |
| Dense topology edges | {dataset['topologyEdgeCount']} |
| Stable vertex indices | {dataset['commonVertexIndices']} |
| Unique request IDs | {dataset['uniqueRequestIdCount']} |
| MediaPipe-sized mesh present | {provenance['mediaPipeLandmarkSignaturePresent']} |
| Stored model vertices | {provenance['modelVertexCount']} |
| Model vs exact recomputation RMS | {provenance['storedModelVsRecomputedRmsFaceSpanPercent']:.9f}% of face span |

## Geometry Alignment

Lower is better. The optimal rigid result is a mathematical reference that rotates each complete 3DDFA scan without deforming it.

| Combination method | Ensemble RMS |
| --- | ---: |
| Center and scale only | {geometry['centerScaleOnlyEnsembleRmsFaceSpanPercent']:.6f}% |
| Current inverse A/B/C | {geometry['currentInverseAbcEnsembleRmsFaceSpanPercent']:.6f}% |
| Optimal rigid alignment | {geometry['optimalRigidEnsembleRmsFaceSpanPercent']:.6f}% |

Current A/B/C versus no rotation ratio: `{geometry['currentVsNoRotationRmsRatio']:.3f}`.
Current A/B/C versus optimal rigid ratio: `{geometry['currentVsOptimalRmsRatio']:.3f}`.

## Learning Signal

| Check | Result |
| --- | ---: |
| Shape coefficients | {learning['shapeCoefficientCount']} |
| Mean shape-vector L2 norm | {learning['meanShapeCoefficientL2Norm']:.6f} |
| Mean coefficient standard deviation | {learning['meanShapeCoefficientStandardDeviation']:.6f} |
| Maximum coefficient standard deviation | {learning['maximumShapeCoefficientStandardDeviation']:.6f} |
| Expression coefficients | {learning['expressionCoefficientCount']} |
| History entries | {history['entryCount']} |
| Maximum one-rebuild model movement | {history['maximumModelMovementFaceSpanPercent']:.6f}% |

## Interpretation Rule

- A 38,365-point observation with 3DDFA provenance is not a MediaPipe mesh.
- An exact recomputation match proves which stored observations produced the model, but does not prove the normalization is geometrically correct.
- Current inverse A/B/C RMS materially above no-rotation or optimal-rigid RMS indicates that pose removal is blurring or warping the accumulated identity.
- Fewer than 12 observations is an early model; fine proportions should not yet be treated as stable.
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", type=Path, help="AvatarSystem/People/<profile> folder")
    parser.add_argument("--output", type=Path, help="Folder for JSON and Markdown audit artifacts")
    args = parser.parse_args()
    report = audit(args.profile.resolve())
    output = args.output.resolve() if args.output else args.profile.resolve() / "Benchmarks"
    output.mkdir(parents=True, exist_ok=True)
    json_path = output / "avatar_model_data_audit.json"
    markdown_path = output / "avatar_model_data_audit.md"
    json_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    markdown_path.write_text(markdown(report), encoding="utf-8")
    print(json.dumps(report, indent=2))
    print(f"JSON: {json_path}")
    print(f"Markdown: {markdown_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
