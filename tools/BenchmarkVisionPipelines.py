#!/usr/bin/env python3
"""Repeatable MediaPipe and 3DDFA stage benchmark for Avatar Builder."""

import argparse
import base64
import csv
import json
import math
import statistics
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

import cv2


ROOT = Path(__file__).resolve().parents[1]
MEDIAPIPE_SCRIPT = ROOT / "Modules" / "Vision" / "MediaPipe" / "Sidecar" / "mediapipe_face_landmarker_sidecar.py"
MEDIAPIPE_MODEL = ROOT / "dependencies" / "vision" / "dense-face-landmarks" / "face_landmarker.task"
THREEDDFA_SCRIPT = ROOT / "Modules" / "Vision" / "Onnx" / "Sidecar" / "three_ddfa_onnx_sidecar.py"
THREEDDFA_REPO = ROOT / "dependencies" / "vision" / "3ddfa-onnx" / "3DDFA_V2"
THREEDDFA_CONFIG = THREEDDFA_REPO / "configs" / "mb1_120x120.yml"


class Sidecar:
    def __init__(self, command):
        self.process = subprocess.Popen(
            command,
            cwd=ROOT,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self.number = 0

    def request(self, payload):
        self.number += 1
        payload["requestId"] = f"benchmark-{self.number:06d}"
        started = time.perf_counter()
        self.process.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        if not line:
            raise RuntimeError(f"Sidecar exited with code {self.process.poll()}")
        response = json.loads(line)
        if response.get("requestId") != payload["requestId"]:
            raise RuntimeError("Sidecar response ID mismatch")
        return response, elapsed_ms

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("videos", nargs="+", type=Path)
    parser.add_argument("--python", type=Path, default=ROOT / ".venv" / "Scripts" / "python.exe")
    parser.add_argument("--output", type=Path, default=ROOT / "benchmark-results")
    parser.add_argument("--frames-per-video", type=int, default=24)
    parser.add_argument("--full-frames-per-video", type=int, default=2)
    parser.add_argument("--warmup-frames", type=int, default=2)
    parser.add_argument("--max-input-dimension", type=int, default=1920)
    parser.add_argument("--sampling", choices=("sequential", "spread"), default="sequential")
    return parser.parse_args()


def validate(args):
    required = [args.python, MEDIAPIPE_SCRIPT, MEDIAPIPE_MODEL, THREEDDFA_SCRIPT, THREEDDFA_REPO, THREEDDFA_CONFIG]
    missing = [str(path) for path in required if not path.exists()]
    missing.extend(str(path) for path in args.videos if not path.exists())
    if missing:
        raise FileNotFoundError("Missing benchmark dependency:\n" + "\n".join(missing))


def sample_frames(video_path, count, sampling):
    capture = cv2.VideoCapture(str(video_path))
    try:
        total = max(1, int(capture.get(cv2.CAP_PROP_FRAME_COUNT)))
        fps = max(1.0, float(capture.get(cv2.CAP_PROP_FPS) or 24.0))
        if sampling == "spread":
            indices = [round(index * (total - 1) / max(1, count - 1)) for index in range(count)]
        else:
            start = max(0, (total - count) // 2)
            indices = list(range(start, min(total, start + count)))
        frames = []
        for frame_index in indices:
            capture.set(cv2.CAP_PROP_POS_FRAMES, frame_index)
            ok, frame = capture.read()
            if ok and frame is not None:
                frames.append((frame_index, frame))
        return frames, fps
    finally:
        capture.release()


def resize_for_tracking(frame, maximum_dimension):
    height, width = frame.shape[:2]
    largest = max(width, height)
    if largest <= maximum_dimension:
        return frame
    scale = maximum_dimension / float(largest)
    return cv2.resize(frame, (round(width * scale), round(height * scale)), interpolation=cv2.INTER_AREA)


def encode(frame, quality):
    started = time.perf_counter()
    ok, payload = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, quality])
    if not ok:
        raise RuntimeError("OpenCV JPEG encode failed")
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    raw = payload.tobytes()
    return base64.b64encode(raw).decode("ascii"), len(raw), elapsed_ms


