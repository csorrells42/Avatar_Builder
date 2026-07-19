using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvSolvePnPHeadPoseEstimate
{
    public static OpenCvSolvePnPHeadPoseEstimate Waiting(string status) => new()
    {
        Status = status
    };

    public bool HasPose { get; init; }

    public double YawDegrees { get; init; }

    public double PitchDegrees { get; init; }

    public double RollDegrees { get; init; }

    public double ReprojectionErrorPixels { get; init; }

    public int LandmarkCount { get; init; }

    public string Status { get; init; } = "";
}

public static class OpenCvSolvePnPHeadPoseSolver
{
    private static readonly PoseLandmark[] PoseLandmarks =
    [
        new(1, 0f, 0f, 0f),          // Nose tip
        new(152, 0f, -72f, -18f),    // Chin
        new(33, -48f, 36f, -36f),    // Left eye outer corner
        new(263, 48f, 36f, -36f),    // Right eye outer corner
        new(61, -34f, -24f, -30f),   // Left mouth corner
        new(291, 34f, -24f, -30f),   // Right mouth corner
        new(10, 0f, 82f, -24f),      // Forehead
        new(234, -72f, 2f, -44f),    // Left cheek
        new(454, 72f, 2f, -44f)      // Right cheek
    ];

    public static OpenCvSolvePnPHeadPoseEstimate Estimate(
        FaceLandmarkFrame frame,
        int? frameWidthPixels,
        int? frameHeightPixels,
        HeadPoseCalibration? calibration)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!frame.HasDenseMesh)
        {
            return OpenCvSolvePnPHeadPoseEstimate.Waiting("solvePnP waiting for dense MediaPipe mesh");
        }

        if (frameWidthPixels is not > 0 || frameHeightPixels is not > 0)
        {
            return OpenCvSolvePnPHeadPoseEstimate.Waiting("solvePnP waiting for frame size");
        }

        var meshByIndex = frame.DenseMeshPoints.ToDictionary(static point => point.Index);
        var objectPoints = new List<Point3f>(PoseLandmarks.Length);
        var imagePoints = new List<Point2f>(PoseLandmarks.Length);
        foreach (var landmark in PoseLandmarks)
        {
            if (!meshByIndex.TryGetValue(landmark.Index, out var point)
                || !double.IsFinite(point.X)
                || !double.IsFinite(point.Y))
            {
                continue;
            }

            objectPoints.Add(new Point3f(landmark.X, landmark.Y, landmark.Z));
            imagePoints.Add(new Point2f(
                (float)(Math.Clamp(point.X, 0d, 1d) * frameWidthPixels.Value),
                (float)(Math.Clamp(point.Y, 0d, 1d) * frameHeightPixels.Value)));
        }

        if (objectPoints.Count < 6)
        {
            return OpenCvSolvePnPHeadPoseEstimate.Waiting($"solvePnP needs at least 6 anchor landmarks; got {objectPoints.Count}");
        }

        var focalPixels = EstimateFocalPixels(frameWidthPixels.Value, frameHeightPixels.Value, calibration);
        var cameraMatrix = new[,]
        {
            { focalPixels, 0d, frameWidthPixels.Value / 2d },
            { 0d, focalPixels, frameHeightPixels.Value / 2d },
            { 0d, 0d, 1d }
        };
        double[] distortionCoefficients = [0d, 0d, 0d, 0d];

        try
        {
            using var rotationVectorMat = new Mat();
            using var translationVectorMat = new Mat();
            using var objectInput = InputArray.Create<Point3f>(objectPoints, MatType.CV_32FC3);
            using var imageInput = InputArray.Create<Point2f>(imagePoints, MatType.CV_32FC2);
            using var cameraInput = InputArray.Create(cameraMatrix, MatType.CV_64FC1);
            using var distortionInput = InputArray.Create(distortionCoefficients, MatType.CV_64FC1);
            using var rotationOutput = OutputArray.Create(rotationVectorMat);
            using var translationOutput = OutputArray.Create(translationVectorMat);
            Cv2.SolvePnP(
                objectInput,
                imageInput,
                cameraInput,
                distortionInput,
                rotationOutput,
                translationOutput,
                useExtrinsicGuess: false,
                flags: SolvePnPMethod.Iterative);

            rotationVectorMat.GetArray(out double[] rotationVector);
            translationVectorMat.GetArray(out double[] translationVector);
            if (rotationVector.Length < 3 || translationVector.Length < 3)
            {
                return OpenCvSolvePnPHeadPoseEstimate.Waiting("solvePnP did not converge");
            }

            using var rotationMatrix = new Mat();
            Cv2.Rodrigues(rotationVectorMat, rotationMatrix);

            var euler = RotationMatrixToEulerDegrees(rotationMatrix);
            var reprojectionError = CalculateReprojectionError(
                objectPoints,
                imagePoints,
                rotationVector,
                translationVector,
                cameraMatrix,
                distortionCoefficients);

            return new OpenCvSolvePnPHeadPoseEstimate
            {
                HasPose = true,
                YawDegrees = ClampPose(euler.YawDegrees, -70d, 70d),
                PitchDegrees = ClampPose(euler.PitchDegrees, -60d, 60d),
                RollDegrees = ClampPose(euler.RollDegrees, -70d, 70d),
                ReprojectionErrorPixels = reprojectionError,
                LandmarkCount = objectPoints.Count,
                Status = $"solvePnP lock from {objectPoints.Count} MediaPipe anchors"
            };
        }
        catch (OpenCVException ex)
        {
            return OpenCvSolvePnPHeadPoseEstimate.Waiting($"solvePnP failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return OpenCvSolvePnPHeadPoseEstimate.Waiting($"solvePnP failed: {ex.Message}");
        }
    }

    private static double EstimateFocalPixels(int width, int height, HeadPoseCalibration? calibration)
    {
        if (calibration?.CameraHorizontalFovDegrees is > 0d and < 180d)
        {
            var radians = calibration.CameraHorizontalFovDegrees.Value * Math.PI / 180d;
            return width / (2d * Math.Tan(radians / 2d));
        }

        return Math.Max(width, height);
    }

    private static double CalculateReprojectionError(
        IReadOnlyList<Point3f> objectPoints,
        IReadOnlyList<Point2f> imagePoints,
        double[] rotationVector,
        double[] translationVector,
        double[,] cameraMatrix,
        double[] distortionCoefficients)
    {
        Cv2.ProjectPoints(
            objectPoints,
            rotationVector,
            translationVector,
            cameraMatrix,
            distortionCoefficients,
            out var projected,
            out _);
        if (projected.Length == 0)
        {
            return 0d;
        }

        var error = 0d;
        var count = Math.Min(projected.Length, imagePoints.Count);
        for (var i = 0; i < count; i++)
        {
            var dx = projected[i].X - imagePoints[i].X;
            var dy = projected[i].Y - imagePoints[i].Y;
            error += Math.Sqrt(dx * dx + dy * dy);
        }

        return error / Math.Max(1, count);
    }

    private static (double YawDegrees, double PitchDegrees, double RollDegrees) RotationMatrixToEulerDegrees(Mat rotation)
    {
        var r00 = rotation.At<double>(0, 0);
        var r10 = rotation.At<double>(1, 0);
        var r20 = rotation.At<double>(2, 0);
        var r21 = rotation.At<double>(2, 1);
        var r22 = rotation.At<double>(2, 2);
        var sy = Math.Sqrt(r00 * r00 + r10 * r10);

        var pitch = Math.Atan2(r21, r22) * 180d / Math.PI;
        var yaw = Math.Atan2(-r20, sy) * 180d / Math.PI;
        var roll = Math.Atan2(r10, r00) * 180d / Math.PI;
        return (yaw, pitch, roll);
    }

    private static double ClampPose(double value, double min, double max)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : 0d;
    }

    private readonly record struct PoseLandmark(int Index, float X, float Y, float Z);
}
