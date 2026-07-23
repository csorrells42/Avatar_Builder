using System;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed record MediaPipeGeometryModelUpdatedEventArgs(MediaPipeNormalizedFaceModel Model, TimeSpan ProcessingDuration, long SubmittedFrameCount, long ReplacedFrameCount);
