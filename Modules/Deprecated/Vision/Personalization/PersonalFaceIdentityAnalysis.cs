namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceIdentityAnalysis
{
    public static PersonalFaceIdentityAnalysis NotReady { get; } = new()
    {
        Accepted = true,
        Reason = "identity signature not ready"
    };

    public bool HasMeasurement { get; set; }

    public bool AutoGateReady { get; set; }

    public bool WarmupStrongMismatchGateReady { get; set; }

    public bool Accepted { get; set; } = true;

    public string Reason { get; set; } = "";

    public double ConfidencePercent { get; set; }

    public int ComparedFeatureCount { get; set; }

    public int OutlierFeatureCount { get; set; }

    public List<PersonalFaceIdentityFeatureScore> FeatureScores { get; set; } = [];

    public string Status
    {
        get
        {
            if (!HasMeasurement)
            {
                return "identity signature waiting for measurable face geometry";
            }

            if (!Accepted)
            {
                var gate = AutoGateReady
                    ? "automatic gate"
                    : WarmupStrongMismatchGateReady ? "warmup protection" : "identity review";
                return $"identity signature rejected by {gate}; confidence {ConfidencePercent:0}%";
            }

            if (!AutoGateReady)
            {
                return $"identity signature warming; {ComparedFeatureCount} comparable feature(s)";
            }

            var label = ConfidencePercent switch
            {
                >= 70d => "matches",
                >= 45d => "uncertain",
                _ => "mismatch"
            };
            return $"identity signature {label}; confidence {ConfidencePercent:0}%";
        }
    }
}
