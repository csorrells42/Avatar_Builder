namespace AvatarBuilder.Modules.Vision.Personalization;

public static class PersonalFaceSubject
{
    public const string DefaultSubjectId = "primary-subject";
    public const string DefaultSubjectDisplayName = "Primary subject";
    public const string ManualConfirmationMode = "manual-confirmed-subject";
    public const string AutomaticIdentityGateMode = "automatic-measurement-identity-gate";
    public const string UnknownSubjectPolicy = "reject-unknown-subject";
    public const string IdentityGatePolicy = "manual-confirmation-first; reject extreme warmup mismatches after a usable identity signature; reject likely non-subject after a strong measurement-only identity signature";
}
