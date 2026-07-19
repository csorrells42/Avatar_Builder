namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed record PersonalFaceModelUpdate(
    bool Accepted,
    PersonalFaceModelRejectionKind RejectionKind,
    string Reason,
    double SampleWeight,
    PersonalFaceModel Model,
    PersonalFaceIdentityAnalysis? IdentityAnalysis = null);

public enum PersonalFaceModelRejectionKind
{
    None,
    NoFace,
    LowQuality,
    TrackingArtifact,
    EventLike,
    SubjectNotConfirmed,
    LearningStopped,
    SubjectMismatch,
    TrackingAuditHold
}
