namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFacePoseBucketProfile
{
    public string BucketId { get; set; } = "";

    public string Label { get; set; } = "";

    public string Description { get; set; } = "";

    public string CaptureInstruction { get; set; } = "";

    public bool PrimaryNeutralReference { get; set; }

    public bool RequiredForAvatarCoverage { get; set; } = true;

    public int SampleCount { get; set; }

    public double TotalWeight { get; set; }

    public PersonalMetricDistribution HeadYawDegrees { get; set; } = new();

    public PersonalMetricDistribution HeadPitchDegrees { get; set; } = new();

    public PersonalMetricDistribution HeadRollDegrees { get; set; } = new();

    public PersonalMetricDistribution FaceAspectRatio { get; set; } = new();

    public PersonalMetricDistribution EyeMidlineXToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution MouthCenterXToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution EyeToMouthXOffsetToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution InterEyeDistanceToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution MouthWidthToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution EyeMidlineYToFaceHeight { get; set; } = new();

    public PersonalMetricDistribution MouthCenterYToFaceHeight { get; set; } = new();

    public PersonalMetricDistribution AverageEyeOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution MouthOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution JawDroopRatio { get; set; } = new();

    public double AverageFaceReliabilityPercent { get; set; }

    public double AverageEyeReliabilityPercent { get; set; }

    public double AverageMouthReliabilityPercent { get; set; }

    public bool HasIdentityProfile =>
        SampleCount > 0
        && FaceAspectRatio.SampleCount > 0
        && InterEyeDistanceToFaceWidth.SampleCount > 0;
}

public sealed record PersonalFacePoseBucketDefinition(
    string BucketId,
    string Label,
    string Description,
    string CaptureInstruction,
    bool PrimaryNeutralReference,
    bool RequiredForAvatarCoverage);

public static class PersonalFacePoseBuckets
{
    public const string FrontNeutral = "front_neutral";
    public const string YawNegative = "yaw_negative";
    public const string YawPositive = "yaw_positive";
    public const string PitchNegative = "pitch_negative";
    public const string PitchPositive = "pitch_positive";
    public const string RollNegative = "roll_negative";
    public const string RollPositive = "roll_positive";

    private const double NeutralYawDegrees = 8d;
    private const double NeutralPitchDegrees = 7d;
    private const double NeutralRollDegrees = 7d;
    private const double TurnYawDegrees = 12d;
    private const double TiltPitchDegrees = 8d;
    private const double RollDegrees = 8d;

    public static IReadOnlyList<PersonalFacePoseBucketDefinition> Definitions { get; } =
    [
        new(
            FrontNeutral,
            "Front neutral pose",
            "Straight-on, low-C pose used as the safest identity and neutral-preview reference.",
            "Look straight at the camera with the face relaxed and glasses visible.",
            PrimaryNeutralReference: true,
            RequiredForAvatarCoverage: true),
        new(
            YawNegative,
            "Negative B turn",
            "Head turned toward the negative-B side; kept separate from neutral identity shape.",
            "Turn your head slowly toward the negative-B side while keeping both eyes visible.",
            PrimaryNeutralReference: false,
            RequiredForAvatarCoverage: true),
        new(
            YawPositive,
            "Positive B turn",
            "Head turned toward the positive-B side; kept separate from neutral identity shape.",
            "Turn your head slowly toward the positive-B side while keeping both eyes visible.",
            PrimaryNeutralReference: false,
            RequiredForAvatarCoverage: true),
        new(
            PitchNegative,
            "Negative A tilt",
            "Head tilted in the negative-A direction; used for pose-aware animation coverage.",
            "Tilt your head slightly in the negative-A direction with the mouth and glasses visible.",
            PrimaryNeutralReference: false,
            RequiredForAvatarCoverage: true),
        new(
            PitchPositive,
            "Positive A tilt",
            "Head tilted in the positive-A direction; used for pose-aware animation coverage.",
            "Tilt your head slightly in the positive-A direction with the mouth and glasses visible.",
            PrimaryNeutralReference: false,
            RequiredForAvatarCoverage: true),
        new(
            RollNegative,
            "Negative C tilt",
            "Head tilted toward the negative-C side; used to separate pose from face shape.",
            "Tilt your head slightly toward the negative-C side without leaving the frame.",
            PrimaryNeutralReference: false,
            RequiredForAvatarCoverage: true),
        new(
            RollPositive,
            "Positive C tilt",
            "Head tilted toward the positive-C side; used to separate pose from face shape.",
            "Tilt your head slightly toward the positive-C side without leaving the frame.",
            PrimaryNeutralReference: false,
            RequiredForAvatarCoverage: true)
    ];

    public static IReadOnlyList<PersonalFacePoseBucketDefinition> Classify(
        double yawDegrees,
        double pitchDegrees,
        double rollDegrees)
    {
        var buckets = new List<PersonalFacePoseBucketDefinition>();
        if (Math.Abs(yawDegrees) <= NeutralYawDegrees
            && Math.Abs(pitchDegrees) <= NeutralPitchDegrees
            && Math.Abs(rollDegrees) <= NeutralRollDegrees)
        {
            buckets.Add(Definition(FrontNeutral));
        }

        if (yawDegrees <= -TurnYawDegrees)
        {
            buckets.Add(Definition(YawNegative));
        }
        else if (yawDegrees >= TurnYawDegrees)
        {
            buckets.Add(Definition(YawPositive));
        }

        if (pitchDegrees <= -TiltPitchDegrees)
        {
            buckets.Add(Definition(PitchNegative));
        }
        else if (pitchDegrees >= TiltPitchDegrees)
        {
            buckets.Add(Definition(PitchPositive));
        }

        if (rollDegrees <= -RollDegrees)
        {
            buckets.Add(Definition(RollNegative));
        }
        else if (rollDegrees >= RollDegrees)
        {
            buckets.Add(Definition(RollPositive));
        }

        return buckets.Count > 0 ? buckets : [Definition(FrontNeutral)];
    }

    public static PersonalFacePoseBucketDefinition Definition(string bucketId)
    {
        return Definitions.FirstOrDefault(definition =>
                string.Equals(definition.BucketId, bucketId, StringComparison.OrdinalIgnoreCase))
            ?? Definitions[0];
    }
}
