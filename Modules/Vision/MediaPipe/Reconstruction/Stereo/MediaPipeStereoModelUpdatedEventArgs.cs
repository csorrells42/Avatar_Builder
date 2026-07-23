using System;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed record MediaPipeStereoModelUpdatedEventArgs(MediaPipeStereoFaceModel Model, TimeSpan ProcessingDuration, long SubmittedFrameCount, long ReplacedFrameCount);
