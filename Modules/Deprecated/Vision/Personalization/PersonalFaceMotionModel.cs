namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceMotionModel
{
    public string SchemaVersion { get; set; } = "personal-face-motion-model-v1";

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = PersonalFaceSubject.UnknownSubjectPolicy;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int ObservationCount { get; set; }

    public int UsableObservationCount { get; set; }

    public int PersonalModelAcceptedObservationCount { get; set; }

    public int MotionPairCount { get; set; }

    public double CapturedDurationSeconds { get; set; }

    public double AverageObservationQualityPercent { get; set; }

    public double AverageFaceReliabilityPercent { get; set; }

    public PersonalMetricDistribution HeadYawDegrees { get; set; } = new();

    public PersonalMetricDistribution HeadPitchDegrees { get; set; } = new();

    public PersonalMetricDistribution HeadRollDegrees { get; set; } = new();

    public PersonalMetricDistribution ZApparentDistanceUnits { get; set; } = new();

    public PersonalMetricDistribution ZRelativeToReference { get; set; } = new();

    public PersonalMetricDistribution ZConfidencePercent { get; set; } = new();

    public PersonalMetricDistribution AverageEyeOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution MouthOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution JawDroopRatio { get; set; } = new();

    public PersonalMetricDistribution AverageBrowHeightRatio { get; set; } = new();

    public PersonalMetricDistribution MediaPipeAverageEyeBlinkPercent { get; set; } = new();

    public PersonalMetricDistribution MediaPipeJawOpenPercent { get; set; } = new();

    public PersonalMetricDistribution MediaPipeMouthClosePercent { get; set; } = new();

    public PersonalMetricDistribution EyeClosingVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution EyeOpeningVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution MouthOpeningVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution MouthClosingVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution JawDroopVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution JawRecoveryVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution BrowRaiseVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution BrowLowerVelocityPerSecond { get; set; } = new();

    public PersonalMetricDistribution HeadYawVelocityDegreesPerSecond { get; set; } = new();

    public PersonalMetricDistribution HeadPitchVelocityDegreesPerSecond { get; set; } = new();

    public PersonalMetricDistribution HeadRollVelocityDegreesPerSecond { get; set; } = new();

    public PersonalMetricDistribution ZApparentVelocityUnitsPerSecond { get; set; } = new();

    public double EyeClosingWithMouthOpeningRate { get; set; }

    public double EyeClosingWithJawDroopRate { get; set; }

    public double MouthOpeningWithJawDroopRate { get; set; }

    public double EyeClosingWithBrowLoweringRate { get; set; }

    public int EyeArtifactSuppressedObservations { get; set; }

    public int EyeReconstructedObservations { get; set; }

    public int MouthReconstructedObservations { get; set; }

    public List<string> Warnings { get; set; } = [];

    public string StoragePolicy { get; set; } =
        "Measurement-only motion model. No raw frames, images, video, or full landmark meshes are stored here.";

    public string Status
    {
        get
        {
            if (UsableObservationCount <= 0)
            {
                return "motion model waiting for usable measurements";
            }

            var strength = MotionPairCount switch
            {
                >= 240 => "strong",
                >= 60 => "warming",
                _ => "starting"
            };
            return $"motion model {strength}; {UsableObservationCount} usable observations, {MotionPairCount} motion pairs";
        }
    }
}
