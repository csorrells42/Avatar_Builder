# Vision Pipeline Baseline

Measured 2026-07-19 on the development workstation using the repository Python environment and the optimized persistent sidecars.

## Method

- Input clips: `20260715-195317.mp4`, `20260715-195342.mp4`, and `20260715-195400.mp4` from the Insta360 camera.
- Sampling: 24 sequential center-window frames per clip after three warm-up frames.
- Analysis input: 1920x1080 derived from the 4K source, matching the bounded live-analysis path while the camera preview remains independent.
- Full dense samples: one per clip because full reconstruction is an intentional observation operation, not the live face-box loop.
- Raw data: `D:\Avatar Builder Output\Benchmarks\Foundation-20260719\vision-benchmark-20260719T092401Z.json` and the adjacent CSV.

## Results

| Pipeline | Samples | Face lock | Median | P95 | Effective FPS |
| --- | ---: | ---: | ---: | ---: | ---: |
| MediaPipe video | 72 | 100% | 23.62 ms | 25.64 ms | 42.34 |
| 3DDFA tracking with MediaPipe box | 72 | 100% | 17.45 ms | 36.73 ms | 57.32 |
| 3DDFA tracking with temporal sparse-landmark box | 72 | 100% | 17.87 ms | 50.66 ms | 55.96 |
| 3DDFA FaceBoxes only | 72 | 100% | 91.33 ms | 114.14 ms | 10.95 |
| 3DDFA tracking with FaceBoxes acquisition | 72 | 100% | 99.32 ms | 132.33 ms | 10.07 |
| 3DDFA full dense with MediaPipe box | 3 | 100% | 881.77 ms | 884.11 ms | 1.13 |

The 3DDFA full-dense row has only three samples and is a workload estimate, not a stable tail-latency distribution. Re-run with more full frames before making a hardware-capacity decision from its p95.

## Operating Decision

- Keep MediaPipe as the default continuous live face/feature tracker.
- Use the MediaPipe caller box for MediaPipe-selected 3DDFA pose/reconstruction work.
- In 3DDFA-selected mode, reacquire with FaceBoxes periodically and use the latest sparse landmarks as the temporal box between acquisitions.
- Use 3DDFA tracking mode for pose and A/B/C alignment evidence.
- Request full dense output only for a logged-in, active, quality-gated persistent observation. Keep the per-profile A/B/C alignment audit diagnostic; 3DDFA owns avatar pose and depth.
- Treat camera preview FPS and vision-analysis throughput as independent measurements.

## Regression Rule

Future changes should be compared with the same clips and sequential protocol. Investigate a loss of face lock, a median regression greater than 15%, or a p95 regression greater than 25% before accepting the change. Live application timings are also written in ten-second batches to `Benchmarks\vision-pipeline-YYYYMMDD.csv` under the configured data folder.
