using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using CvRect = OpenCvSharp.Rect;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvYuNetFaceDetector : IDisposable
{
    private readonly OpenCvYuNetModelInfo _modelInfo = OpenCvYuNetModelInfo.Load();
    private FaceDetectorYN? _detector;
    private OpenCvSharp.Size _inputSize;
    private string _initializationStatus = "";

    public bool IsAvailable => _modelInfo.IsReady && EnsureDetector(new OpenCvSharp.Size(320, 320));

    public string Status
    {
        get
        {
            if (!_modelInfo.IsReady)
            {
                return _modelInfo.Status;
            }

            return string.IsNullOrWhiteSpace(_initializationStatus)
                ? "OpenCV YuNet face detector waiting"
                : _initializationStatus;
        }
    }

    public YuNetFaceDetection? Detect(Mat gray)
    {
        return DetectAll(gray)
            .OrderByDescending(static face => face.Score)
            .FirstOrDefault();
    }

    public IReadOnlyList<YuNetFaceDetection> DetectAll(Mat gray)
    {
        if (!_modelInfo.IsReady || gray.Empty())
        {
            return [];
        }

        var inputSize = new OpenCvSharp.Size(gray.Width, gray.Height);
        if (!EnsureDetector(inputSize))
        {
            return [];
        }

        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        using var faces = new Mat();
        _detector!.Detect(bgr, faces);
        return ParseFaces(faces, gray.Width, gray.Height);
    }

    public void Dispose()
    {
        _detector?.Dispose();
    }

    private bool EnsureDetector(OpenCvSharp.Size inputSize)
    {
        if (!_modelInfo.IsReady)
        {
            _initializationStatus = _modelInfo.Status;
            return false;
        }

        if (_detector is not null && _inputSize == inputSize)
        {
            return true;
        }

        try
        {
            _detector?.Dispose();
            _detector = FaceDetectorYN.Create(
                _modelInfo.ModelPath,
                "",
                inputSize,
                0.70f,
                0.30f,
                5000,
                (Backend)0,
                (Target)0);
            _inputSize = inputSize;
            _initializationStatus = "OpenCV YuNet face detector loaded";
            return true;
        }
        catch (Exception ex)
        {
            _detector?.Dispose();
            _detector = null;
            _inputSize = default;
            _initializationStatus = $"OpenCV YuNet face detector failed to load: {ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<YuNetFaceDetection> ParseFaces(Mat faces, int width, int height)
    {
        var rows = faces.Rows;
        var cols = faces.Cols;
        if (faces.Empty() || rows <= 0 || cols < 15)
        {
            return [];
        }

        var detections = new List<YuNetFaceDetection>(rows);
        for (var row = 0; row < rows; row++)
        {
            var score = faces.At<float>(row, 14);
            var x = faces.At<float>(row, 0);
            var y = faces.At<float>(row, 1);
            var w = faces.At<float>(row, 2);
            var h = faces.At<float>(row, 3);
            var rect = ClampRect(
                new CvRect(
                    (int)Math.Round(x),
                    (int)Math.Round(y),
                    (int)Math.Round(w),
                    (int)Math.Round(h)),
                width,
                height);

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            detections.Add(new YuNetFaceDetection(
                rect,
                new Point2f(faces.At<float>(row, 4), faces.At<float>(row, 5)),
                new Point2f(faces.At<float>(row, 6), faces.At<float>(row, 7)),
                new Point2f(faces.At<float>(row, 8), faces.At<float>(row, 9)),
                new Point2f(faces.At<float>(row, 10), faces.At<float>(row, 11)),
                new Point2f(faces.At<float>(row, 12), faces.At<float>(row, 13)),
                Math.Clamp(score, 0d, 1d)));
        }

        return detections
            .OrderByDescending(static detection => detection.Score)
            .ToList();
    }

    private static CvRect ClampRect(CvRect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, x + 1, width);
        var bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return new CvRect(x, y, right - x, bottom - y);
    }
}

public sealed record YuNetFaceDetection(
    CvRect FaceBox,
    Point2f RightEye,
    Point2f LeftEye,
    Point2f NoseTip,
    Point2f RightMouthCorner,
    Point2f LeftMouthCorner,
    double Score);
