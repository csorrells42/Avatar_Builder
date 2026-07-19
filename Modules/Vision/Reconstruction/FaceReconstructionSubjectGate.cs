namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionSubjectGate
{
    public const string DefaultSubjectId = "chris";

    public const string DefaultSubjectDisplayName = "Chris";

    public const string ManualConfirmationMode = "manual-confirmation";

    public const string DefaultUnknownSubjectPolicy = "reject";

    public string SubjectId { get; set; } = DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = DefaultUnknownSubjectPolicy;

    public bool ManualSubjectConfirmed { get; set; }

    public double? IdentityConfidencePercent { get; set; }

    public string GateDecision { get; set; } = "paused";

    public string Reason { get; set; } = "subject not confirmed";

    public static FaceReconstructionSubjectGate FromAvatarProfile(
        string subjectId,
        string subjectDisplayName,
        bool manualSubjectConfirmed,
        double? identityConfidencePercent = null,
        string? reason = null)
    {
        var accepted = manualSubjectConfirmed
            && (identityConfidencePercent is null or >= 80d);
        return new FaceReconstructionSubjectGate
        {
            SubjectId = string.IsNullOrWhiteSpace(subjectId) ? DefaultSubjectId : subjectId,
            SubjectDisplayName = string.IsNullOrWhiteSpace(subjectDisplayName) ? DefaultSubjectDisplayName : subjectDisplayName,
            SubjectCollectionMode = ManualConfirmationMode,
            UnknownSubjectPolicy = DefaultUnknownSubjectPolicy,
            ManualSubjectConfirmed = manualSubjectConfirmed,
            IdentityConfidencePercent = identityConfidencePercent,
            GateDecision = accepted ? "accepted" : "paused",
            Reason = reason ?? (accepted
                ? "subject confirmed for avatar reconstruction"
                : "subject not confirmed strongly enough for avatar reconstruction")
        };
    }
}
