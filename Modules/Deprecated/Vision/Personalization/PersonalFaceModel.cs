namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceModel
{
    public string ModelVersion { get; set; } = "personal-face-model-v1";

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = PersonalFaceSubject.UnknownSubjectPolicy;

    public string IdentityGatePolicy { get; set; } = PersonalFaceSubject.IdentityGatePolicy;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int ObservedSamples { get; set; }

    public int AcceptedSamples { get; set; }

    public double AcceptedSampleWeight { get; set; }

    public PersonalFaceLearningStability LearningStability { get; set; } = new();

    public int RejectedSamples { get; set; }

    public int EventLikeRejectedSamples { get; set; }

    public int LowQualityRejectedSamples { get; set; }

    public int TrackingArtifactRejectedSamples { get; set; }

    public int NoFaceRejectedSamples { get; set; }

    public int SubjectMismatchRejectedSamples { get; set; }

    public double AcceptedRate => ObservedSamples <= 0 ? 0d : AcceptedSamples / (double)ObservedSamples;

    public double AverageFaceReliabilityPercent { get; set; }

    public double AverageFaceContinuityPercent { get; set; }

    public double AverageEyeReliabilityPercent { get; set; }

    public double AverageMouthReliabilityPercent { get; set; }

    public PersonalMetricDistribution FaceCenterX { get; set; } = new();

    public PersonalMetricDistribution FaceCenterY { get; set; } = new();

    public PersonalMetricDistribution FaceWidth { get; set; } = new();

    public PersonalMetricDistribution FaceHeight { get; set; } = new();

    public PersonalMetricDistribution ZApparentDistanceUnits { get; set; } = new();

    public PersonalMetricDistribution ZRelativeToReference { get; set; } = new();

    public PersonalMetricDistribution ZConfidencePercent { get; set; } = new();

    public int ZEstimateSamples { get; set; }

    public int ZCalibratedSamples { get; set; }

    public int ZCameraFovEstimatedSamples { get; set; }

    public int ZLearnedReferenceSamples { get; set; }

    public int ZApparentOnlySamples { get; set; }

    public PersonalMetricDistribution HeadYawDegrees { get; set; } = new();

    public PersonalMetricDistribution HeadPitchDegrees { get; set; } = new();

    public PersonalMetricDistribution HeadRollDegrees { get; set; } = new();

    public PersonalMetricDistribution LeftEyeOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution RightEyeOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution AverageEyeOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution EyeAgreementPercent { get; set; } = new();

    public PersonalMetricDistribution MouthOpeningRatio { get; set; } = new();

    public PersonalMetricDistribution JawDroopRatio { get; set; } = new();

    public PersonalMetricDistribution LeftBrowHeightRatio { get; set; } = new();

    public PersonalMetricDistribution RightBrowHeightRatio { get; set; } = new();

    public PersonalMetricDistribution AverageBrowHeightRatio { get; set; } = new();

    public PersonalMetricDistribution BrowAsymmetryPercent { get; set; } = new();

    public PersonalMetricDistribution MediaPipeAverageEyeBlinkPercent { get; set; } = new();

    public PersonalMetricDistribution MediaPipeJawOpenPercent { get; set; } = new();

    public PersonalMetricDistribution MediaPipeMouthClosePercent { get; set; } = new();

    public PersonalMetricDistribution EyeGlarePercent { get; set; } = new();

    public PersonalMetricDistribution EyeContrastPercent { get; set; } = new();

    public PersonalMetricDistribution EyeSharpnessPercent { get; set; } = new();

    public int IdentitySignatureSamples { get; set; }

    public PersonalMetricDistribution FaceAspectRatio { get; set; } = new();

    public PersonalMetricDistribution EyeMidlineXToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution MouthCenterXToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution EyeToMouthXOffsetToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution InterEyeDistanceToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution LeftEyeWidthToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution RightEyeWidthToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution MouthWidthToFaceWidth { get; set; } = new();

    public PersonalMetricDistribution EyeMidlineYToFaceHeight { get; set; } = new();

    public PersonalMetricDistribution MouthCenterYToFaceHeight { get; set; } = new();

    public PersonalMetricDistribution EyeToMouthYDistanceToFaceHeight { get; set; } = new();

    public PersonalFaceContourShapeProfile LeftEyeShape { get; set; } = new()
    {
        FeatureId = "left_eye_shape",
        Label = "Left eye contour shape",
        Closed = true,
        PointCount = 8
    };

    public PersonalFaceContourShapeProfile RightEyeShape { get; set; } = new()
    {
        FeatureId = "right_eye_shape",
        Label = "Right eye contour shape",
        Closed = true,
        PointCount = 8
    };

    public PersonalFaceContourShapeProfile OuterLipShape { get; set; } = new()
    {
        FeatureId = "outer_lip_shape",
        Label = "Outer lip contour shape",
        Closed = true,
        PointCount = 12
    };

    public PersonalFaceContourShapeProfile InnerLipShape { get; set; } = new()
    {
        FeatureId = "inner_lip_shape",
        Label = "Inner lip contour shape",
        Closed = true,
        PointCount = 10
    };

    public PersonalFaceContourShapeProfile JawShape { get; set; } = new()
    {
        FeatureId = "jaw_shape",
        Label = "Jaw contour shape",
        Closed = false,
        PointCount = 9
    };

    public PersonalFaceContourShapeProfile LeftBrowShape { get; set; } = new()
    {
        FeatureId = "left_brow_shape",
        Label = "Left brow 3D shape",
        Closed = false,
        PointCount = 10
    };

    public PersonalFaceContourShapeProfile RightBrowShape { get; set; } = new()
    {
        FeatureId = "right_brow_shape",
        Label = "Right brow 3D shape",
        Closed = false,
        PointCount = 10
    };

    public PersonalFaceContourShapeProfile NoseBridgeShape { get; set; } = new()
    {
        FeatureId = "nose_bridge_shape",
        Label = "Nose bridge 3D shape",
        Closed = false,
        PointCount = 10
    };

    public PersonalFaceContourShapeProfile NoseBaseShape { get; set; } = new()
    {
        FeatureId = "nose_base_shape",
        Label = "Nose base 3D shape",
        Closed = false,
        PointCount = 5
    };

    public PersonalFaceContourShapeProfile LeftCheekSurface { get; set; } = new()
    {
        FeatureId = "left_cheek_surface",
        Label = "Left cheek 3D surface",
        Closed = false,
        PointCount = 6
    };

    public PersonalFaceContourShapeProfile RightCheekSurface { get; set; } = new()
    {
        FeatureId = "right_cheek_surface",
        Label = "Right cheek 3D surface",
        Closed = false,
        PointCount = 6
    };

    public PersonalFaceContourShapeProfile ForeheadSurface { get; set; } = new()
    {
        FeatureId = "forehead_surface",
        Label = "Forehead 3D surface",
        Closed = false,
        PointCount = 9
    };

    public List<PersonalFacePoseBucketProfile> PoseBuckets { get; set; } = [];

    public int PossibleOneEyeArtifactSamples { get; set; }

    public int EyeArtifactSuppressedSamples { get; set; }

    public int LeftEyeReconstructedSamples { get; set; }

    public int RightEyeReconstructedSamples { get; set; }

    public int MouthReconstructedSamples { get; set; }

    public int MediaPipeEyeOpeningCorrectedSamples { get; set; }

    public int MediaPipeMouthOpeningCorrectedSamples { get; set; }

    public string Status
    {
        get
        {
            if (AcceptedSamples <= 0)
            {
                return ObservedSamples <= 0
                    ? "personal model waiting"
                    : $"personal model waiting; rejected {RejectedSamples} sample(s)";
            }

            var confidence = AcceptedSamples switch
            {
                >= 240 => "strong",
                >= 60 => "warming",
                _ => "starting"
            };
            return $"personal model {confidence}; {AcceptedSamples} accepted, {RejectedSamples} rejected";
        }
    }
}
