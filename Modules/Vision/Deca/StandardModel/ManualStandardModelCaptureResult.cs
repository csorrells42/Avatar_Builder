using AvatarBuilder.Modules.Storage.AvatarObservations;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed record ManualStandardModelCaptureResult(AvatarObservationWriteResult WriteResult, AvatarStandardModel? Model, AvatarStandardPoseSample? PoseSample);