def mediapipe_box(response):
    points = response.get("landmarks") or []
    if not points:
        return None
    xs = [float(point["x"]) for point in points]
    ys = [float(point["y"]) for point in points]
    left, right = min(xs), max(xs)
    top, bottom = min(ys), max(ys)
    width = right - left
    height = bottom - top
    return {
        "left": max(0.0, left - width * 0.08),
        "top": max(0.0, top - height * 0.08),
        "right": min(1.0, right + width * 0.08),
        "bottom": min(1.0, bottom + height * 0.08),
        "normalized": True,
        "confidence": 1.0,
    }


def three_ddfa_tracking_box(response, frame):
    points = response.get("sparseLandmarks") or []
    if len(points) < 20:
        return response.get("faceBox")
    height, width = frame.shape[:2]
    xs = [float(point["x"]) for point in points]
    ys = [float(point["y"]) for point in points]
    left, right = min(xs), max(xs)
    top, bottom = min(ys), max(ys)
    face_width = right - left
    face_height = bottom - top
    return {
        "left": max(0.0, left - face_width * 0.14),
        "top": max(0.0, top - face_height * 0.30),
        "right": min(float(width), right + face_width * 0.14),
        "bottom": min(float(height), bottom + face_height * 0.12),
        "normalized": False,
        "confidence": max(0.01, min(1.0, float(response.get("reconstructionConfidencePercent") or 0.0) / 100.0)),
    }


def make_sample(video, frame_index, pipeline, response, round_trip_ms, encode_ms, payload_bytes, input_frame):
    timings = response.get("timingsMilliseconds") or {}
    height, width = input_frame.shape[:2]
    return {
        "video": str(video),
        "frameIndex": frame_index,
        "pipeline": pipeline,
        "ok": bool(response.get("ok")),
        "hasFace": bool(response.get("hasFace")),
        "inputWidth": width,
        "inputHeight": height,
        "payloadBytes": payload_bytes,
        "encodeMilliseconds": round(encode_ms, 4),
        "roundTripMilliseconds": round(round_trip_ms, 4),
        "sidecarStagesMilliseconds": timings,
        "denseVertexCount": int(response.get("denseVertexCount") or 0),
        "canonicalIdentityVertexCount": len(response.get("canonicalIdentityVertices") or []),
        "status": response.get("status", ""),
    }


def percentile(values, percent):
    if not values:
        return 0.0
    ordered = sorted(values)
    position = (len(ordered) - 1) * percent / 100.0
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def summarize(samples):
    summaries = []
    for pipeline in sorted({sample["pipeline"] for sample in samples}):
        selected = [sample for sample in samples if sample["pipeline"] == pipeline and not sample.get("warmup")]
        round_trips = [sample["roundTripMilliseconds"] for sample in selected]
        encodes = [sample["encodeMilliseconds"] for sample in selected]
        summaries.append({
            "pipeline": pipeline,
            "samples": len(selected),
            "faceLocks": sum(1 for sample in selected if sample["hasFace"]),
            "faceLockPercent": round(sum(1 for sample in selected if sample["hasFace"]) / max(1, len(selected)) * 100.0, 2),
            "medianRoundTripMilliseconds": round(statistics.median(round_trips), 4) if round_trips else 0.0,
            "p95RoundTripMilliseconds": round(percentile(round_trips, 95), 4),
            "maximumRoundTripMilliseconds": round(max(round_trips), 4) if round_trips else 0.0,
            "medianEncodeMilliseconds": round(statistics.median(encodes), 4) if encodes else 0.0,
            "effectiveFramesPerSecond": round(1000.0 / statistics.median(round_trips), 3) if round_trips and statistics.median(round_trips) > 0 else 0.0,
        })
    return summaries


def write_results(output, samples, summaries):
    output.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    json_path = output / f"vision-benchmark-{stamp}.json"
    csv_path = output / f"vision-benchmark-{stamp}.csv"
    json_path.write_text(json.dumps({"createdAtUtc": stamp, "summaries": summaries, "samples": samples}, indent=2), encoding="utf-8")
    columns = ["video", "frameIndex", "pipeline", "ok", "hasFace", "inputWidth", "inputHeight", "payloadBytes", "encodeMilliseconds", "roundTripMilliseconds", "denseVertexCount", "canonicalIdentityVertexCount", "status"]
    with csv_path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(samples)
    return json_path, csv_path


