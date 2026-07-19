using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public static class FaceLandmarkCropMapper
{
    public static FaceLandmarkTrackingResult MapToFrame(
        FaceLandmarkTrackingResult cropResult,
        Rect normalizedCrop,
        string backendStatusSuffix)
    {
        if (!cropResult.HasFace)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = cropResult.BackendName,
                BackendStatus = AppendStatus(cropResult.BackendStatus, backendStatusSuffix),
                FeatureDetection = cropResult.FeatureDetection,
                LandmarkFrame = cropResult.LandmarkFrame
            };
        }

        return new FaceLandmarkTrackingResult
        {
            BackendName = cropResult.BackendName,
            BackendStatus = AppendStatus(cropResult.BackendStatus, backendStatusSuffix),
            FeatureDetection = MapFeatureDetection(cropResult.FeatureDetection, normalizedCrop),
            LandmarkFrame = MapLandmarkFrame(cropResult.LandmarkFrame, normalizedCrop)
        };
    }

    private static FaceFeatureDetection MapFeatureDetection(FaceFeatureDetection feature, Rect crop)
    {
        if (!feature.HasFace)
        {
            return feature;
        }

        return new FaceFeatureDetection
        {
            HasFace = true,
            FaceBox = MapRect(feature.FaceBox, crop),
            LeftEyeBox = MapNullableRect(feature.LeftEyeBox, crop),
            RightEyeBox = MapNullableRect(feature.RightEyeBox, crop),
            MouthBox = MapNullableRect(feature.MouthBox, crop),
            FaceContour = MapPoints(feature.FaceContour, crop),
            LeftEyeContour = MapPoints(feature.LeftEyeContour, crop),
            RightEyeContour = MapPoints(feature.RightEyeContour, crop),
            OuterLipContour = MapPoints(feature.OuterLipContour, crop),
            InnerLipContour = MapPoints(feature.InnerLipContour, crop),
            JawContour = MapPoints(feature.JawContour, crop),
            TrackingConfidence = feature.TrackingConfidence,
            EyeConfidence = feature.EyeConfidence,
            MouthConfidence = feature.MouthConfidence,
            EyeImageQualityAvailable = feature.EyeImageQualityAvailable,
            MouthImageQualityAvailable = feature.MouthImageQualityAvailable,
            EyeGlarePercent = feature.EyeGlarePercent,
            MouthGlarePercent = feature.MouthGlarePercent,
            EyeContrastPercent = feature.EyeContrastPercent,
            MouthContrastPercent = feature.MouthContrastPercent,
            EyeSharpnessPercent = feature.EyeSharpnessPercent,
            MouthSharpnessPercent = feature.MouthSharpnessPercent,
            EyeDarkCoveragePercent = feature.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = feature.MouthDarkCoveragePercent,
            Source = AppendStatus(feature.Source, "crop mapped")
        };
    }

    private static FaceLandmarkFrame MapLandmarkFrame(FaceLandmarkFrame frame, Rect crop)
    {
        if (!frame.HasFace)
        {
            return frame;
        }

        return new FaceLandmarkFrame
        {
            HasFace = true,
            Source = AppendStatus(frame.Source, "crop mapped"),
            CapturedAtUtc = frame.CapturedAtUtc,
            TrackingConfidence = frame.TrackingConfidence,
            EyeConfidence = frame.EyeConfidence,
            MouthConfidence = frame.MouthConfidence,
            EyeImageQualityAvailable = frame.EyeImageQualityAvailable,
            MouthImageQualityAvailable = frame.MouthImageQualityAvailable,
            EyeGlarePercent = frame.EyeGlarePercent,
            MouthGlarePercent = frame.MouthGlarePercent,
            EyeContrastPercent = frame.EyeContrastPercent,
            MouthContrastPercent = frame.MouthContrastPercent,
            EyeSharpnessPercent = frame.EyeSharpnessPercent,
            MouthSharpnessPercent = frame.MouthSharpnessPercent,
            EyeDarkCoveragePercent = frame.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = frame.MouthDarkCoveragePercent,
            LeftEyeReconstructed = frame.LeftEyeReconstructed,
            RightEyeReconstructed = frame.RightEyeReconstructed,
            MouthReconstructed = frame.MouthReconstructed,
            EyeArtifactSuppressed = frame.EyeArtifactSuppressed,
            HeadYawDegrees = frame.HeadYawDegrees,
            HeadPitchDegrees = frame.HeadPitchDegrees,
            HeadRollDegrees = frame.HeadRollDegrees,
            BlendshapeScores = frame.BlendshapeScores,
            DenseMeshTopology = frame.DenseMeshTopology,
            DenseMeshPoints = MapDensePoints(frame.DenseMeshPoints, crop),
            FacialTransformationMatrix = frame.FacialTransformationMatrix,
            FaceContour = MapPoints(frame.FaceContour, crop),
            LeftEyeContour = MapPoints(frame.LeftEyeContour, crop),
            RightEyeContour = MapPoints(frame.RightEyeContour, crop),
            LeftBrowContour = MapPoints(frame.LeftBrowContour, crop),
            RightBrowContour = MapPoints(frame.RightBrowContour, crop),
            OuterLipContour = MapPoints(frame.OuterLipContour, crop),
            InnerLipContour = MapPoints(frame.InnerLipContour, crop),
            JawContour = MapPoints(frame.JawContour, crop)
        };
    }

    private static Rect? MapNullableRect(Rect? rect, Rect crop)
    {
        return rect is Rect value ? MapRect(value, crop) : null;
    }

    private static Rect MapRect(Rect rect, Rect crop)
    {
        var left = MapX(rect.Left, crop);
        var top = MapY(rect.Top, crop);
        var right = MapX(rect.Right, crop);
        var bottom = MapY(rect.Bottom, crop);
        return new Rect(left, top, Math.Max(0d, right - left), Math.Max(0d, bottom - top));
    }

    private static IReadOnlyList<Point> MapPoints(IReadOnlyList<Point> points, Rect crop)
    {
        if (points.Count == 0)
        {
            return [];
        }

        return points
            .Select(point => new Point(MapX(point.X, crop), MapY(point.Y, crop)))
            .ToList();
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> MapDensePoints(IReadOnlyList<FaceMeshLandmarkPoint> points, Rect crop)
    {
        if (points.Count == 0)
        {
            return [];
        }

        return points
            .Select(point => new FaceMeshLandmarkPoint
            {
                Index = point.Index,
                X = MapX(point.X, crop),
                Y = MapY(point.Y, crop),
                Z = point.Z
            })
            .ToList();
    }

    private static double MapX(double x, Rect crop)
    {
        return Clamp01(crop.Left + x * crop.Width);
    }

    private static double MapY(double y, Rect crop)
    {
        return Clamp01(crop.Top + y * crop.Height);
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0d, 1d);
    }

    private static string AppendStatus(string value, string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(value)
            ? suffix
            : $"{value}; {suffix}";
    }
}
