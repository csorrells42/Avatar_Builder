using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public static class FaceFeatureDetectionExtensions
{
    public static FaceLandmarkFrame ToLandmarkFrame(this FaceFeatureDetection detection, DateTime capturedAtUtc)
    {
        if (!detection.HasFace)
        {
            return FaceLandmarkFrame.None;
        }

        var hasBothEyes = detection.LeftEyeBox is not null && detection.RightEyeBox is not null;
        var hasMouth = detection.MouthBox is not null;
        return new FaceLandmarkFrame
        {
            HasFace = true,
            CapturedAtUtc = capturedAtUtc,
            Source = $"{detection.Source} landmark fallback",
            TrackingConfidence = detection.TrackingConfidence > 0d ? detection.TrackingConfidence : 0.50d,
            EyeConfidence = detection.EyeConfidence > 0d ? detection.EyeConfidence : hasBothEyes ? 0.46d : 0.22d,
            MouthConfidence = detection.MouthConfidence > 0d ? detection.MouthConfidence : hasMouth ? 0.40d : 0.18d,
            EyeImageQualityAvailable = detection.EyeImageQualityAvailable,
            MouthImageQualityAvailable = detection.MouthImageQualityAvailable,
            EyeGlarePercent = detection.EyeGlarePercent,
            MouthGlarePercent = detection.MouthGlarePercent,
            EyeContrastPercent = detection.EyeContrastPercent,
            MouthContrastPercent = detection.MouthContrastPercent,
            EyeSharpnessPercent = detection.EyeSharpnessPercent,
            MouthSharpnessPercent = detection.MouthSharpnessPercent,
            EyeDarkCoveragePercent = detection.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = detection.MouthDarkCoveragePercent,
            FaceContour = detection.FaceContour.Count > 0 ? detection.FaceContour : CreateOvalContour(detection.FaceBox, 24),
            LeftEyeContour = detection.LeftEyeContour.Count > 0
                ? detection.LeftEyeContour
                : detection.LeftEyeBox is Rect leftEye ? CreateEyeContour(leftEye) : [],
            RightEyeContour = detection.RightEyeContour.Count > 0
                ? detection.RightEyeContour
                : detection.RightEyeBox is Rect rightEye ? CreateEyeContour(rightEye) : [],
            LeftBrowContour = detection.LeftEyeBox is Rect leftBrowEye ? CreateBrowContour(leftBrowEye) : [],
            RightBrowContour = detection.RightEyeBox is Rect rightBrowEye ? CreateBrowContour(rightBrowEye) : [],
            OuterLipContour = detection.OuterLipContour.Count > 0
                ? detection.OuterLipContour
                : detection.MouthBox is Rect mouth ? CreateMouthContour(mouth, outer: true) : [],
            InnerLipContour = detection.InnerLipContour.Count > 0
                ? detection.InnerLipContour
                : detection.MouthBox is Rect innerMouth ? CreateMouthContour(innerMouth, outer: false) : [],
            JawContour = detection.JawContour.Count > 0 ? detection.JawContour : CreateJawContour(detection.FaceBox)
        };
    }

    private static IReadOnlyList<Point> CreateEyeContour(Rect box)
    {
        var centerX = box.Left + box.Width / 2d;
        var centerY = box.Top + box.Height / 2d;
        var halfWidth = box.Width * 0.50d;
        var halfHeight = box.Height * 0.34d;
        return
        [
            new(centerX - halfWidth, centerY),
            new(centerX - halfWidth * 0.55d, centerY - halfHeight),
            new(centerX, centerY - halfHeight * 1.05d),
            new(centerX + halfWidth * 0.55d, centerY - halfHeight),
            new(centerX + halfWidth, centerY),
            new(centerX + halfWidth * 0.55d, centerY + halfHeight),
            new(centerX, centerY + halfHeight * 1.05d),
            new(centerX - halfWidth * 0.55d, centerY + halfHeight)
        ];
    }

    private static IReadOnlyList<Point> CreateMouthContour(Rect box, bool outer)
    {
        var centerX = box.Left + box.Width / 2d;
        var centerY = box.Top + box.Height * (outer ? 0.52d : 0.56d);
        var halfWidth = box.Width * (outer ? 0.50d : 0.36d);
        var halfHeight = box.Height * (outer ? 0.24d : 0.13d);
        return
        [
            new(centerX - halfWidth, centerY),
            new(centerX - halfWidth * 0.50d, centerY - halfHeight),
            new(centerX, centerY - halfHeight * 1.10d),
            new(centerX + halfWidth * 0.50d, centerY - halfHeight),
            new(centerX + halfWidth, centerY),
            new(centerX + halfWidth * 0.50d, centerY + halfHeight),
            new(centerX, centerY + halfHeight * 1.10d),
            new(centerX - halfWidth * 0.50d, centerY + halfHeight)
        ];
    }

    private static IReadOnlyList<Point> CreateBrowContour(Rect eyeBox)
    {
        var centerX = eyeBox.Left + eyeBox.Width / 2d;
        var y = Math.Clamp(eyeBox.Top - eyeBox.Height * 0.50d, 0d, 1d);
        var halfWidth = eyeBox.Width * 0.52d;
        return
        [
            new(Math.Clamp(centerX - halfWidth, 0d, 1d), y + eyeBox.Height * 0.05d),
            new(Math.Clamp(centerX - halfWidth * 0.45d, 0d, 1d), y - eyeBox.Height * 0.03d),
            new(centerX, y - eyeBox.Height * 0.06d),
            new(Math.Clamp(centerX + halfWidth * 0.45d, 0d, 1d), y - eyeBox.Height * 0.03d),
            new(Math.Clamp(centerX + halfWidth, 0d, 1d), y + eyeBox.Height * 0.05d)
        ];
    }

    private static IReadOnlyList<Point> CreateOvalContour(Rect box, int count)
    {
        var points = new List<Point>(count);
        var centerX = box.Left + box.Width / 2d;
        var centerY = box.Top + box.Height / 2d;
        for (var index = 0; index < count; index++)
        {
            var angle = Math.PI * 2d * index / count;
            points.Add(new Point(
                centerX + Math.Cos(angle) * box.Width * 0.50d,
                centerY + Math.Sin(angle) * box.Height * 0.50d));
        }

        return points;
    }

    private static IReadOnlyList<Point> CreateJawContour(Rect face)
    {
        return
        [
            new(face.Left + face.Width * 0.12d, face.Top + face.Height * 0.62d),
            new(face.Left + face.Width * 0.22d, face.Top + face.Height * 0.80d),
            new(face.Left + face.Width * 0.50d, face.Bottom),
            new(face.Left + face.Width * 0.78d, face.Top + face.Height * 0.80d),
            new(face.Left + face.Width * 0.88d, face.Top + face.Height * 0.62d)
        ];
    }
}
