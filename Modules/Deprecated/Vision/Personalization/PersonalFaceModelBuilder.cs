using System.Windows;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceModelBuilder
{
    private static readonly int[] EyeA =
    [
        33, 246, 161, 160, 159, 158, 157, 173, 133, 155, 154, 153, 145, 144, 163, 7
    ];

    private static readonly int[] EyeB =
    [
        362, 398, 384, 385, 386, 387, 388, 466, 263, 249, 390, 373, 374, 380, 381, 382
    ];

    private static readonly int[] BrowA =
    [
        70, 63, 105, 66, 107, 55, 65, 52, 53, 46
    ];

    private static readonly int[] BrowB =
    [
        336, 296, 334, 293, 300, 285, 295, 282, 283, 276
    ];

    private static readonly int[] NoseBridge =
    [
        168, 6, 197, 195, 5, 4, 1, 19, 94, 2
    ];

    private static readonly int[] NoseBase =
    [
        98, 97, 2, 326, 327
    ];

    private static readonly int[] CheekA =
    [
        234, 93, 132, 58, 172, 136
    ];

    private static readonly int[] CheekB =
    [
        454, 323, 361, 288, 397, 365
    ];

    private static readonly int[] Forehead =
    [
        109, 67, 103, 54, 10, 284, 332, 297, 338
    ];

    private static readonly int[] OuterLip =
    [
        61, 185, 40, 39, 37, 0, 267, 269, 270, 409,
        291, 375, 321, 405, 314, 17, 84, 181, 91, 146
    ];

    private static readonly int[] InnerLip =
    [
        78, 191, 80, 81, 82, 13, 312, 311, 310, 415,
        308, 324, 318, 402, 317, 14, 87, 178, 88, 95
    ];

    private static readonly int[] Jaw =
    [
        234, 93, 132, 58, 172, 136, 150, 149, 176, 148, 152,
        377, 400, 378, 379, 365, 397, 288, 361, 323, 454
    ];

    private const double EventCueRejectThreshold = 28d;
    private const double EventTrendRejectThreshold = 24d;
    private const double MinimumOverallQualityPercent = 48d;
    private const double MinimumReliabilityPercent = 68d;
    private const double DistributionEmaAlpha = 0.035d;
    private const double MaximumStableSampleWeight = 1.25d;
    private const double EventLikeSampleWeightMultiplier = 0.45d;
    private const double AnchorTargetWeight = 180d;
    private const int MinimumIdentitySamplesForGeometryArtifactGate = 16;
    private const double GeometryArtifactMaximumPoseDegrees = 8d;
    private const double GeometryArtifactConfidenceThresholdPercent = 45d;

    private static readonly string[] GeometryArtifactFeatureNames =
    [
        "Face aspect",
        "Eye horizontal position",
        "Mouth horizontal position",
        "Eye-to-mouth horizontal offset",
        "Eye spacing / face width",
        "Left eye width / face width",
        "Right eye width / face width",
        "Mouth width / face width"
    ];

    private readonly DistributionAccumulator _faceCenterX = new();
    private readonly DistributionAccumulator _faceCenterY = new();
    private readonly DistributionAccumulator _faceWidth = new();
    private readonly DistributionAccumulator _faceHeight = new();
    private readonly DistributionAccumulator _zApparentDistanceUnits = new();
    private readonly DistributionAccumulator _zRelativeToReference = new();
    private readonly DistributionAccumulator _zConfidencePercent = new();
    private readonly DistributionAccumulator _headYaw = new();
    private readonly DistributionAccumulator _headPitch = new();
    private readonly DistributionAccumulator _headRoll = new();
    private readonly DistributionAccumulator _leftEyeOpening = new();
    private readonly DistributionAccumulator _rightEyeOpening = new();
    private readonly DistributionAccumulator _averageEyeOpening = new();
    private readonly DistributionAccumulator _eyeAgreement = new();
    private readonly DistributionAccumulator _mouthOpening = new();
    private readonly DistributionAccumulator _jawDroop = new();
    private readonly DistributionAccumulator _leftBrowHeight = new();
    private readonly DistributionAccumulator _rightBrowHeight = new();
    private readonly DistributionAccumulator _averageBrowHeight = new();
    private readonly DistributionAccumulator _browAsymmetry = new();
    private readonly DistributionAccumulator _mediaPipeAverageBlink = new();
    private readonly DistributionAccumulator _mediaPipeJawOpen = new();
    private readonly DistributionAccumulator _mediaPipeMouthClose = new();
    private readonly DistributionAccumulator _eyeGlare = new();
    private readonly DistributionAccumulator _eyeContrast = new();
    private readonly DistributionAccumulator _eyeSharpness = new();
    private readonly DistributionAccumulator _faceAspectRatio = new();
    private readonly DistributionAccumulator _eyeMidlineXToFaceWidth = new();
    private readonly DistributionAccumulator _mouthCenterXToFaceWidth = new();
    private readonly DistributionAccumulator _eyeToMouthXOffsetToFaceWidth = new();
    private readonly DistributionAccumulator _interEyeDistanceToFaceWidth = new();
    private readonly DistributionAccumulator _leftEyeWidthToFaceWidth = new();
    private readonly DistributionAccumulator _rightEyeWidthToFaceWidth = new();
    private readonly DistributionAccumulator _mouthWidthToFaceWidth = new();
    private readonly DistributionAccumulator _eyeMidlineYToFaceHeight = new();
    private readonly DistributionAccumulator _mouthCenterYToFaceHeight = new();
    private readonly DistributionAccumulator _eyeToMouthYDistanceToFaceHeight = new();
    private readonly ContourShapeAccumulator _leftEyeShape = new("left_eye_shape", "Left eye contour shape", pointCount: 8, closed: true);
    private readonly ContourShapeAccumulator _rightEyeShape = new("right_eye_shape", "Right eye contour shape", pointCount: 8, closed: true);
    private readonly ContourShapeAccumulator _outerLipShape = new("outer_lip_shape", "Outer lip contour shape", pointCount: 12, closed: true);
    private readonly ContourShapeAccumulator _innerLipShape = new("inner_lip_shape", "Inner lip contour shape", pointCount: 10, closed: true);
    private readonly ContourShapeAccumulator _jawShape = new("jaw_shape", "Jaw contour shape", pointCount: 9, closed: false);
    private readonly ContourShapeAccumulator _leftBrowShape = new("left_brow_shape", "Left brow 3D shape", pointCount: 10, closed: false);
    private readonly ContourShapeAccumulator _rightBrowShape = new("right_brow_shape", "Right brow 3D shape", pointCount: 10, closed: false);
    private readonly ContourShapeAccumulator _noseBridgeShape = new("nose_bridge_shape", "Nose bridge 3D shape", pointCount: 10, closed: false);
    private readonly ContourShapeAccumulator _noseBaseShape = new("nose_base_shape", "Nose base 3D shape", pointCount: 5, closed: false);
    private readonly ContourShapeAccumulator _leftCheekSurface = new("left_cheek_surface", "Left cheek 3D surface", pointCount: 6, closed: false);
    private readonly ContourShapeAccumulator _rightCheekSurface = new("right_cheek_surface", "Right cheek 3D surface", pointCount: 6, closed: false);
    private readonly ContourShapeAccumulator _foreheadSurface = new("forehead_surface", "Forehead 3D surface", pointCount: 9, closed: false);
    private readonly PoseBucketCollectionAccumulator _poseBuckets = new();

    private DateTime _createdAtUtc;
    private DateTime _updatedAtUtc;
    private int _observedSamples;
    private int _acceptedSamples;
    private int _rejectedSamples;
    private int _eventLikeRejectedSamples;
    private int _lowQualityRejectedSamples;
    private int _trackingArtifactRejectedSamples;
    private int _noFaceRejectedSamples;
    private int _subjectMismatchRejectedSamples;
    private int _identitySignatureSamples;
    private int _possibleOneEyeArtifactSamples;
    private int _eyeArtifactSuppressedSamples;
    private int _leftEyeReconstructedSamples;
    private int _rightEyeReconstructedSamples;
    private int _mouthReconstructedSamples;
    private int _mediaPipeEyeOpeningCorrectedSamples;
    private int _mediaPipeMouthOpeningCorrectedSamples;
    private int _zEstimateSamples;
    private int _zCalibratedSamples;
    private int _zCameraFovEstimatedSamples;
    private int _zLearnedReferenceSamples;
    private int _zApparentOnlySamples;
    private double _acceptedSampleWeight;
    private double _faceReliabilityTotal;
    private double _faceContinuityTotal;
    private double _eyeReliabilityTotal;
    private double _mouthReliabilityTotal;
    private string _subjectId;
    private string _subjectDisplayName;
    private string _subjectCollectionMode;

    public PersonalFaceModelBuilder(
        string subjectId = PersonalFaceSubject.DefaultSubjectId,
        string subjectDisplayName = PersonalFaceSubject.DefaultSubjectDisplayName,
        string subjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode)
    {
        _subjectId = string.IsNullOrWhiteSpace(subjectId) ? PersonalFaceSubject.DefaultSubjectId : subjectId.Trim();
        _subjectDisplayName = string.IsNullOrWhiteSpace(subjectDisplayName) ? PersonalFaceSubject.DefaultSubjectDisplayName : subjectDisplayName.Trim();
        _subjectCollectionMode = string.IsNullOrWhiteSpace(subjectCollectionMode) ? PersonalFaceSubject.ManualConfirmationMode : subjectCollectionMode.Trim();
    }

    public PersonalFaceModel CurrentModel => CreateModel();

    public void Reset()
    {
        LoadModel(new PersonalFaceModel
        {
            SubjectId = _subjectId,
            SubjectDisplayName = _subjectDisplayName,
            SubjectCollectionMode = _subjectCollectionMode,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    public void LoadModel(PersonalFaceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _subjectId = string.IsNullOrWhiteSpace(model.SubjectId)
            ? PersonalFaceSubject.DefaultSubjectId
            : model.SubjectId.Trim();
        _subjectDisplayName = string.IsNullOrWhiteSpace(model.SubjectDisplayName)
            ? PersonalFaceSubject.DefaultSubjectDisplayName
            : model.SubjectDisplayName.Trim();
        _subjectCollectionMode = string.IsNullOrWhiteSpace(model.SubjectCollectionMode)
            ? PersonalFaceSubject.ManualConfirmationMode
            : model.SubjectCollectionMode.Trim();

        _createdAtUtc = model.CreatedAtUtc;
        _updatedAtUtc = model.UpdatedAtUtc;
        _observedSamples = Math.Max(0, model.ObservedSamples);
        _acceptedSamples = Math.Max(0, model.AcceptedSamples);
        _rejectedSamples = Math.Max(0, model.RejectedSamples);
        _eventLikeRejectedSamples = Math.Max(0, model.EventLikeRejectedSamples);
        _lowQualityRejectedSamples = Math.Max(0, model.LowQualityRejectedSamples);
        _trackingArtifactRejectedSamples = Math.Max(0, model.TrackingArtifactRejectedSamples);
        _noFaceRejectedSamples = Math.Max(0, model.NoFaceRejectedSamples);
        _subjectMismatchRejectedSamples = Math.Max(0, model.SubjectMismatchRejectedSamples);
        _identitySignatureSamples = Math.Max(0, model.IdentitySignatureSamples);
        _possibleOneEyeArtifactSamples = Math.Max(0, model.PossibleOneEyeArtifactSamples);
        _eyeArtifactSuppressedSamples = Math.Max(0, model.EyeArtifactSuppressedSamples);
        _leftEyeReconstructedSamples = Math.Max(0, model.LeftEyeReconstructedSamples);
        _rightEyeReconstructedSamples = Math.Max(0, model.RightEyeReconstructedSamples);
        _mouthReconstructedSamples = Math.Max(0, model.MouthReconstructedSamples);
        _mediaPipeEyeOpeningCorrectedSamples = Math.Max(0, model.MediaPipeEyeOpeningCorrectedSamples);
        _mediaPipeMouthOpeningCorrectedSamples = Math.Max(0, model.MediaPipeMouthOpeningCorrectedSamples);
        _zEstimateSamples = Math.Max(0, model.ZEstimateSamples);
        _zCalibratedSamples = Math.Max(0, model.ZCalibratedSamples);
        _zCameraFovEstimatedSamples = Math.Max(0, model.ZCameraFovEstimatedSamples);
        _zLearnedReferenceSamples = Math.Max(0, model.ZLearnedReferenceSamples);
        _zApparentOnlySamples = Math.Max(0, model.ZApparentOnlySamples);
        _acceptedSampleWeight = Math.Max(0d, model.AcceptedSampleWeight);
        _faceReliabilityTotal = model.AverageFaceReliabilityPercent * _acceptedSampleWeight;
        _faceContinuityTotal = model.AverageFaceContinuityPercent * _acceptedSampleWeight;
        _eyeReliabilityTotal = model.AverageEyeReliabilityPercent * _acceptedSampleWeight;
        _mouthReliabilityTotal = model.AverageMouthReliabilityPercent * _acceptedSampleWeight;

        _faceCenterX.Load(model.FaceCenterX);
        _faceCenterY.Load(model.FaceCenterY);
        _faceWidth.Load(model.FaceWidth);
        _faceHeight.Load(model.FaceHeight);
        _zApparentDistanceUnits.Load(model.ZApparentDistanceUnits);
        _zRelativeToReference.Load(model.ZRelativeToReference);
        _zConfidencePercent.Load(model.ZConfidencePercent);
        _headYaw.Load(model.HeadYawDegrees);
        _headPitch.Load(model.HeadPitchDegrees);
        _headRoll.Load(model.HeadRollDegrees);
        _leftEyeOpening.Load(model.LeftEyeOpeningRatio);
        _rightEyeOpening.Load(model.RightEyeOpeningRatio);
        _averageEyeOpening.Load(model.AverageEyeOpeningRatio);
        _eyeAgreement.Load(model.EyeAgreementPercent);
        _mouthOpening.Load(model.MouthOpeningRatio);
        _jawDroop.Load(model.JawDroopRatio);
        _leftBrowHeight.Load(model.LeftBrowHeightRatio);
        _rightBrowHeight.Load(model.RightBrowHeightRatio);
        _averageBrowHeight.Load(model.AverageBrowHeightRatio);
        _browAsymmetry.Load(model.BrowAsymmetryPercent);
        _mediaPipeAverageBlink.Load(model.MediaPipeAverageEyeBlinkPercent);
        _mediaPipeJawOpen.Load(model.MediaPipeJawOpenPercent);
        _mediaPipeMouthClose.Load(model.MediaPipeMouthClosePercent);
        _eyeGlare.Load(model.EyeGlarePercent);
        _eyeContrast.Load(model.EyeContrastPercent);
        _eyeSharpness.Load(model.EyeSharpnessPercent);
        _faceAspectRatio.Load(model.FaceAspectRatio);
        _eyeMidlineXToFaceWidth.Load(model.EyeMidlineXToFaceWidth);
        _mouthCenterXToFaceWidth.Load(model.MouthCenterXToFaceWidth);
        _eyeToMouthXOffsetToFaceWidth.Load(model.EyeToMouthXOffsetToFaceWidth);
        _interEyeDistanceToFaceWidth.Load(model.InterEyeDistanceToFaceWidth);
        _leftEyeWidthToFaceWidth.Load(model.LeftEyeWidthToFaceWidth);
        _rightEyeWidthToFaceWidth.Load(model.RightEyeWidthToFaceWidth);
        _mouthWidthToFaceWidth.Load(model.MouthWidthToFaceWidth);
        _eyeMidlineYToFaceHeight.Load(model.EyeMidlineYToFaceHeight);
        _mouthCenterYToFaceHeight.Load(model.MouthCenterYToFaceHeight);
        _eyeToMouthYDistanceToFaceHeight.Load(model.EyeToMouthYDistanceToFaceHeight);
        _leftEyeShape.Load(model.LeftEyeShape);
        _rightEyeShape.Load(model.RightEyeShape);
        _outerLipShape.Load(model.OuterLipShape);
        _innerLipShape.Load(model.InnerLipShape);
        _jawShape.Load(model.JawShape);
        _leftBrowShape.Load(model.LeftBrowShape);
        _rightBrowShape.Load(model.RightBrowShape);
        _noseBridgeShape.Load(model.NoseBridgeShape);
        _noseBaseShape.Load(model.NoseBaseShape);
        _leftCheekSurface.Load(model.LeftCheekSurface);
        _rightCheekSurface.Load(model.RightCheekSurface);
        _foreheadSurface.Load(model.ForeheadSurface);
        _poseBuckets.Clear();
        _poseBuckets.Load(model.PoseBuckets);
    }

    public PersonalFaceModelUpdate Update(
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        FaceLockStabilityAnalysis stability,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis? trendAnalysis,
        HeadPoseEstimate? headPose = null,
        bool allowEventLikeMeasurements = false)
    {
        var timestamp = metrics.CapturedAtUtc != default
            ? metrics.CapturedAtUtc
            : frame.CapturedAtUtc != default ? frame.CapturedAtUtc : DateTime.UtcNow;
        if (_observedSamples == 0)
        {
            _createdAtUtc = timestamp;
        }

        _observedSamples++;
        _updatedAtUtc = timestamp;

        var gate = EvaluateGate(frame, metrics, stability, cueAnalysis, trendAnalysis, allowEventLikeMeasurements);
        if (!gate.Accepted)
        {
            _rejectedSamples++;
            CountRejectedGate(gate.Kind);

            return new PersonalFaceModelUpdate(false, gate.Kind, gate.Reason, 0d, CreateModel());
        }

        var currentModel = CreateModel();
        var identityMeasurement = PersonalFaceIdentityMeasurement.FromFrame(frame);
        var identityAnalysis = PersonalFaceIdentityAnalyzer.Analyze(currentModel, identityMeasurement);
        if (!identityAnalysis.Accepted)
        {
            _rejectedSamples++;
            CountRejectedGate(PersonalFaceModelRejectionKind.SubjectMismatch);
            return new PersonalFaceModelUpdate(
                false,
                PersonalFaceModelRejectionKind.SubjectMismatch,
                identityAnalysis.Reason,
                0d,
                CreateModel(),
                identityAnalysis);
        }

        var identityGeometryArtifactReason = GetIdentityGeometryArtifactReason(currentModel, identityAnalysis, metrics);
        if (!string.IsNullOrWhiteSpace(identityGeometryArtifactReason))
        {
            _rejectedSamples++;
            CountRejectedGate(PersonalFaceModelRejectionKind.TrackingArtifact);
            return new PersonalFaceModelUpdate(
                false,
                PersonalFaceModelRejectionKind.TrackingArtifact,
                identityGeometryArtifactReason,
                0d,
                CreateModel(),
                identityAnalysis);
        }

        _acceptedSamples++;
        var sampleWeight = CalculateSampleWeight(metrics, stability) * gate.SampleWeightMultiplier;
        var eyeWeight = sampleWeight * FeatureWeight(metrics.EyeMeasurementQualityPercent, stability.EyeReliabilityPercent);
        var mouthWeight = sampleWeight * FeatureWeight(metrics.MouthMeasurementQualityPercent, stability.MouthReliabilityPercent);
        _acceptedSampleWeight += sampleWeight;
        _faceReliabilityTotal += stability.CompositeReliabilityPercent * sampleWeight;
        _faceContinuityTotal += stability.FaceContinuityPercent * sampleWeight;
        _eyeReliabilityTotal += stability.EyeReliabilityPercent * sampleWeight;
        _mouthReliabilityTotal += stability.MouthReliabilityPercent * sampleWeight;

        var faceBounds = GetFaceBounds(frame);
        if (faceBounds is Rect bounds)
        {
            _faceCenterX.Add(bounds.Left + bounds.Width / 2d, sampleWeight);
            _faceCenterY.Add(bounds.Top + bounds.Height / 2d, sampleWeight);
            _faceWidth.Add(bounds.Width, sampleWeight);
            _faceHeight.Add(bounds.Height, sampleWeight);
        }

        AddZDistanceEvidence(headPose, sampleWeight);

        var storedHeadPose = CreateStoredHeadPose(metrics, headPose);
        _headYaw.Add(storedHeadPose.YawDegrees, sampleWeight);
        _headPitch.Add(storedHeadPose.PitchDegrees, sampleWeight);
        _headRoll.Add(storedHeadPose.RollDegrees, sampleWeight);
        _leftEyeOpening.Add(metrics.LeftEyeOpeningRatio, eyeWeight);
        _rightEyeOpening.Add(metrics.RightEyeOpeningRatio, eyeWeight);
        _averageEyeOpening.Add(metrics.AverageEyeOpeningRatio, eyeWeight);
        _eyeAgreement.Add(metrics.LeftEyeOpeningRatio.HasValue && metrics.RightEyeOpeningRatio.HasValue ? metrics.EyeAgreementPercent : null, eyeWeight);
        _mouthOpening.Add(metrics.MouthOpeningRatio, mouthWeight);
        _jawDroop.Add(metrics.JawDroopRatio, mouthWeight);
        var browWeight = sampleWeight * FeatureWeight(metrics.BrowMeasurementQualityPercent, stability.EyeReliabilityPercent);
        _leftBrowHeight.Add(metrics.LeftBrowHeightRatio, browWeight);
        _rightBrowHeight.Add(metrics.RightBrowHeightRatio, browWeight);
        _averageBrowHeight.Add(metrics.AverageBrowHeightRatio, browWeight);
        _browAsymmetry.Add(metrics.BrowAsymmetryPercent, browWeight);
        _mediaPipeAverageBlink.Add(metrics.MediaPipeAverageEyeBlinkPercent, eyeWeight);
        _mediaPipeJawOpen.Add(metrics.MediaPipeJawOpenPercent, mouthWeight);
        _mediaPipeMouthClose.Add(metrics.MediaPipeMouthClosePercent, mouthWeight);
        if (metrics.EyeImageQualityAvailable)
        {
            _eyeGlare.Add(metrics.EyeGlarePercent, eyeWeight);
            _eyeContrast.Add(metrics.EyeContrastPercent, eyeWeight);
            _eyeSharpness.Add(metrics.EyeSharpnessPercent, eyeWeight);
        }

        if (identityMeasurement.HasMeasurement)
        {
            _identitySignatureSamples++;
            var identityWeight = sampleWeight * 0.74d + Math.Clamp(metrics.TrackingConfidence, 0d, 1d) * 0.26d;
            _faceAspectRatio.Add(identityMeasurement.FaceAspectRatio, identityWeight);
            _eyeMidlineXToFaceWidth.Add(identityMeasurement.EyeMidlineXToFaceWidth, identityWeight);
            _mouthCenterXToFaceWidth.Add(identityMeasurement.MouthCenterXToFaceWidth, identityWeight);
            _eyeToMouthXOffsetToFaceWidth.Add(identityMeasurement.EyeToMouthXOffsetToFaceWidth, identityWeight);
            _interEyeDistanceToFaceWidth.Add(identityMeasurement.InterEyeDistanceToFaceWidth, identityWeight);
            _leftEyeWidthToFaceWidth.Add(identityMeasurement.LeftEyeWidthToFaceWidth, identityWeight);
            _rightEyeWidthToFaceWidth.Add(identityMeasurement.RightEyeWidthToFaceWidth, identityWeight);
            _mouthWidthToFaceWidth.Add(identityMeasurement.MouthWidthToFaceWidth, identityWeight);
            _eyeMidlineYToFaceHeight.Add(identityMeasurement.EyeMidlineYToFaceHeight, identityWeight);
            _mouthCenterYToFaceHeight.Add(identityMeasurement.MouthCenterYToFaceHeight, identityWeight);
            _eyeToMouthYDistanceToFaceHeight.Add(identityMeasurement.EyeToMouthYDistanceToFaceHeight, identityWeight);
        }

        _poseBuckets.Add(frame, metrics, stability, identityMeasurement, sampleWeight, storedHeadPose);

        if (faceBounds is Rect contourBounds)
        {
            if (!metrics.EyeArtifactSuppressed && !metrics.AnyEyeReconstructed)
            {
                if (!TryAddDenseEyeProfiles(frame, contourBounds, eyeWeight))
                {
                    _leftEyeShape.Add(frame.LeftEyeContour, contourBounds, eyeWeight);
                    _rightEyeShape.Add(frame.RightEyeContour, contourBounds, eyeWeight);
                }
            }

            if (!metrics.MouthReconstructed)
            {
                if (!TryAddDenseFeatureProfile(_outerLipShape, frame, contourBounds, OuterLip, mouthWeight))
                {
                    _outerLipShape.Add(frame.OuterLipContour, contourBounds, mouthWeight);
                }

                if (!TryAddDenseFeatureProfile(_innerLipShape, frame, contourBounds, InnerLip, mouthWeight))
                {
                    _innerLipShape.Add(frame.InnerLipContour, contourBounds, mouthWeight);
                }

                if (!TryAddDenseFeatureProfile(_jawShape, frame, contourBounds, Jaw, mouthWeight))
                {
                    _jawShape.Add(frame.JawContour, contourBounds, mouthWeight);
                }
            }

            if (frame.HasDenseMesh)
            {
                var surfaceWeight = sampleWeight * FeatureWeight(metrics.TrackingConfidence * 100d, stability.CompositeReliabilityPercent);
                AddDenseSurfaceProfiles(frame, contourBounds, browWeight, surfaceWeight);
            }
        }

        if (metrics.PossibleOneEyeArtifact)
        {
            _possibleOneEyeArtifactSamples++;
        }

        if (metrics.EyeArtifactSuppressed)
        {
            _eyeArtifactSuppressedSamples++;
        }

        if (metrics.LeftEyeReconstructed)
        {
            _leftEyeReconstructedSamples++;
        }

        if (metrics.RightEyeReconstructed)
        {
            _rightEyeReconstructedSamples++;
        }

        if (metrics.MouthReconstructed)
        {
            _mouthReconstructedSamples++;
        }

        if (metrics.MediaPipeEyeOpeningCorrected)
        {
            _mediaPipeEyeOpeningCorrectedSamples++;
        }

        if (metrics.MediaPipeMouthOpeningCorrected)
        {
            _mediaPipeMouthOpeningCorrectedSamples++;
        }

        return new PersonalFaceModelUpdate(true, PersonalFaceModelRejectionKind.None, gate.Reason, sampleWeight, CreateModel(), identityAnalysis);
    }

    private void AddZDistanceEvidence(HeadPoseEstimate? headPose, double sampleWeight)
    {
        if (headPose is not { HasFace: true } pose
            || pose.ApparentDistanceUnits is not > 0d)
        {
            return;
        }

        var zWeight = sampleWeight * Math.Clamp(pose.ZConfidencePercent / 100d, 0.25d, 1d);
        _zApparentDistanceUnits.Add(pose.ApparentDistanceUnits, zWeight);
        _zRelativeToReference.Add(pose.ZRelativeToReference, zWeight);
        _zConfidencePercent.Add(pose.ZConfidencePercent, sampleWeight);
        _zEstimateSamples++;

        if (pose.DistanceCalibrated)
        {
            _zCalibratedSamples++;
        }
        else if (pose.ZUsesCameraFov)
        {
            _zCameraFovEstimatedSamples++;
        }
        else if (pose.ZUsesLearnedReference)
        {
            _zLearnedReferenceSamples++;
        }
        else
        {
            _zApparentOnlySamples++;
        }
    }

    private void AddDenseSurfaceProfiles(
        FaceLandmarkFrame frame,
        Rect faceBounds,
        double browWeight,
        double surfaceWeight)
    {
        var browAIsLeft = AverageMeshX(frame.DenseMeshPoints, BrowA) <= AverageMeshX(frame.DenseMeshPoints, BrowB);
        TryAddDenseFeatureProfile(browAIsLeft ? _leftBrowShape : _rightBrowShape, frame, faceBounds, BrowA, browWeight);
        TryAddDenseFeatureProfile(browAIsLeft ? _rightBrowShape : _leftBrowShape, frame, faceBounds, BrowB, browWeight);

        TryAddDenseFeatureProfile(_noseBridgeShape, frame, faceBounds, NoseBridge, surfaceWeight);
        TryAddDenseFeatureProfile(_noseBaseShape, frame, faceBounds, NoseBase, surfaceWeight);
        TryAddDenseFeatureProfile(_foreheadSurface, frame, faceBounds, Forehead, surfaceWeight);

        var cheekAIsLeft = AverageMeshX(frame.DenseMeshPoints, CheekA) <= AverageMeshX(frame.DenseMeshPoints, CheekB);
        TryAddDenseFeatureProfile(cheekAIsLeft ? _leftCheekSurface : _rightCheekSurface, frame, faceBounds, CheekA, surfaceWeight);
        TryAddDenseFeatureProfile(cheekAIsLeft ? _rightCheekSurface : _leftCheekSurface, frame, faceBounds, CheekB, surfaceWeight);
    }

    private bool TryAddDenseEyeProfiles(
        FaceLandmarkFrame frame,
        Rect faceBounds,
        double weight)
    {
        if (!frame.HasDenseMesh
            || !TryGetDenseFeaturePoints(frame, EyeA, out var firstEye)
            || !TryGetDenseFeaturePoints(frame, EyeB, out var secondEye))
        {
            return false;
        }

        var eyeAIsLeft = firstEye.Average(static point => point.X) <= secondEye.Average(static point => point.X);
        (eyeAIsLeft ? _leftEyeShape : _rightEyeShape).Add(firstEye, faceBounds, weight);
        (eyeAIsLeft ? _rightEyeShape : _leftEyeShape).Add(secondEye, faceBounds, weight);
        return true;
    }

    private static bool TryAddDenseFeatureProfile(
        ContourShapeAccumulator accumulator,
        FaceLandmarkFrame frame,
        Rect faceBounds,
        IReadOnlyList<int> landmarkIndices,
        double weight)
    {
        if (!TryGetDenseFeaturePoints(frame, landmarkIndices, out var points))
        {
            return false;
        }

        accumulator.Add(points, faceBounds, weight);
        return true;
    }

    private static bool TryGetDenseFeaturePoints(
        FaceLandmarkFrame frame,
        IReadOnlyList<int> landmarkIndices,
        out List<FaceMeshLandmarkPoint> points)
    {
        points = [];
        if (!frame.HasDenseMesh)
        {
            return false;
        }

        var lookup = frame.DenseMeshPoints.ToDictionary(static point => point.Index);
        points = landmarkIndices
            .Where(lookup.ContainsKey)
            .Select(index => lookup[index])
            .ToList();
        return points.Count >= 3;
    }

    private static double AverageMeshX(IReadOnlyList<FaceMeshLandmarkPoint> points, IReadOnlyList<int> landmarkIndices)
    {
        var lookup = points.ToDictionary(static point => point.Index);
        var values = landmarkIndices
            .Where(lookup.ContainsKey)
            .Select(index => lookup[index].X)
            .ToList();
        return values.Count == 0 ? 0.5d : values.Average();
    }

    private void CountRejectedGate(PersonalFaceModelRejectionKind kind)
    {
        switch (kind)
        {
            case PersonalFaceModelRejectionKind.NoFace:
                _noFaceRejectedSamples++;
                break;
            case PersonalFaceModelRejectionKind.EventLike:
                _eventLikeRejectedSamples++;
                break;
            case PersonalFaceModelRejectionKind.TrackingArtifact:
                _trackingArtifactRejectedSamples++;
                break;
            case PersonalFaceModelRejectionKind.SubjectMismatch:
                _subjectMismatchRejectedSamples++;
                break;
            default:
                _lowQualityRejectedSamples++;
                break;
        }
    }

    private static PersonalFaceModelGate EvaluateGate(
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        FaceLockStabilityAnalysis stability,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis? trendAnalysis,
        bool allowEventLikeMeasurements)
    {
        if (!metrics.HasFace || !frame.HasFace)
        {
            return PersonalFaceModelGate.Reject(PersonalFaceModelRejectionKind.NoFace, "no face lock");
        }

        var trackingArtifactReason = GetTrackingArtifactReason(metrics);
        if (!string.IsNullOrWhiteSpace(trackingArtifactReason))
        {
            return PersonalFaceModelGate.Reject(
                PersonalFaceModelRejectionKind.TrackingArtifact,
                trackingArtifactReason);
        }

        if (metrics.OverallMeasurementQualityPercent < MinimumOverallQualityPercent)
        {
            return PersonalFaceModelGate.Reject(
                PersonalFaceModelRejectionKind.LowQuality,
                $"overall quality {metrics.OverallMeasurementQualityPercent:0}% below {MinimumOverallQualityPercent:0}%");
        }

        if (stability.SampleCount < 3 || stability.CompositeReliabilityPercent < MinimumReliabilityPercent)
        {
            return PersonalFaceModelGate.Reject(
                PersonalFaceModelRejectionKind.LowQuality,
                $"face reliability {stability.CompositeReliabilityPercent:0}% warming or below {MinimumReliabilityPercent:0}%");
        }

        if (!metrics.IsEyeMeasurementUsable && !metrics.MediaPipeAverageEyeBlinkPercent.HasValue)
        {
            return PersonalFaceModelGate.Reject(PersonalFaceModelRejectionKind.LowQuality, "eye measurement not usable");
        }

        var cueScore = cueAnalysis?.CompositeCuePercent ?? 0d;
        var trendScore = trendAnalysis?.TrendCuePercent ?? 0d;
        if (cueScore >= EventCueRejectThreshold || trendScore >= EventTrendRejectThreshold)
        {
            if (allowEventLikeMeasurements)
            {
                return PersonalFaceModelGate.AcceptEventLike(
                    $"accepted event-like face range/motion measurement; cue {cueScore:0}% or trend {trendScore:0}%");
            }

            return PersonalFaceModelGate.Reject(
                PersonalFaceModelRejectionKind.EventLike,
                $"event-like cue {cueScore:0}% or trend {trendScore:0}%");
        }

        return PersonalFaceModelGate.Accept;
    }

    private static string GetTrackingArtifactReason(FaceLandmarkMetrics metrics)
    {
        if (metrics.EyeArtifactSuppressed)
        {
            return "eye artifact suppression active; not learning personal face shape from this frame";
        }

        if (metrics.PossibleOneEyeArtifact)
        {
            return "possible one-eye glasses/contour artifact; not learning personal face shape from this frame";
        }

        if (metrics.AnyEyeReconstructed)
        {
            return "eye contour was reconstructed; wait for direct eyelid lock before avatar capture";
        }

        if (metrics.MouthReconstructed)
        {
            return "mouth contour was reconstructed; wait for direct lip/jaw lock before avatar capture";
        }

        return "";
    }

    private static string GetIdentityGeometryArtifactReason(
        PersonalFaceModel model,
        PersonalFaceIdentityAnalysis identityAnalysis,
        FaceLandmarkMetrics metrics)
    {
        if (!identityAnalysis.HasMeasurement
            || model.IdentitySignatureSamples < MinimumIdentitySamplesForGeometryArtifactGate
            || identityAnalysis.ComparedFeatureCount < 5
            || !IsNearNeutralPose(metrics))
        {
            return "";
        }

        var highRiskOutliers = identityAnalysis.FeatureScores
            .Where(static score => score.IsOutlier && IsGeometryArtifactFeature(score.Name))
            .OrderBy(static score => score.ConfidencePercent)
            .ToList();
        if (highRiskOutliers.Count < 3
            || identityAnalysis.OutlierFeatureCount < 4
            || identityAnalysis.ConfidencePercent >= GeometryArtifactConfidenceThresholdPercent)
        {
            return "";
        }

        var weakFeatureEvidence =
            metrics.EyeMeasurementQualityPercent < 72d
            || metrics.MouthMeasurementQualityPercent < 72d
            || metrics.EyeAgreementPercent < 72d
            || metrics.BrowMeasurementQualityPercent < 55d;
        if (!weakFeatureEvidence && highRiskOutliers.Count < 4)
        {
            return "";
        }

        var names = string.Join(
            ", ",
            highRiskOutliers
                .Select(static score => score.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4));
        return $"front-facing feature geometry has {identityAnalysis.OutlierFeatureCount} identity outlier(s) ({names}); likely occlusion or bad landmark lock, not learning personal face shape from this frame";
    }

    private static bool IsGeometryArtifactFeature(string name)
    {
        return GeometryArtifactFeatureNames.Any(feature =>
            name.Equals(feature, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNearNeutralPose(FaceLandmarkMetrics metrics)
    {
        return Math.Abs(metrics.HeadYawDegrees) <= GeometryArtifactMaximumPoseDegrees
            && Math.Abs(metrics.HeadPitchDegrees) <= GeometryArtifactMaximumPoseDegrees
            && Math.Abs(metrics.HeadRollDegrees) <= GeometryArtifactMaximumPoseDegrees;
    }

    private PersonalFaceModel CreateModel()
    {
        return new PersonalFaceModel
        {
            SubjectId = _subjectId,
            SubjectDisplayName = _subjectDisplayName,
            SubjectCollectionMode = _subjectCollectionMode,
            UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
            IdentityGatePolicy = PersonalFaceSubject.IdentityGatePolicy,
            CreatedAtUtc = _createdAtUtc,
            UpdatedAtUtc = _updatedAtUtc,
            ObservedSamples = _observedSamples,
            AcceptedSamples = _acceptedSamples,
            AcceptedSampleWeight = _acceptedSampleWeight,
            LearningStability = BuildLearningStability(_acceptedSampleWeight, GetMinimumTrackedDistributionWeight()),
            RejectedSamples = _rejectedSamples,
            EventLikeRejectedSamples = _eventLikeRejectedSamples,
            LowQualityRejectedSamples = _lowQualityRejectedSamples,
            TrackingArtifactRejectedSamples = _trackingArtifactRejectedSamples,
            NoFaceRejectedSamples = _noFaceRejectedSamples,
            SubjectMismatchRejectedSamples = _subjectMismatchRejectedSamples,
            AverageFaceReliabilityPercent = Average(_faceReliabilityTotal, _acceptedSampleWeight),
            AverageFaceContinuityPercent = Average(_faceContinuityTotal, _acceptedSampleWeight),
            AverageEyeReliabilityPercent = Average(_eyeReliabilityTotal, _acceptedSampleWeight),
            AverageMouthReliabilityPercent = Average(_mouthReliabilityTotal, _acceptedSampleWeight),
            FaceCenterX = _faceCenterX.ToModel(),
            FaceCenterY = _faceCenterY.ToModel(),
            FaceWidth = _faceWidth.ToModel(),
            FaceHeight = _faceHeight.ToModel(),
            ZApparentDistanceUnits = _zApparentDistanceUnits.ToModel(),
            ZRelativeToReference = _zRelativeToReference.ToModel(),
            ZConfidencePercent = _zConfidencePercent.ToModel(),
            ZEstimateSamples = _zEstimateSamples,
            ZCalibratedSamples = _zCalibratedSamples,
            ZCameraFovEstimatedSamples = _zCameraFovEstimatedSamples,
            ZLearnedReferenceSamples = _zLearnedReferenceSamples,
            ZApparentOnlySamples = _zApparentOnlySamples,
            HeadYawDegrees = _headYaw.ToModel(),
            HeadPitchDegrees = _headPitch.ToModel(),
            HeadRollDegrees = _headRoll.ToModel(),
            LeftEyeOpeningRatio = _leftEyeOpening.ToModel(),
            RightEyeOpeningRatio = _rightEyeOpening.ToModel(),
            AverageEyeOpeningRatio = _averageEyeOpening.ToModel(),
            EyeAgreementPercent = _eyeAgreement.ToModel(),
            MouthOpeningRatio = _mouthOpening.ToModel(),
            JawDroopRatio = _jawDroop.ToModel(),
            LeftBrowHeightRatio = _leftBrowHeight.ToModel(),
            RightBrowHeightRatio = _rightBrowHeight.ToModel(),
            AverageBrowHeightRatio = _averageBrowHeight.ToModel(),
            BrowAsymmetryPercent = _browAsymmetry.ToModel(),
            MediaPipeAverageEyeBlinkPercent = _mediaPipeAverageBlink.ToModel(),
            MediaPipeJawOpenPercent = _mediaPipeJawOpen.ToModel(),
            MediaPipeMouthClosePercent = _mediaPipeMouthClose.ToModel(),
            EyeGlarePercent = _eyeGlare.ToModel(),
            EyeContrastPercent = _eyeContrast.ToModel(),
            EyeSharpnessPercent = _eyeSharpness.ToModel(),
            IdentitySignatureSamples = _identitySignatureSamples,
            FaceAspectRatio = _faceAspectRatio.ToModel(),
            EyeMidlineXToFaceWidth = _eyeMidlineXToFaceWidth.ToModel(),
            MouthCenterXToFaceWidth = _mouthCenterXToFaceWidth.ToModel(),
            EyeToMouthXOffsetToFaceWidth = _eyeToMouthXOffsetToFaceWidth.ToModel(),
            InterEyeDistanceToFaceWidth = _interEyeDistanceToFaceWidth.ToModel(),
            LeftEyeWidthToFaceWidth = _leftEyeWidthToFaceWidth.ToModel(),
            RightEyeWidthToFaceWidth = _rightEyeWidthToFaceWidth.ToModel(),
            MouthWidthToFaceWidth = _mouthWidthToFaceWidth.ToModel(),
            EyeMidlineYToFaceHeight = _eyeMidlineYToFaceHeight.ToModel(),
            MouthCenterYToFaceHeight = _mouthCenterYToFaceHeight.ToModel(),
            EyeToMouthYDistanceToFaceHeight = _eyeToMouthYDistanceToFaceHeight.ToModel(),
            LeftEyeShape = _leftEyeShape.ToModel(),
            RightEyeShape = _rightEyeShape.ToModel(),
            OuterLipShape = _outerLipShape.ToModel(),
            InnerLipShape = _innerLipShape.ToModel(),
            JawShape = _jawShape.ToModel(),
            LeftBrowShape = _leftBrowShape.ToModel(),
            RightBrowShape = _rightBrowShape.ToModel(),
            NoseBridgeShape = _noseBridgeShape.ToModel(),
            NoseBaseShape = _noseBaseShape.ToModel(),
            LeftCheekSurface = _leftCheekSurface.ToModel(),
            RightCheekSurface = _rightCheekSurface.ToModel(),
            ForeheadSurface = _foreheadSurface.ToModel(),
            PoseBuckets = _poseBuckets.ToModel(),
            PossibleOneEyeArtifactSamples = _possibleOneEyeArtifactSamples,
            EyeArtifactSuppressedSamples = _eyeArtifactSuppressedSamples,
            LeftEyeReconstructedSamples = _leftEyeReconstructedSamples,
            RightEyeReconstructedSamples = _rightEyeReconstructedSamples,
            MouthReconstructedSamples = _mouthReconstructedSamples,
            MediaPipeEyeOpeningCorrectedSamples = _mediaPipeEyeOpeningCorrectedSamples,
            MediaPipeMouthOpeningCorrectedSamples = _mediaPipeMouthOpeningCorrectedSamples
        };
    }

    private double GetMinimumTrackedDistributionWeight()
    {
        var weights = new[]
            {
                _acceptedSampleWeight,
                _faceCenterX.TotalWeight,
                _faceCenterY.TotalWeight,
                _faceWidth.TotalWeight,
                _faceHeight.TotalWeight,
                _zApparentDistanceUnits.TotalWeight,
                _zRelativeToReference.TotalWeight,
                _headYaw.TotalWeight,
                _headPitch.TotalWeight,
                _headRoll.TotalWeight,
                _averageEyeOpening.TotalWeight,
                _mouthOpening.TotalWeight,
                _jawDroop.TotalWeight,
                _averageBrowHeight.TotalWeight,
                _faceAspectRatio.TotalWeight,
                _eyeMidlineXToFaceWidth.TotalWeight,
                _mouthCenterXToFaceWidth.TotalWeight,
                _eyeToMouthXOffsetToFaceWidth.TotalWeight,
                _interEyeDistanceToFaceWidth.TotalWeight,
                _mouthWidthToFaceWidth.TotalWeight,
                _eyeMidlineYToFaceHeight.TotalWeight,
                _mouthCenterYToFaceHeight.TotalWeight,
                _leftEyeShape.TotalWeight,
                _rightEyeShape.TotalWeight,
                _outerLipShape.TotalWeight,
                _innerLipShape.TotalWeight,
                _jawShape.TotalWeight,
                _leftBrowShape.TotalWeight,
                _rightBrowShape.TotalWeight,
                _noseBridgeShape.TotalWeight,
                _noseBaseShape.TotalWeight,
                _leftCheekSurface.TotalWeight,
                _rightCheekSurface.TotalWeight,
                _foreheadSurface.TotalWeight
            }
            .Where(static weight => weight > 0d)
            .ToList();
        return weights.Count == 0 ? Math.Max(0d, _acceptedSampleWeight) : weights.Min();
    }

    private static PersonalFaceLearningStability BuildLearningStability(
        double acceptedSampleWeight,
        double minimumTrackedDistributionWeight)
    {
        var weight = Math.Max(0d, acceptedSampleWeight);
        var trackedWeight = Math.Max(0d, minimumTrackedDistributionWeight);
        var conservativeWeight = trackedWeight > 0d ? Math.Min(weight, trackedWeight) : weight;
        var maximumNextSampleInfluence = CalculateNextSampleInfluencePercent(conservativeWeight, MaximumStableSampleWeight);
        var maximumEventLikeWeight = MaximumStableSampleWeight * EventLikeSampleWeightMultiplier;
        var anchorPercent = Math.Clamp(weight / AnchorTargetWeight * 100d, 0d, 100d);

        return new PersonalFaceLearningStability
        {
            AcceptedSampleWeight = weight,
            MinimumTrackedDistributionWeight = trackedWeight,
            AnchorTargetWeight = AnchorTargetWeight,
            AnchorPercent = anchorPercent,
            AnchorStatus = anchorPercent switch
            {
                >= 95d => "anchored",
                >= 50d => "warming",
                > 0d => "starting",
                _ => "waiting"
            },
            BaseExponentialMovingAverageAlphaPercent = DistributionEmaAlpha * 100d,
            MaximumStableSampleWeight = MaximumStableSampleWeight,
            EventLikeSampleWeightMultiplier = EventLikeSampleWeightMultiplier,
            MaximumNextSampleInfluencePercent = maximumNextSampleInfluence,
            MaximumEventLikeNextSampleInfluencePercent = CalculateNextSampleInfluencePercent(conservativeWeight, maximumEventLikeWeight)
        };
    }

    private static double CalculateNextSampleInfluencePercent(double acceptedSampleWeight, double nextSampleWeight)
    {
        var boundedAcceptedWeight = Math.Max(0d, acceptedSampleWeight);
        var boundedNextSampleWeight = Math.Max(0d, nextSampleWeight);
        if (boundedNextSampleWeight <= 0d)
        {
            return 0d;
        }

        return boundedNextSampleWeight / (boundedAcceptedWeight + boundedNextSampleWeight) * 100d;
    }

    private static Rect? GetFaceBounds(FaceLandmarkFrame frame)
    {
        if (frame.FaceContour.Count == 0)
        {
            return null;
        }

        var left = frame.FaceContour.Min(static point => point.X);
        var top = frame.FaceContour.Min(static point => point.Y);
        var right = frame.FaceContour.Max(static point => point.X);
        var bottom = frame.FaceContour.Max(static point => point.Y);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    private static double CalculateSampleWeight(FaceLandmarkMetrics metrics, FaceLockStabilityAnalysis stability)
    {
        var reliability = Math.Clamp(stability.CompositeReliabilityPercent / 100d, 0d, 1d);
        var continuity = Math.Clamp(stability.FaceContinuityPercent / 100d, 0d, 1d);
        var quality = Math.Clamp(metrics.OverallMeasurementQualityPercent / 100d, 0d, 1d);
        var directObservationPenalty =
            metrics.AnyEyeReconstructed || metrics.MouthReconstructed || metrics.EyeArtifactSuppressed
                ? 0.78d
                : 1d;
        return Math.Clamp(
            (reliability * 0.42d + continuity * 0.24d + quality * 0.34d) * directObservationPenalty,
            0.10d,
            MaximumStableSampleWeight);
    }

    private static double FeatureWeight(double qualityPercent, double reliabilityPercent)
    {
        return Math.Clamp(
            Math.Clamp(qualityPercent / 100d, 0d, 1d) * 0.58d
            + Math.Clamp(reliabilityPercent / 100d, 0d, 1d) * 0.42d,
            0.30d,
            1.10d);
    }

    private static double Average(double total, double weight)
    {
        return weight <= 0d ? 0d : total / weight;
    }

    private static StoredHeadPose CreateStoredHeadPose(FaceLandmarkMetrics metrics, HeadPoseEstimate? headPose)
    {
        if (headPose is { HasFace: true } pose)
        {
            return new StoredHeadPose(
                CleanPoseDegrees(pose.BRotationAroundYDegrees, metrics.HeadYawDegrees),
                CleanPoseDegrees(pose.ARotationAroundXDegrees, metrics.HeadPitchDegrees),
                CleanPoseDegrees(pose.CRotationAroundZDegrees, metrics.HeadRollDegrees));
        }

        return new StoredHeadPose(
            CleanPoseDegrees(metrics.HeadYawDegrees, 0d),
            CleanPoseDegrees(metrics.HeadPitchDegrees, 0d),
            CleanPoseDegrees(metrics.HeadRollDegrees, 0d));
    }

    private static double CleanPoseDegrees(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }

    private readonly record struct StoredHeadPose(double YawDegrees, double PitchDegrees, double RollDegrees);

    private sealed class PoseBucketCollectionAccumulator
    {
        private readonly Dictionary<string, PoseBucketAccumulator> _buckets;

        public PoseBucketCollectionAccumulator()
        {
            _buckets = PersonalFacePoseBuckets.Definitions.ToDictionary(
                static definition => definition.BucketId,
                static definition => new PoseBucketAccumulator(definition),
                StringComparer.OrdinalIgnoreCase);
        }

        public void Add(
            FaceLandmarkFrame frame,
            FaceLandmarkMetrics metrics,
            FaceLockStabilityAnalysis stability,
            PersonalFaceIdentityMeasurement identityMeasurement,
            double weight,
            StoredHeadPose headPose)
        {
            foreach (var definition in PersonalFacePoseBuckets.Classify(
                         headPose.YawDegrees,
                         headPose.PitchDegrees,
                         headPose.RollDegrees))
            {
                if (_buckets.TryGetValue(definition.BucketId, out var bucket))
                {
                    bucket.Add(frame, metrics, stability, identityMeasurement, weight, headPose);
                }
            }
        }

        public void Load(IEnumerable<PersonalFacePoseBucketProfile>? profiles)
        {
            foreach (var profile in profiles ?? [])
            {
                if (string.IsNullOrWhiteSpace(profile.BucketId))
                {
                    continue;
                }

                if (!_buckets.TryGetValue(profile.BucketId, out var bucket))
                {
                    var definition = new PersonalFacePoseBucketDefinition(
                        profile.BucketId,
                        profile.Label,
                        profile.Description,
                        profile.CaptureInstruction,
                        profile.PrimaryNeutralReference,
                        profile.RequiredForAvatarCoverage);
                    bucket = new PoseBucketAccumulator(definition);
                    _buckets[profile.BucketId] = bucket;
                }

                bucket.Load(profile);
            }
        }

        public void Clear()
        {
            foreach (var bucket in _buckets.Values)
            {
                bucket.Clear();
            }
        }

        public List<PersonalFacePoseBucketProfile> ToModel()
        {
            return PersonalFacePoseBuckets.Definitions
                .Select(static definition => definition.BucketId)
                .Concat(_buckets.Keys.Except(
                    PersonalFacePoseBuckets.Definitions.Select(static definition => definition.BucketId),
                    StringComparer.OrdinalIgnoreCase))
                .Where(_buckets.ContainsKey)
                .Select(bucketId => _buckets[bucketId].ToModel())
                .ToList();
        }
    }

    private sealed class PoseBucketAccumulator
    {
        private readonly PersonalFacePoseBucketDefinition _definition;
        private readonly DistributionAccumulator _headYaw = new();
        private readonly DistributionAccumulator _headPitch = new();
        private readonly DistributionAccumulator _headRoll = new();
        private readonly DistributionAccumulator _faceAspectRatio = new();
        private readonly DistributionAccumulator _eyeMidlineXToFaceWidth = new();
        private readonly DistributionAccumulator _mouthCenterXToFaceWidth = new();
        private readonly DistributionAccumulator _eyeToMouthXOffsetToFaceWidth = new();
        private readonly DistributionAccumulator _interEyeDistanceToFaceWidth = new();
        private readonly DistributionAccumulator _mouthWidthToFaceWidth = new();
        private readonly DistributionAccumulator _eyeMidlineYToFaceHeight = new();
        private readonly DistributionAccumulator _mouthCenterYToFaceHeight = new();
        private readonly DistributionAccumulator _averageEyeOpening = new();
        private readonly DistributionAccumulator _mouthOpening = new();
        private readonly DistributionAccumulator _jawDroop = new();
        private int _sampleCount;
        private double _totalWeight;
        private double _faceReliabilityTotal;
        private double _eyeReliabilityTotal;
        private double _mouthReliabilityTotal;

        public PoseBucketAccumulator(PersonalFacePoseBucketDefinition definition)
        {
            _definition = definition;
        }

        public void Add(
            FaceLandmarkFrame frame,
            FaceLandmarkMetrics metrics,
            FaceLockStabilityAnalysis stability,
            PersonalFaceIdentityMeasurement identityMeasurement,
            double weight,
            StoredHeadPose headPose)
        {
            if (!frame.HasFace || weight <= 0d)
            {
                return;
            }

            var boundedWeight = Math.Clamp(weight, 0.05d, MaximumStableSampleWeight);
            _sampleCount++;
            _totalWeight += boundedWeight;
            _faceReliabilityTotal += stability.CompositeReliabilityPercent * boundedWeight;
            _eyeReliabilityTotal += stability.EyeReliabilityPercent * boundedWeight;
            _mouthReliabilityTotal += stability.MouthReliabilityPercent * boundedWeight;
            _headYaw.Add(headPose.YawDegrees, boundedWeight);
            _headPitch.Add(headPose.PitchDegrees, boundedWeight);
            _headRoll.Add(headPose.RollDegrees, boundedWeight);
            _averageEyeOpening.Add(metrics.AverageEyeOpeningRatio, boundedWeight);
            _mouthOpening.Add(metrics.MouthOpeningRatio, boundedWeight);
            _jawDroop.Add(metrics.JawDroopRatio, boundedWeight);

            if (identityMeasurement.HasMeasurement)
            {
                _faceAspectRatio.Add(identityMeasurement.FaceAspectRatio, boundedWeight);
                _eyeMidlineXToFaceWidth.Add(identityMeasurement.EyeMidlineXToFaceWidth, boundedWeight);
                _mouthCenterXToFaceWidth.Add(identityMeasurement.MouthCenterXToFaceWidth, boundedWeight);
                _eyeToMouthXOffsetToFaceWidth.Add(identityMeasurement.EyeToMouthXOffsetToFaceWidth, boundedWeight);
                _interEyeDistanceToFaceWidth.Add(identityMeasurement.InterEyeDistanceToFaceWidth, boundedWeight);
                _mouthWidthToFaceWidth.Add(identityMeasurement.MouthWidthToFaceWidth, boundedWeight);
                _eyeMidlineYToFaceHeight.Add(identityMeasurement.EyeMidlineYToFaceHeight, boundedWeight);
                _mouthCenterYToFaceHeight.Add(identityMeasurement.MouthCenterYToFaceHeight, boundedWeight);
            }
        }

        public void Load(PersonalFacePoseBucketProfile profile)
        {
            _sampleCount = Math.Max(0, profile.SampleCount);
            _totalWeight = Math.Max(0d, profile.TotalWeight);
            _faceReliabilityTotal = profile.AverageFaceReliabilityPercent * _totalWeight;
            _eyeReliabilityTotal = profile.AverageEyeReliabilityPercent * _totalWeight;
            _mouthReliabilityTotal = profile.AverageMouthReliabilityPercent * _totalWeight;
            _headYaw.Load(profile.HeadYawDegrees);
            _headPitch.Load(profile.HeadPitchDegrees);
            _headRoll.Load(profile.HeadRollDegrees);
            _faceAspectRatio.Load(profile.FaceAspectRatio);
            _eyeMidlineXToFaceWidth.Load(profile.EyeMidlineXToFaceWidth);
            _mouthCenterXToFaceWidth.Load(profile.MouthCenterXToFaceWidth);
            _eyeToMouthXOffsetToFaceWidth.Load(profile.EyeToMouthXOffsetToFaceWidth);
            _interEyeDistanceToFaceWidth.Load(profile.InterEyeDistanceToFaceWidth);
            _mouthWidthToFaceWidth.Load(profile.MouthWidthToFaceWidth);
            _eyeMidlineYToFaceHeight.Load(profile.EyeMidlineYToFaceHeight);
            _mouthCenterYToFaceHeight.Load(profile.MouthCenterYToFaceHeight);
            _averageEyeOpening.Load(profile.AverageEyeOpeningRatio);
            _mouthOpening.Load(profile.MouthOpeningRatio);
            _jawDroop.Load(profile.JawDroopRatio);
        }

        public void Clear()
        {
            Load(new PersonalFacePoseBucketProfile
            {
                BucketId = _definition.BucketId,
                Label = _definition.Label,
                Description = _definition.Description,
                CaptureInstruction = _definition.CaptureInstruction,
                PrimaryNeutralReference = _definition.PrimaryNeutralReference,
                RequiredForAvatarCoverage = _definition.RequiredForAvatarCoverage
            });
        }

        public PersonalFacePoseBucketProfile ToModel()
        {
            return new PersonalFacePoseBucketProfile
            {
                BucketId = _definition.BucketId,
                Label = _definition.Label,
                Description = _definition.Description,
                CaptureInstruction = _definition.CaptureInstruction,
                PrimaryNeutralReference = _definition.PrimaryNeutralReference,
                RequiredForAvatarCoverage = _definition.RequiredForAvatarCoverage,
                SampleCount = _sampleCount,
                TotalWeight = _totalWeight,
                HeadYawDegrees = _headYaw.ToModel(),
                HeadPitchDegrees = _headPitch.ToModel(),
                HeadRollDegrees = _headRoll.ToModel(),
                FaceAspectRatio = _faceAspectRatio.ToModel(),
                EyeMidlineXToFaceWidth = _eyeMidlineXToFaceWidth.ToModel(),
                MouthCenterXToFaceWidth = _mouthCenterXToFaceWidth.ToModel(),
                EyeToMouthXOffsetToFaceWidth = _eyeToMouthXOffsetToFaceWidth.ToModel(),
                InterEyeDistanceToFaceWidth = _interEyeDistanceToFaceWidth.ToModel(),
                MouthWidthToFaceWidth = _mouthWidthToFaceWidth.ToModel(),
                EyeMidlineYToFaceHeight = _eyeMidlineYToFaceHeight.ToModel(),
                MouthCenterYToFaceHeight = _mouthCenterYToFaceHeight.ToModel(),
                AverageEyeOpeningRatio = _averageEyeOpening.ToModel(),
                MouthOpeningRatio = _mouthOpening.ToModel(),
                JawDroopRatio = _jawDroop.ToModel(),
                AverageFaceReliabilityPercent = Average(_faceReliabilityTotal, _totalWeight),
                AverageEyeReliabilityPercent = Average(_eyeReliabilityTotal, _totalWeight),
                AverageMouthReliabilityPercent = Average(_mouthReliabilityTotal, _totalWeight)
            };
        }
    }

    private sealed class DistributionAccumulator
    {
        private int _count;
        private double _totalWeight;
        private double _weightedSum;
        private double _weightedSumSquares;
        private double? _minimum;
        private double? _maximum;
        private double? _ema;

        public double TotalWeight => _totalWeight;

        public void Add(double? value, double weight)
        {
            if (value is not double number || double.IsNaN(number) || double.IsInfinity(number))
            {
                return;
            }

            var boundedWeight = Math.Clamp(weight, 0.05d, MaximumStableSampleWeight);
            _count++;
            _totalWeight += boundedWeight;
            _weightedSum += number * boundedWeight;
            _weightedSumSquares += number * number * boundedWeight;
            _minimum = _minimum is double min ? Math.Min(min, number) : number;
            _maximum = _maximum is double max ? Math.Max(max, number) : number;
            var effectiveAlpha = DistributionEmaAlpha * Math.Clamp(boundedWeight, 0.35d, MaximumStableSampleWeight);
            _ema = _ema is double previous
                ? previous + (number - previous) * effectiveAlpha
                : number;
        }

        public void Load(PersonalMetricDistribution? distribution)
        {
            _count = Math.Max(0, distribution?.SampleCount ?? 0);
            _totalWeight = Math.Max(0d, distribution?.TotalWeight ?? 0d);
            _weightedSum = 0d;
            _weightedSumSquares = 0d;
            _minimum = Clean(distribution?.Minimum);
            _maximum = Clean(distribution?.Maximum);
            _ema = Clean(distribution?.ExponentialMovingAverage);

            var average = Clean(distribution?.Average);
            if (_count <= 0 || _totalWeight <= 0d || average is not double mean)
            {
                _count = 0;
                _totalWeight = 0d;
                _minimum = null;
                _maximum = null;
                _ema = null;
                return;
            }

            var standardDeviation = Math.Max(0d, Clean(distribution?.StandardDeviation) ?? 0d);
            _weightedSum = mean * _totalWeight;
            _weightedSumSquares = (standardDeviation * standardDeviation + mean * mean) * _totalWeight;
        }

        public PersonalMetricDistribution ToModel()
        {
            double? average = _totalWeight <= 0d ? null : _weightedSum / _totalWeight;
            double? standardDeviation = _count <= 1 || average is not double mean
                ? null
                : Math.Sqrt(Math.Max(0d, _weightedSumSquares / _totalWeight - mean * mean));
            return new PersonalMetricDistribution
            {
                SampleCount = _count,
                TotalWeight = _totalWeight,
                Average = average,
                Minimum = _minimum,
                Maximum = _maximum,
                StandardDeviation = standardDeviation,
                ExponentialMovingAverage = _ema,
                NormalLow = average is double lowMean && standardDeviation is double lowStd
                    ? lowMean - lowStd * 2d
                    : _minimum,
                NormalHigh = average is double highMean && standardDeviation is double highStd
                    ? highMean + highStd * 2d
                    : _maximum
            };
        }

        private static double? Clean(double? value)
        {
            return value is double number && !double.IsNaN(number) && !double.IsInfinity(number)
                ? number
                : null;
        }
    }

    private sealed class ContourShapeAccumulator
    {
        private readonly string _featureId;
        private readonly string _label;
        private readonly int _pointCount;
        private readonly bool _closed;
        private readonly DistributionAccumulator[] _x;
        private readonly DistributionAccumulator[] _y;
        private readonly DistributionAccumulator[] _z;
        private int _sampleCount;
        private double _totalWeight;

        public double TotalWeight => _totalWeight;

        public ContourShapeAccumulator(string featureId, string label, int pointCount, bool closed)
        {
            _featureId = featureId;
            _label = label;
            _pointCount = Math.Max(2, pointCount);
            _closed = closed;
            _x = Enumerable.Range(0, _pointCount).Select(_ => new DistributionAccumulator()).ToArray();
            _y = Enumerable.Range(0, _pointCount).Select(_ => new DistributionAccumulator()).ToArray();
            _z = Enumerable.Range(0, _pointCount).Select(_ => new DistributionAccumulator()).ToArray();
        }

        public void Add(IReadOnlyList<Point> contour, Rect faceBounds, double weight)
        {
            if (contour.Count < (_closed ? 4 : 3)
                || faceBounds.Width <= 0d
                || faceBounds.Height <= 0d)
            {
                return;
            }

            var sampled = Resample(contour, _pointCount, _closed);
            if (sampled.Count != _pointCount)
            {
                return;
            }

            var boundedWeight = Math.Clamp(weight, 0.05d, MaximumStableSampleWeight);
            _sampleCount++;
            _totalWeight += boundedWeight;
            for (var index = 0; index < sampled.Count; index++)
            {
                var point = sampled[index];
                _x[index].Add((point.X - faceBounds.Left) / faceBounds.Width, boundedWeight);
                _y[index].Add((point.Y - faceBounds.Top) / faceBounds.Height, boundedWeight);
            }
        }

        public void Add(IReadOnlyList<FaceMeshLandmarkPoint> points, Rect faceBounds, double weight)
        {
            if (points.Count < (_closed ? 4 : 3)
                || faceBounds.Width <= 0d
                || faceBounds.Height <= 0d)
            {
                return;
            }

            var sampled = Resample(points, _pointCount, _closed);
            if (sampled.Count != _pointCount)
            {
                return;
            }

            var boundedWeight = Math.Clamp(weight, 0.05d, MaximumStableSampleWeight);
            _sampleCount++;
            _totalWeight += boundedWeight;
            for (var index = 0; index < sampled.Count; index++)
            {
                var point = sampled[index];
                _x[index].Add((point.X - faceBounds.Left) / faceBounds.Width, boundedWeight);
                _y[index].Add((point.Y - faceBounds.Top) / faceBounds.Height, boundedWeight);
                _z[index].Add(point.Z / faceBounds.Width, boundedWeight);
            }
        }

        public void Load(PersonalFaceContourShapeProfile? profile)
        {
            _sampleCount = Math.Max(0, profile?.SampleCount ?? 0);
            _totalWeight = Math.Max(0d, profile?.TotalWeight ?? 0d);
            var points = profile?.Points ?? [];
            for (var index = 0; index < _pointCount; index++)
            {
                var point = points.FirstOrDefault(point => point.Index == index);
                _x[index].Load(point?.X);
                _y[index].Load(point?.Y);
                _z[index].Load(point?.Z);
            }
        }

        public PersonalFaceContourShapeProfile ToModel()
        {
            var profile = new PersonalFaceContourShapeProfile
            {
                FeatureId = _featureId,
                Label = _label,
                Closed = _closed,
                PointCount = _pointCount,
                SampleCount = _sampleCount,
                TotalWeight = _totalWeight,
                Points = Enumerable.Range(0, _pointCount)
                    .Select(index => new PersonalFaceContourShapePointProfile
                    {
                        Index = index,
                        X = _x[index].ToModel(),
                        Y = _y[index].ToModel(),
                        Z = _z[index].ToModel()
                    })
                    .ToList()
            };
            AddDepthEvidence(profile);
            return profile;
        }

        private static void AddDepthEvidence(PersonalFaceContourShapeProfile profile)
        {
            var expectedPointCount = Math.Max(2, profile.PointCount);
            var populatedPoints = profile.Points.Count(static point =>
                HasValue(point.X)
                && HasValue(point.Y));
            var depthPoints = profile.Points
                .Select(static point => point.Z)
                .Where(HasValue)
                .ToList();
            var depthValues = depthPoints
                .Select(Value)
                .OfType<double>()
                .ToList();
            var depthStandardDeviations = depthPoints
                .Select(static distribution => distribution.StandardDeviation)
                .OfType<double>()
                .Where(static value => !double.IsNaN(value) && !double.IsInfinity(value))
                .ToList();

            profile.PointCoveragePercent = RoundPercent(populatedPoints / (double)expectedPointCount * 100d);
            profile.DepthPointCount = depthValues.Count;
            profile.DepthPointCoveragePercent = RoundPercent(depthValues.Count / (double)expectedPointCount * 100d);

            if (depthValues.Count == 0)
            {
                profile.DepthEvidencePercent = 0d;
                profile.DepthStabilityPercent = 0d;
                profile.DepthRange = null;
                profile.AverageDepthStandardDeviation = null;
                return;
            }

            var depthRange = depthValues.Max() - depthValues.Min();
            var averageDepthStandardDeviation = depthStandardDeviations.Count == 0
                ? (double?)null
                : depthStandardDeviations.Average();
            var sampleScore = Math.Clamp(profile.SampleCount / 45d * 100d, 0d, 100d);
            var weightScore = Math.Clamp(profile.TotalWeight / 36d * 100d, 0d, 100d);
            var depthRangeScore = Math.Clamp(depthRange / 0.055d * 100d, 0d, 100d);
            var depthStabilityScore = averageDepthStandardDeviation is double standardDeviation
                ? Math.Clamp(100d - standardDeviation / 0.050d * 100d, 0d, 100d)
                : Math.Min(sampleScore, weightScore) * 0.50d;

            profile.DepthRange = RoundPercent(depthRange);
            profile.AverageDepthStandardDeviation = averageDepthStandardDeviation is double average
                ? RoundPercent(average)
                : null;
            profile.DepthEvidencePercent = RoundPercent(
                profile.DepthPointCoveragePercent * 0.30d
                + depthRangeScore * 0.30d
                + sampleScore * 0.22d
                + weightScore * 0.18d);
            profile.DepthStabilityPercent = RoundPercent(
                depthStabilityScore * 0.62d
                + sampleScore * 0.20d
                + weightScore * 0.18d);
        }

        private static bool HasValue(PersonalMetricDistribution distribution)
        {
            return Value(distribution).HasValue;
        }

        private static double? Value(PersonalMetricDistribution distribution)
        {
            var value = distribution.ExponentialMovingAverage ?? distribution.Average;
            return value is double number && !double.IsNaN(number) && !double.IsInfinity(number)
                ? number
                : null;
        }

        private static double RoundPercent(double value)
        {
            return Math.Round(value, 6, MidpointRounding.AwayFromZero);
        }

        private static IReadOnlyList<Point> Resample(IReadOnlyList<Point> contour, int pointCount, bool closed)
        {
            if (contour.Count < 2 || pointCount < 2)
            {
                return [];
            }

            var path = contour.ToList();
            if (closed)
            {
                path.Add(contour[0]);
            }

            var distances = new double[path.Count];
            for (var index = 1; index < path.Count; index++)
            {
                distances[index] = distances[index - 1] + Distance(path[index - 1], path[index]);
            }

            var total = distances[^1];
            if (total <= 0d)
            {
                return [];
            }

            var result = new List<Point>(pointCount);
            for (var index = 0; index < pointCount; index++)
            {
                var targetDistance = closed
                    ? total * index / pointCount
                    : total * index / (pointCount - 1);
                result.Add(InterpolateAt(path, distances, targetDistance));
            }

            return result;
        }

        private static Point InterpolateAt(IReadOnlyList<Point> path, IReadOnlyList<double> distances, double targetDistance)
        {
            for (var index = 1; index < path.Count; index++)
            {
                if (distances[index] < targetDistance)
                {
                    continue;
                }

                var startDistance = distances[index - 1];
                var segmentLength = Math.Max(0.000001d, distances[index] - startDistance);
                var t = Math.Clamp((targetDistance - startDistance) / segmentLength, 0d, 1d);
                var a = path[index - 1];
                var b = path[index];
                return new Point(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t);
            }

            return path[^1];
        }

        private static IReadOnlyList<FaceMeshLandmarkPoint> Resample(IReadOnlyList<FaceMeshLandmarkPoint> contour, int pointCount, bool closed)
        {
            if (contour.Count < 2 || pointCount < 2)
            {
                return [];
            }

            var path = contour.ToList();
            if (closed)
            {
                path.Add(contour[0]);
            }

            var distances = new double[path.Count];
            for (var index = 1; index < path.Count; index++)
            {
                distances[index] = distances[index - 1] + Distance(path[index - 1], path[index]);
            }

            var total = distances[^1];
            if (total <= 0d)
            {
                return [];
            }

            var result = new List<FaceMeshLandmarkPoint>(pointCount);
            for (var index = 0; index < pointCount; index++)
            {
                var targetDistance = closed
                    ? total * index / pointCount
                    : total * index / (pointCount - 1);
                var point = InterpolateAt(path, distances, targetDistance);
                result.Add(new FaceMeshLandmarkPoint
                {
                    Index = index,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z
                });
            }

            return result;
        }

        private static FaceMeshLandmarkPoint InterpolateAt(IReadOnlyList<FaceMeshLandmarkPoint> path, IReadOnlyList<double> distances, double targetDistance)
        {
            for (var index = 1; index < path.Count; index++)
            {
                if (distances[index] < targetDistance)
                {
                    continue;
                }

                var startDistance = distances[index - 1];
                var segmentLength = Math.Max(0.000001d, distances[index] - startDistance);
                var t = Math.Clamp((targetDistance - startDistance) / segmentLength, 0d, 1d);
                var a = path[index - 1];
                var b = path[index];
                return new FaceMeshLandmarkPoint
                {
                    Index = 0,
                    X = a.X + (b.X - a.X) * t,
                    Y = a.Y + (b.Y - a.Y) * t,
                    Z = a.Z + (b.Z - a.Z) * t
                };
            }

            return path[^1];
        }

        private static double Distance(Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Distance(FaceMeshLandmarkPoint a, FaceMeshLandmarkPoint b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var dz = b.Z - a.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    private sealed record PersonalFaceModelGate(bool Accepted, PersonalFaceModelRejectionKind Kind, string Reason, double SampleWeightMultiplier)
    {
        public static PersonalFaceModelGate Accept { get; } = new(true, PersonalFaceModelRejectionKind.None, "accepted stable measurement", 1d);

        public static PersonalFaceModelGate AcceptEventLike(string reason)
        {
            return new PersonalFaceModelGate(true, PersonalFaceModelRejectionKind.None, reason, EventLikeSampleWeightMultiplier);
        }

        public static PersonalFaceModelGate Reject(PersonalFaceModelRejectionKind kind, string reason)
        {
            return new PersonalFaceModelGate(false, kind, reason, 0d);
        }
    }
}
