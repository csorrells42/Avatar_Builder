using AvatarBuilder.Modules.Vision.Common;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Onnx;

public static class ThreeDdfaOnnxFaceTrackingMapper
{
    private static readonly int[] JawIndices = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly int[] LeftBrowIndices = [17, 18, 19, 20, 21];
    private static readonly int[] RightBrowIndices = [22, 23, 24, 25, 26];
    private static readonly int[] LeftEyeIndices = [36, 37, 38, 39, 40, 41];
    private static readonly int[] RightEyeIndices = [42, 43, 44, 45, 46, 47];
    private static readonly int[] OuterLipIndices = [48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59];
    private static readonly int[] InnerLipIndices = [60, 61, 62, 63, 64, 65, 66, 67];

    public static FaceLandmarkTrackingResult ToTrackingResult(
        ThreeDdfaOnnxSidecarResponse response,
        int frameWidth,
        int frameHeight,
        DateTime capturedAtUtc)
    {
        if (!response.Ok || !response.HasFace || response.FaceBox is null || frameWidth <= 0 || frameHeight <= 0)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = "3DDFA-V2 FaceBoxes",
                BackendStatus = string.IsNullOrWhiteSpace(response.Status)
                    ? "3DDFA-V2 FaceBoxes searching"
                    : response.Status
            };
        }

        var faceBox = NormalizeFaceBox(response.FaceBox, frameWidth, frameHeight);
        if (faceBox.Width <= 0d || faceBox.Height <= 0d)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = "3DDFA-V2 FaceBoxes",
                BackendStatus = "3DDFA-V2 returned an invalid face box"
            };
        }

        var sparse = response.SparseLandmarks.ToDictionary(static point => point.Index);
        var jaw = SelectPoints(sparse, JawIndices, frameWidth, frameHeight);
        var leftEye = SelectPoints(sparse, LeftEyeIndices, frameWidth, frameHeight);
        var rightEye = SelectPoints(sparse, RightEyeIndices, frameWidth, frameHeight);
        var leftBrow = SelectPoints(sparse, LeftBrowIndices, frameWidth, frameHeight);
        var rightBrow = SelectPoints(sparse, RightBrowIndices, frameWidth, frameHeight);
        var outerLip = SelectPoints(sparse, OuterLipIndices, frameWidth, frameHeight);
        var innerLip = SelectPoints(sparse, InnerLipIndices, frameWidth, frameHeight);
        var faceContour = CreateFaceContour(faceBox, 32);
        var confidence = Math.Clamp(response.ReconstructionConfidencePercent / 100d, 0.01d, 1d);
        var eyeConfidence = leftEye.Count >= 4 && rightEye.Count >= 4 ? confidence * 0.92d : confidence * 0.35d;
        var mouthConfidence = outerLip.Count >= 4 ? confidence * 0.90d : confidence * 0.35d;
        var source = "3DDFA_V2 ONNX FaceBoxes and 68-point landmarks";

        var featureDetection = new FaceFeatureDetection
        {
            HasFace = true,
            Source = source,
            FaceBox = faceBox,
            LeftEyeBox = Bounds(leftEye),
            RightEyeBox = Bounds(rightEye),
            MouthBox = Bounds(outerLip),
            FaceContour = faceContour,
            LeftEyeContour = leftEye,
            RightEyeContour = rightEye,
            OuterLipContour = outerLip,
            InnerLipContour = innerLip,
            JawContour = jaw,
            TrackingConfidence = confidence,
            EyeConfidence = eyeConfidence,
            MouthConfidence = mouthConfidence
        };

        var landmarkFrame = new FaceLandmarkFrame
        {
            HasFace = true,
            Source = source,
            CapturedAtUtc = capturedAtUtc,
            TrackingConfidence = confidence,
            EyeConfidence = eyeConfidence,
            MouthConfidence = mouthConfidence,
            HeadPitchDegrees = response.Pose.ARotationAroundXDegrees,
            HeadYawDegrees = response.Pose.BRotationAroundYDegrees,
            HeadRollDegrees = response.Pose.CRotationAroundZDegrees,
            FaceContour = faceContour,
            LeftEyeContour = leftEye,
            RightEyeContour = rightEye,
            LeftBrowContour = leftBrow,
            RightBrowContour = rightBrow,
            OuterLipContour = outerLip,
            InnerLipContour = innerLip,
            JawContour = jaw
        };

        return new FaceLandmarkTrackingResult
        {
            BackendName = "3DDFA-V2 FaceBoxes",
            BackendStatus = response.Status,
            FeatureDetection = featureDetection,
            LandmarkFrame = landmarkFrame,
            Diagnostics = response.Diagnostics
        };
    }

    private static Rect NormalizeFaceBox(ThreeDdfaOnnxSidecarFaceBox faceBox, int frameWidth, int frameHeight)
    {
        var left = faceBox.Normalized ? faceBox.Left : faceBox.Left / frameWidth;
        var top = faceBox.Normalized ? faceBox.Top : faceBox.Top / frameHeight;
        var right = faceBox.Normalized ? faceBox.Right : faceBox.Right / frameWidth;
        var bottom = faceBox.Normalized ? faceBox.Bottom : faceBox.Bottom / frameHeight;
        left = Math.Clamp(left, 0d, 1d);
        top = Math.Clamp(top, 0d, 1d);
        right = Math.Clamp(right, left, 1d);
        bottom = Math.Clamp(bottom, top, 1d);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<Point> SelectPoints(
        IReadOnlyDictionary<int, ThreeDdfaOnnxSidecarVertex> points,
        IReadOnlyList<int> indices,
        int frameWidth,
        int frameHeight)
    {
        return indices
            .Where(points.ContainsKey)
            .Select(index => points[index])
            .Select(point => new Point(
                Math.Clamp(point.X / frameWidth, 0d, 1d),
                Math.Clamp(point.Y / frameHeight, 0d, 1d)))
            .ToList();
    }

    private static Rect? Bounds(IReadOnlyList<Point> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var left = points.Min(static point => point.X);
        var top = points.Min(static point => point.Y);
        var right = points.Max(static point => point.X);
        var bottom = points.Max(static point => point.Y);
        return right <= left || bottom <= top
            ? null
            : new Rect(left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<Point> CreateFaceContour(Rect faceBox, int pointCount)
    {
        var points = new List<Point>(pointCount);
        var centerX = faceBox.Left + faceBox.Width / 2d;
        var centerY = faceBox.Top + faceBox.Height / 2d;
        for (var index = 0; index < pointCount; index++)
        {
            var angle = Math.PI * 2d * index / pointCount;
            points.Add(new Point(
                centerX + Math.Cos(angle) * faceBox.Width / 2d,
                centerY + Math.Sin(angle) * faceBox.Height / 2d));
        }

        return points;
    }
}
