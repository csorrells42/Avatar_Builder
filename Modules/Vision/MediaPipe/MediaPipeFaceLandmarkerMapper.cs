using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal static class MediaPipeFaceLandmarkerMapper
{
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

    public static FaceLandmarkTrackingResult ToTrackingResult(
        MediaPipeSidecarResponse response,
        DateTime capturedAtUtc,
        string backendName)
    {
        if (!response.Ok)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = backendName,
                BackendStatus = response.Status
            };
        }

        if (!response.HasFace || response.Landmarks.Count < 468)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = backendName,
                BackendStatus = string.IsNullOrWhiteSpace(response.Status)
                    ? "MediaPipe sidecar searching"
                    : response.Status
            };
        }

        var faceContour = Select(response.Landmarks, FaceOval);
        var firstEye = Select(response.Landmarks, EyeA);
        var secondEye = Select(response.Landmarks, EyeB);
        var (leftEye, rightEye) = SortEyesByFramePosition(firstEye, secondEye);
        var firstBrow = Select(response.Landmarks, MediaPipeBrowOutlineGeometry.BrowAIndices);
        var secondBrow = Select(response.Landmarks, MediaPipeBrowOutlineGeometry.BrowBIndices);
        var (leftBrow, rightBrow) = SortEyesByFramePosition(firstBrow, secondBrow);
        var outerLip = Select(response.Landmarks, OuterLip);
        var innerLip = Select(response.Landmarks, InnerLip);
        var jaw = Select(response.Landmarks, Jaw);
        var blendshapes = CreateBlendshapeDictionary(response.Blendshapes);
        var faceBox = BoundingRect(faceContour);
        var pose = EstimatePoseDegrees(response, leftEye, rightEye);
        var frame = new FaceLandmarkFrame
        {
            HasFace = true,
            Source = "MediaPipe Face Landmarker sidecar",
            CapturedAtUtc = capturedAtUtc,
            TrackingConfidence = 0.94d,
            EyeConfidence = 0.90d,
            MouthConfidence = 0.90d,
            HeadYawDegrees = pose.YawDegrees,
            HeadPitchDegrees = pose.PitchDegrees,
            HeadRollDegrees = pose.RollDegrees,
            BlendshapeScores = blendshapes,
            DenseMeshTopology = "MediaPipeFaceMesh468",
            DenseMeshPoints = CreateDenseMeshPoints(response.Landmarks),
            FacialTransformationMatrix = response.FacialTransformationMatrix,
            FaceContour = faceContour,
            LeftEyeContour = leftEye,
            RightEyeContour = rightEye,
            LeftBrowContour = leftBrow,
            RightBrowContour = rightBrow,
            OuterLipContour = outerLip,
            InnerLipContour = innerLip,
            JawContour = jaw
        };

        var feature = new FaceFeatureDetection
        {
            HasFace = true,
            Source = frame.Source,
            FaceBox = faceBox ?? new Rect(0, 0, 0, 0),
            LeftEyeBox = BoundingRect(leftEye),
            RightEyeBox = BoundingRect(rightEye),
            MouthBox = BoundingRect(outerLip.Count > 0 ? outerLip : innerLip),
            TrackingConfidence = frame.TrackingConfidence,
            EyeConfidence = frame.EyeConfidence,
            MouthConfidence = frame.MouthConfidence,
            FaceContour = frame.FaceContour,
            LeftEyeContour = frame.LeftEyeContour,
            RightEyeContour = frame.RightEyeContour,
            OuterLipContour = frame.OuterLipContour,
            InnerLipContour = frame.InnerLipContour,
            JawContour = frame.JawContour
        };

        return new FaceLandmarkTrackingResult
        {
            BackendName = backendName,
            BackendStatus = string.IsNullOrWhiteSpace(response.Status)
                ? "MediaPipe dense landmark lock"
                : response.Status,
            FeatureDetection = feature,
            LandmarkFrame = frame,
            Diagnostics = response.Diagnostics
        };
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> CreateDenseMeshPoints(IReadOnlyList<MediaPipeSidecarLandmark> landmarks)
    {
        var points = new List<FaceMeshLandmarkPoint>(landmarks.Count);
        for (var index = 0; index < landmarks.Count; index++)
        {
            var landmark = landmarks[index];
            points.Add(new FaceMeshLandmarkPoint
            {
                Index = index,
                X = Math.Clamp(landmark.X, 0d, 1d),
                Y = Math.Clamp(landmark.Y, 0d, 1d),
                Z = landmark.Z
            });
        }

        return points;
    }

    private static IReadOnlyList<Point> Select(IReadOnlyList<MediaPipeSidecarLandmark> landmarks, IReadOnlyList<int> indices)
    {
        var points = new List<Point>(indices.Count);
        foreach (var index in indices)
        {
            if (index >= 0 && index < landmarks.Count)
            {
                var landmark = landmarks[index];
                points.Add(new Point(
                    Math.Clamp(landmark.X, 0d, 1d),
                    Math.Clamp(landmark.Y, 0d, 1d)));
            }
        }

        return points;
    }

    private static (IReadOnlyList<Point> Left, IReadOnlyList<Point> Right) SortEyesByFramePosition(
        IReadOnlyList<Point> first,
        IReadOnlyList<Point> second)
    {
        var firstCenter = first.Count == 0 ? 0d : MeanX(first);
        var secondCenter = second.Count == 0 ? 1d : MeanX(second);
        return firstCenter <= secondCenter ? (first, second) : (second, first);
    }

    private static (double YawDegrees, double PitchDegrees, double RollDegrees) EstimatePoseDegrees(
        MediaPipeSidecarResponse response,
        IReadOnlyList<Point> leftEye,
        IReadOnlyList<Point> rightEye)
    {
        var fallbackRoll = EstimateRollDegrees(leftEye, rightEye);
        if (TryEstimatePoseFromMatrix(response.FacialTransformationMatrix, out var matrixPose))
        {
            return matrixPose;
        }

        return (
            EstimateYawDegrees(response.Landmarks),
            EstimatePitchDegrees(response.Landmarks),
            fallbackRoll);
    }

    private static bool TryEstimatePoseFromMatrix(
        IReadOnlyList<double> values,
        out (double YawDegrees, double PitchDegrees, double RollDegrees) pose)
    {
        pose = default;
        if (values.Count < 16)
        {
            return false;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (!double.IsFinite(values[index]))
            {
                return false;
            }
        }

        // MediaPipe supplies a 4x4 facial transformation matrix. Treat it as row-major here;
        // if a runtime omits or changes it, the landmark fallback below keeps pose populated.
        var r02 = values[2];
        var r10 = values[4];
        var r11 = values[5];
        var r12 = values[6];
        var r22 = values[10];
        var yaw = Math.Atan2(r02, r22) * 180d / Math.PI;
        var pitch = Math.Asin(Math.Clamp(-r12, -1d, 1d)) * 180d / Math.PI;
        var roll = Math.Atan2(r10, r11) * 180d / Math.PI;
        if (Math.Abs(yaw) > 80d || Math.Abs(pitch) > 70d || Math.Abs(roll) > 80d)
        {
            return false;
        }

        pose = (
            Math.Clamp(yaw, -55d, 55d),
            Math.Clamp(pitch, -45d, 45d),
            Math.Clamp(roll, -55d, 55d));
        return true;
    }

    private static double EstimateYawDegrees(IReadOnlyList<MediaPipeSidecarLandmark> landmarks)
    {
        if (!TryLandmark(landmarks, 1, out var noseTip)
            || !TryLandmark(landmarks, 234, out var leftCheek)
            || !TryLandmark(landmarks, 454, out var rightCheek))
        {
            return 0d;
        }

        var faceCenterX = (leftCheek.X + rightCheek.X) / 2d;
        var halfWidth = Math.Abs(rightCheek.X - leftCheek.X) / 2d;
        if (halfWidth <= 0.001d)
        {
            return 0d;
        }

        var lateralOffset = (noseTip.X - faceCenterX) / halfWidth;
        var depthAsymmetry = (rightCheek.Z - leftCheek.Z) / Math.Max(0.02d, halfWidth);
        return Math.Clamp((lateralOffset * 24d) + (depthAsymmetry * 10d), -45d, 45d);
    }

    private static double EstimatePitchDegrees(IReadOnlyList<MediaPipeSidecarLandmark> landmarks)
    {
        if (!TryLandmark(landmarks, 1, out var noseTip)
            || !TryLandmark(landmarks, 10, out var forehead)
            || !TryLandmark(landmarks, 152, out var chin))
        {
            return 0d;
        }

        var faceHeight = chin.Y - forehead.Y;
        if (faceHeight <= 0.001d)
        {
            return 0d;
        }

        var noseRatio = (noseTip.Y - forehead.Y) / faceHeight;
        var depthOffset = (noseTip.Z - ((forehead.Z + chin.Z) / 2d)) / Math.Max(0.02d, faceHeight);
        return Math.Clamp((noseRatio - 0.47d) * 85d - depthOffset * 8d, -35d, 35d);
    }

    private static bool TryLandmark(
        IReadOnlyList<MediaPipeSidecarLandmark> landmarks,
        int index,
        out MediaPipeSidecarLandmark landmark)
    {
        if (index >= 0 && index < landmarks.Count)
        {
            landmark = landmarks[index];
            return true;
        }

        landmark = new MediaPipeSidecarLandmark();
        return false;
    }

    private static Rect? BoundingRect(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var first = points[0];
        var minX = first.X;
        var maxX = first.X;
        var minY = first.Y;
        var maxY = first.Y;
        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        return maxX <= minX || maxY <= minY
            ? null
            : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double EstimateRollDegrees(IReadOnlyList<Point> leftEye, IReadOnlyList<Point> rightEye)
    {
        if (leftEye.Count == 0 || rightEye.Count == 0)
        {
            return 0d;
        }

        var leftCenter = Center(leftEye);
        var rightCenter = Center(rightEye);
        return Math.Atan2(rightCenter.Y - leftCenter.Y, rightCenter.X - leftCenter.X) * 180d / Math.PI;
    }

    private static Point Center(IReadOnlyList<Point> points)
    {
        var sumX = 0d;
        var sumY = 0d;
        foreach (var point in points)
        {
            sumX += point.X;
            sumY += point.Y;
        }

        return new Point(sumX / points.Count, sumY / points.Count);
    }

    private static double MeanX(IReadOnlyList<Point> points)
    {
        var sum = 0d;
        foreach (var point in points)
        {
            sum += point.X;
        }

        return sum / points.Count;
    }

    private static IReadOnlyDictionary<string, double> CreateBlendshapeDictionary(IReadOnlyList<MediaPipeSidecarBlendshape> blendshapes)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var blendshape in blendshapes)
        {
            if (!string.IsNullOrWhiteSpace(blendshape.CategoryName))
            {
                scores[blendshape.CategoryName] = Math.Clamp(blendshape.Score, 0d, 1d);
            }
        }

        return scores;
    }
}