def main():
    args = parse_args()
    validate(args)
    media_pipe = Sidecar([str(args.python), str(MEDIAPIPE_SCRIPT), "--model", str(MEDIAPIPE_MODEL)])
    three_ddfa = Sidecar([str(args.python), str(THREEDDFA_SCRIPT), "--repo", str(THREEDDFA_REPO), "--config", str(THREEDDFA_CONFIG)])
    samples = []
    timestamp_ms = 1
    try:
        for video in args.videos:
            frames, video_fps = sample_frames(video, args.frames_per_video + args.warmup_frames, args.sampling)
            video_timestamp_base = timestamp_ms + 1000
            temporal_face_box = None
            full_indices = (
                set(index for index, _ in frames[-args.full_frames_per_video:])
                if args.full_frames_per_video > 0
                else set()
            )
            for position, (frame_index, source_frame) in enumerate(frames):
                tracking_frame = resize_for_tracking(source_frame, args.max_input_dimension)
                mp_base64, mp_bytes, mp_encode_ms = encode(tracking_frame, 88)
                mp_response, mp_round_trip = media_pipe.request({
                    "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
                    "timestampMilliseconds": max(timestamp_ms + 1, round(video_timestamp_base + frame_index / video_fps * 1000.0)),
                    "imageBase64": mp_base64,
                })
                timestamp_ms = max(timestamp_ms + 1, round(video_timestamp_base + frame_index / video_fps * 1000.0))
                sample = make_sample(video, frame_index, "MediaPipe-video", mp_response, mp_round_trip, mp_encode_ms, mp_bytes, tracking_frame)
                sample["warmup"] = position < args.warmup_frames
                samples.append(sample)

                ddfa_base64, ddfa_bytes, ddfa_encode_ms = encode(tracking_frame, 90)
                common = {
                    "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
                    "imageBase64": ddfa_base64,
                    "denseSampleStride": 72,
                }
                face_response, face_round_trip = three_ddfa.request({**common, "mode": "faceBoxOnly"})
                sample = make_sample(video, frame_index, "3DDFA-FaceBoxes-only", face_response, face_round_trip, ddfa_encode_ms, ddfa_bytes, tracking_frame)
                sample["warmup"] = position < args.warmup_frames
                samples.append(sample)

                tracking_response, tracking_round_trip = three_ddfa.request({**common, "mode": "tracking"})
                sample = make_sample(video, frame_index, "3DDFA-tracking-FaceBoxes", tracking_response, tracking_round_trip, ddfa_encode_ms, ddfa_bytes, tracking_frame)
                sample["warmup"] = position < args.warmup_frames
                samples.append(sample)

                refresh_temporal_box = temporal_face_box is None or position % 15 == 0
                temporal_response, temporal_round_trip = three_ddfa.request({
                    **common,
                    "mode": "tracking",
                    "faceBox": None if refresh_temporal_box else temporal_face_box,
                })
                temporal_face_box = three_ddfa_tracking_box(temporal_response, tracking_frame)
                sample = make_sample(video, frame_index, "3DDFA-tracking-temporal-box", temporal_response, temporal_round_trip, ddfa_encode_ms, ddfa_bytes, tracking_frame)
                sample["warmup"] = position < args.warmup_frames
                samples.append(sample)

                caller_box = mediapipe_box(mp_response)
                if caller_box is not None:
                    caller_response, caller_round_trip = three_ddfa.request({**common, "mode": "tracking", "faceBox": caller_box})
                    sample = make_sample(video, frame_index, "3DDFA-tracking-MediaPipe-box", caller_response, caller_round_trip, ddfa_encode_ms, ddfa_bytes, tracking_frame)
                    sample["warmup"] = position < args.warmup_frames
                    samples.append(sample)
                    if frame_index in full_indices:
                        full_response, full_round_trip = three_ddfa.request({**common, "mode": "full", "denseSampleStride": 1, "faceBox": caller_box})
                        sample = make_sample(video, frame_index, "3DDFA-full-MediaPipe-box", full_response, full_round_trip, ddfa_encode_ms, ddfa_bytes, tracking_frame)
                        sample["warmup"] = False
                        samples.append(sample)
    finally:
        media_pipe.close()
        three_ddfa.close()

    summaries = summarize(samples)
    json_path, csv_path = write_results(args.output, samples, summaries)
    print(json.dumps(summaries, indent=2))
    print(f"JSON: {json_path}")
    print(f"CSV: {csv_path}")


if __name__ == "__main__":
    main()
