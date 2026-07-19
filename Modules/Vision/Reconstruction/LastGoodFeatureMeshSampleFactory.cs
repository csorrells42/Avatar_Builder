using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class LastGoodFeatureMeshSampleFactory
{
    private const double MinimumOverallQualityPercent = 55d;
    private const double MinimumEyeQualityPercent = 50d;
    private const double MinimumMouthQualityPercent = 48d;
    private const double MinimumTrackingConfidence = 0.70d;

    private static readonly int[] FaceOval =
    [
        10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288,
        397, 365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136,
        172, 58, 132, 93, 234, 127, 162, 21, 54, 103, 67, 109
    ];

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

    public static bool TryCreate(
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        FaceLockStabilityAnalysis stability,
        AvatarCaptureQualityAssessment captureQuality,
        out LastGoodFeatureMeshSample sample,
        out string reason,
        FaceFrameGeometry? faceGeometry = null)
    {
        sample = new LastGoodFeatureMeshSample();
        reason = "";

        if (!frame.HasDenseMesh)
        {
            reason = "waiting for dense 468-point mesh";
            return false;
        }

        if (!frame.HasEyeContours || !frame.HasMouthContours || !metrics.HasFace)
        {
            reason = "waiting for eye and mouth feature lock";
            return false;
        }

        if (frame.TrackingConfidence < MinimumTrackingConfidence)
        {
            reason = $"tracking confidence {frame.TrackingConfidence * 100d:0}% below {MinimumTrackingConfidence * 100d:0}%";
            return false;
        }

        if (metrics.OverallMeasurementQualityPercent < MinimumOverallQualityPercent
            || metrics.EyeMeasurementQualityPercent < MinimumEyeQualityPercent
            || metrics.MouthMeasurementQualityPercent < MinimumMouthQualityPercent)
        {
            reason = $"feature quality low: overall {metrics.OverallMeasurementQualityPercent:0}%, eyes {metrics.EyeMeasurementQualityPercent:0}%, mouth {metrics.MouthMeasurementQualityPercent:0}%";
            return false;
        }

        if (metrics.AnyEyeReconstructed || metrics.MouthReconstructed || metrics.EyeArtifactSuppressed || metrics.PossibleOneEyeArtifact)
        {
            reason = "feature lock used reconstruction or artifact suppression";
            return false;
        }

        var storedFaceGeometry = CreateStoredFaceGeometry(frame, metrics, faceGeometry);
        var points = frame.DenseMeshPoints
            .OrderBy(static point => point.Index)
            .Select(static point => new FaceMeshLandmarkPoint
            {
                Index = point.Index,
                X = Round(point.X),
                Y = Round(point.Y),
                Z = Round(point.Z)
            })
            .ToList();
        var featureGroups = BuildFeatureGroups(points, metrics).ToList();
        var wireframeEdges = LastGoodFeatureMeshWireframeBuilder.Build(
            points,
            featureGroups,
            frame.TrackingConfidence * 100d);

        sample = new LastGoodFeatureMeshSample
        {
            SampleId = $"{frame.CapturedAtUtc:yyyyMMddHHmmssfff}-{points.Count}",
            CapturedAtUtc = frame.CapturedAtUtc == default ? DateTime.UtcNow : frame.CapturedAtUtc,
            Source = frame.Source,
            DenseMeshTopology = string.IsNullOrWhiteSpace(frame.DenseMeshTopology) ? "unknown" : frame.DenseMeshTopology,
            PointCount = points.Count,
            TrackingConfidencePercent = Round(frame.TrackingConfidence * 100d),
            EyeConfidencePercent = Round(frame.EyeConfidence * 100d),
            MouthConfidencePercent = Round(frame.MouthConfidence * 100d),
            OverallQualityPercent = Round(metrics.OverallMeasurementQualityPercent),
            EyeQualityPercent = Round(metrics.EyeMeasurementQualityPercent),
            MouthQualityPercent = Round(metrics.MouthMeasurementQualityPercent),
            BrowQualityPercent = Round(metrics.BrowMeasurementQualityPercent),
            FaceReliabilityPercent = Round(stability.CompositeReliabilityPercent),
            FaceContinuityPercent = Round(stability.FaceContinuityPercent),
            EyeReliabilityPercent = Round(stability.EyeReliabilityPercent),
            MouthReliabilityPercent = Round(stability.MouthReliabilityPercent),
            HeadYawDegrees = Round(storedFaceGeometry.YawDegrees),
            HeadPitchDegrees = Round(storedFaceGeometry.PitchDegrees),
            HeadRollDegrees = Round(storedFaceGeometry.RollDegrees),
            XHorizontalPercent = Round(storedFaceGeometry.XHorizontalPercent),
            YVerticalPercent = Round(storedFaceGeometry.YVerticalPercent),
            DistanceInches = RoundNullable(storedFaceGeometry.DistanceInches),
            ApparentDistanceUnits = RoundNullable(storedFaceGeometry.ApparentDistanceUnits),
            RelativeDistanceScale = RoundNullable(storedFaceGeometry.RelativeDistanceScale),
            InterEyeFrameWidthPercent = RoundNullable(storedFaceGeometry.InterEyeFrameWidthPercent),
            ZConfidencePercent = Round(storedFaceGeometry.ZConfidencePercent),
            DistanceCalibrated = storedFaceGeometry.DistanceCalibrated,
            ZUsesCameraFov = storedFaceGeometry.ZUsesCameraFov,
            ZUsesLearnedReference = storedFaceGeometry.ZUsesLearnedReference,
            ZEstimateKind = storedFaceGeometry.ZEstimateKind,
            ZQualityLabel = storedFaceGeometry.ZQualityLabel,
            RotationSource = storedFaceGeometry.RotationSource,
            DistanceSource = storedFaceGeometry.DistanceSource,
            ReferenceScaleSource = storedFaceGeometry.ReferenceScaleSource,
            LeftBrowHeightRatio = RoundNullable(metrics.LeftBrowHeightRatio),
            RightBrowHeightRatio = RoundNullable(metrics.RightBrowHeightRatio),
            AverageBrowHeightRatio = RoundNullable(metrics.AverageBrowHeightRatio),
            BrowAsymmetryPercent = RoundNullable(metrics.BrowAsymmetryPercent),
            PossibleOneEyeArtifact = metrics.PossibleOneEyeArtifact,
            LeftEyeReconstructed = metrics.LeftEyeReconstructed,
            RightEyeReconstructed = metrics.RightEyeReconstructed,
            MouthReconstructed = metrics.MouthReconstructed,
            EyeArtifactSuppressed = metrics.EyeArtifactSuppressed,
            CaptureQualityLabel = captureQuality.Label,
            CaptureQualityScorePercent = Round(captureQuality.ScorePercent),
            GoodFeatureReason = "dense mesh with direct eye and mouth feature lock",
            FacialTransformationMatrix = frame.FacialTransformationMatrix.Select(Round).ToList(),
            Points = points,
            WireframeEdges = wireframeEdges,
            FeatureGroups = featureGroups
        };

        reason = sample.GoodFeatureReason;
        return true;
    }

    private static StoredFaceGeometry CreateStoredFaceGeometry(
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        FaceFrameGeometry? faceGeometry)
    {
        if (faceGeometry is { HasFace: true } pose)
        {
            return new StoredFaceGeometry(
                CleanPoseDegrees(pose.BRotationAroundYDegrees, metrics.HeadYawDegrees),
                CleanPoseDegrees(pose.ARotationAroundXDegrees, metrics.HeadPitchDegrees),
                CleanPoseDegrees(pose.CRotationAroundZDegrees, metrics.HeadRollDegrees),
                CleanPoseDegrees(pose.XHorizontalPercent, 0d),
                CleanPoseDegrees(pose.YVerticalPercent, 0d),
                pose.DistanceInches,
                pose.ApparentDistanceUnits,
                pose.RelativeDistanceScale,
                pose.InterEyeFrameWidthPercent,
                CleanPoseDegrees(pose.ZConfidencePercent, 0d),
                pose.DistanceCalibrated,
                pose.ZUsesCameraFov,
                pose.ZUsesLearnedReference,
                pose.ZEstimateKind,
                pose.ZQualityLabel,
                string.IsNullOrWhiteSpace(pose.RotationSource) ? "face-frame geometry" : pose.RotationSource,
                pose.DistanceSource,
                pose.ReferenceScaleSource);
        }

        return new StoredFaceGeometry(
            CleanPoseDegrees(metrics.HeadYawDegrees, frame.HeadYawDegrees),
            CleanPoseDegrees(metrics.HeadPitchDegrees, frame.HeadPitchDegrees),
            CleanPoseDegrees(metrics.HeadRollDegrees, frame.HeadRollDegrees),
            0d,
            0d,
            null,
            null,
            null,
            null,
            0d,
            false,
            false,
            false,
            "",
            "",
            "landmark frame pose",
            "",
            "");
    }

    private static IEnumerable<LastGoodFeatureMeshFeatureGroup> BuildFeatureGroups(
        IReadOnlyList<FaceMeshLandmarkPoint> points,
        FaceLandmarkMetrics metrics)
    {
        yield return Create("face_oval", "Face oval", "face", FaceOval, closed: true, metrics.TrackingConfidence * 100d);

        var eyeAIsLeft = AverageX(points, EyeA) <= AverageX(points, EyeB);
        yield return Create(
            eyeAIsLeft ? "left_eye" : "right_eye",
            eyeAIsLeft ? "Left eye" : "Right eye",
            "eye",
            EyeA,
            closed: true,
            metrics.EyeMeasurementQualityPercent);
        yield return Create(
            eyeAIsLeft ? "right_eye" : "left_eye",
            eyeAIsLeft ? "Right eye" : "Left eye",
            "eye",
            EyeB,
            closed: true,
            metrics.EyeMeasurementQualityPercent);

        var browAIsLeft = AverageX(points, BrowA) <= AverageX(points, BrowB);
        yield return Create(
            browAIsLeft ? "left_brow" : "right_brow",
            browAIsLeft ? "Left eyebrow" : "Right eyebrow",
            "brow",
            BrowA,
            closed: false,
            metrics.TrackingConfidence * 100d);
        yield return Create(
            browAIsLeft ? "right_brow" : "left_brow",
            browAIsLeft ? "Right eyebrow" : "Left eyebrow",
            "brow",
            BrowB,
            closed: false,
            metrics.TrackingConfidence * 100d);

        yield return Create("outer_lip", "Outer lip", "mouth", OuterLip, closed: true, metrics.MouthMeasurementQualityPercent);
        yield return Create("inner_lip", "Mouth opening", "mouth-opening", InnerLip, closed: true, metrics.MouthMeasurementQualityPercent);
        yield return Create("jaw", "Jaw contour", "jaw", Jaw, closed: false, metrics.MouthMeasurementQualityPercent);
        yield return Create("nose_bridge", "Nose bridge", "nose", NoseBridge, closed: false, metrics.TrackingConfidence * 100d);
        yield return Create("nose_base", "Nose base", "nose", NoseBase, closed: false, metrics.TrackingConfidence * 100d);
        yield return Create("forehead", "Forehead surface", "forehead", Forehead, closed: false, metrics.TrackingConfidence * 100d);

        var cheekAIsLeft = AverageX(points, CheekA) <= AverageX(points, CheekB);
        yield return Create(
            cheekAIsLeft ? "left_cheek" : "right_cheek",
            cheekAIsLeft ? "Left cheek" : "Right cheek",
            "cheek",
            CheekA,
            closed: false,
            metrics.TrackingConfidence * 100d);
        yield return Create(
            cheekAIsLeft ? "right_cheek" : "left_cheek",
            cheekAIsLeft ? "Right cheek" : "Left cheek",
            "cheek",
            CheekB,
            closed: false,
            metrics.TrackingConfidence * 100d);
    }

    private static LastGoodFeatureMeshFeatureGroup Create(
        string id,
        string label,
        string role,
        IReadOnlyList<int> indices,
        bool closed,
        double confidencePercent)
    {
        return new LastGoodFeatureMeshFeatureGroup
        {
            Id = id,
            Label = label,
            Role = role,
            Closed = closed,
            ConfidencePercent = Round(confidencePercent),
            LandmarkIndices = indices.ToList()
        };
    }

    private static double AverageX(IReadOnlyList<FaceMeshLandmarkPoint> points, IReadOnlyList<int> indices)
    {
        var lookup = points.ToDictionary(static point => point.Index);
        var values = indices
            .Where(lookup.ContainsKey)
            .Select(index => lookup[index].X)
            .ToList();
        return values.Count == 0 ? 0.5d : values.Average();
    }

    private static double Round(double value)
    {
        return double.IsFinite(value) ? Math.Round(value, 6) : 0d;
    }

    private static double? RoundNullable(double? value)
    {
        return value is double number && double.IsFinite(number)
            ? Math.Round(number, 6)
            : null;
    }

    private static double CleanPoseDegrees(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }

    private sealed record StoredFaceGeometry(
        double YawDegrees,
        double PitchDegrees,
        double RollDegrees,
        double XHorizontalPercent,
        double YVerticalPercent,
        double? DistanceInches,
        double? ApparentDistanceUnits,
        double? RelativeDistanceScale,
        double? InterEyeFrameWidthPercent,
        double ZConfidencePercent,
        bool DistanceCalibrated,
        bool ZUsesCameraFov,
        bool ZUsesLearnedReference,
        string ZEstimateKind,
        string ZQualityLabel,
        string RotationSource,
        string DistanceSource,
        string ReferenceScaleSource);
}
