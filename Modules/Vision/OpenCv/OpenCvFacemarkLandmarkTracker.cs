using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.Face;
using CvRect = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvFacemarkLandmarkTracker : IStatefulFaceLandmarkTracker
{
    private const int LandmarkHoldFrameLimit = 6;
    private readonly OpenCvFacemarkModelInfo _modelInfo = OpenCvFacemarkModelInfo.Load();
    private readonly CascadeClassifier? _faceCascade;
    private readonly OpenCvYuNetFaceDetector _yuNetDetector = new();
    private FacemarkLBF? _facemark;
    private string _initializationStatus = "";
    private FaceLandmarkFrame? _lastLandmarkFrame;
    private FaceFeatureDetection? _lastFeatureDetection;
    private int _framesSinceLandmarkLock;

    public OpenCvFacemarkLandmarkTracker()
    {
        var cascadeRoot = Path.Combine(AppContext.BaseDirectory, "dependencies", "vision", "opencv", "haarcascades");
        _faceCascade = LoadCascade(Path.Combine(cascadeRoot, "haarcascade_frontalface_alt2.xml"));
    }

    public string Name => "OpenCV LBF facemark backend";

    public bool IsAvailable => _modelInfo.IsReady && _faceCascade is not null && EnsureFacemark();

    public string Status
    {
        get
        {
            if (!_modelInfo.IsReady)
            {
                return _modelInfo.Status;
            }

            if (_faceCascade is null)
            {
                return "OpenCV face cascade missing";
            }

            return string.IsNullOrWhiteSpace(_initializationStatus)
                ? "OpenCV LBF facemark waiting"
                : _initializationStatus;
        }
    }

    public int MaxDetectionDimension { get; set; } = 1280;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        if (!_modelInfo.IsReady)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = _modelInfo.Status
            };
        }

        if (_faceCascade is null)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = "OpenCV face cascade missing"
            };
        }

        if (!EnsureFacemark())
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = string.IsNullOrWhiteSpace(_initializationStatus)
                    ? "OpenCV LBF facemark unavailable"
                    : _initializationStatus
            };
        }

        using var gray = CreateGrayMat(bitmap);
        using var small = new Mat();
        var detectionDimension = Math.Clamp(MaxDetectionDimension, 320, 1920);
        var scale = Math.Min(1d, detectionDimension / (double)Math.Max(gray.Width, gray.Height));
        if (scale < 1d)
        {
            Cv2.Resize(gray, small, new OpenCvSharp.Size(Math.Max(1, (int)(gray.Width * scale)), Math.Max(1, (int)(gray.Height * scale))));
        }
        else
        {
            gray.CopyTo(small);
        }

        Cv2.EqualizeHist(small, small);
        var face = DetectPrimaryFace(small);
        if (face.Width <= 0 || face.Height <= 0)
        {
            return TryCreateHeldResult(capturedAtUtc, "LBF model ready; searching for face") ?? new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = "LBF model ready; searching for face"
            };
        }

        Point2f[][] landmarks;
        using (var image = InputArray.Create(small))
        using (var faces = InputArray.Create(new[] { face }))
        {
            var fit = _facemark!.Fit(image, faces, out landmarks);
            if (!fit || landmarks.Length == 0 || landmarks[0].Length < 68)
            {
                var status = $"LBF face lock but landmark fit failed ({landmarks.Length} face result{(landmarks.Length == 1 ? "" : "s")})";
                return TryCreateHeldResult(capturedAtUtc, status) ?? new FaceLandmarkTrackingResult
                {
                    BackendName = Name,
                    BackendStatus = status
                };
            }
        }

        var landmarkFrame = CreateLandmarkFrame(face, landmarks[0], small.Width, small.Height, capturedAtUtc);
        var featureDetection = CreateFeatureDetection(face, landmarkFrame, small.Width, small.Height);
        RememberLock(landmarkFrame, featureDetection);
        return new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = "LBF 68-point landmark lock",
            FeatureDetection = featureDetection,
            LandmarkFrame = landmarkFrame
        };
    }

    public void Reset()
    {
        _lastLandmarkFrame = null;
        _lastFeatureDetection = null;
        _framesSinceLandmarkLock = 0;
    }

    public void Dispose()
    {
        _facemark?.Dispose();
        _yuNetDetector.Dispose();
        _faceCascade?.Dispose();
    }

    public static FaceLandmarkFrame CreateLandmarkFrameFrom68Points(
        IReadOnlyList<WpfPoint> normalizedPoints,
        DateTime capturedAtUtc,
        string source = "OpenCV LBF 68-point facemark")
    {
        if (normalizedPoints.Count < 68)
        {
            return FaceLandmarkFrame.None;
        }

        var leftEye = Slice(normalizedPoints, 36, 6);
        var rightEye = Slice(normalizedPoints, 42, 6);
        var firstBrow = Slice(normalizedPoints, 17, 5);
        var secondBrow = Slice(normalizedPoints, 22, 5);
        var (leftBrow, rightBrow) = SortByFramePosition(firstBrow, secondBrow);
        var outerLip = Slice(normalizedPoints, 48, 12);
        var innerLip = Slice(normalizedPoints, 60, 8);
        var jaw = Slice(normalizedPoints, 0, 17);

        return new FaceLandmarkFrame
        {
            HasFace = true,
            Source = source,
            CapturedAtUtc = capturedAtUtc,
            TrackingConfidence = 0.82d,
            EyeConfidence = 0.78d,
            MouthConfidence = 0.78d,
            HeadYawDegrees = EstimateYawDegrees(normalizedPoints),
            HeadPitchDegrees = EstimatePitchDegrees(normalizedPoints),
            HeadRollDegrees = EstimateRollDegrees(leftEye, rightEye),
            FaceContour = CreateFaceContour(normalizedPoints),
            LeftEyeContour = leftEye,
            RightEyeContour = rightEye,
            LeftBrowContour = leftBrow,
            RightBrowContour = rightBrow,
            OuterLipContour = outerLip,
            InnerLipContour = innerLip,
            JawContour = jaw
        };
    }

    private bool EnsureFacemark()
    {
        if (_facemark is not null)
        {
            return true;
        }

        if (!_modelInfo.IsReady)
        {
            _initializationStatus = _modelInfo.Status;
            return false;
        }

        try
        {
            using var parameters = new FacemarkLBF.Params
            {
                ModelFilename = _modelInfo.ModelPath
            };
            _facemark = FacemarkLBF.Create(parameters);
            _facemark.LoadModel(_modelInfo.ModelPath);
            _initializationStatus = "OpenCV LBF facemark model loaded";
            return true;
        }
        catch (Exception ex)
        {
            _facemark?.Dispose();
            _facemark = null;
            _initializationStatus = $"OpenCV LBF facemark failed to load: {ex.Message}";
            return false;
        }
    }

    private void RememberLock(FaceLandmarkFrame landmarkFrame, FaceFeatureDetection featureDetection)
    {
        _lastLandmarkFrame = landmarkFrame;
        _lastFeatureDetection = featureDetection;
        _framesSinceLandmarkLock = 0;
    }

    private FaceLandmarkTrackingResult? TryCreateHeldResult(DateTime capturedAtUtc, string missStatus)
    {
        if (_lastLandmarkFrame is null
            || _lastFeatureDetection is null
            || _framesSinceLandmarkLock >= LandmarkHoldFrameLimit)
        {
            return null;
        }

        _framesSinceLandmarkLock++;
        var decay = Math.Pow(0.78d, _framesSinceLandmarkLock);
        return new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = $"{missStatus}; LBF temporal landmark hold {_framesSinceLandmarkLock}/{LandmarkHoldFrameLimit}",
            FeatureDetection = CreateHeldFeatureDetection(_lastFeatureDetection, decay),
            LandmarkFrame = CreateHeldLandmarkFrame(_lastLandmarkFrame, capturedAtUtc, _framesSinceLandmarkLock, decay)
        };
    }

    private static FaceLandmarkFrame CreateHeldLandmarkFrame(
        FaceLandmarkFrame source,
        DateTime capturedAtUtc,
        int framesSinceLock,
        double decay)
    {
        return new FaceLandmarkFrame
        {
            HasFace = true,
            Source = $"{source.Source}; LBF temporal hold {framesSinceLock}/{LandmarkHoldFrameLimit}",
            CapturedAtUtc = capturedAtUtc,
            TrackingConfidence = Math.Max(0.24d, source.TrackingConfidence * decay),
            EyeConfidence = Math.Max(0.22d, source.EyeConfidence * decay),
            MouthConfidence = Math.Max(0.20d, source.MouthConfidence * decay),
            HeadYawDegrees = source.HeadYawDegrees,
            HeadPitchDegrees = source.HeadPitchDegrees,
            HeadRollDegrees = source.HeadRollDegrees,
            FaceContour = source.FaceContour,
            LeftEyeContour = source.LeftEyeContour,
            RightEyeContour = source.RightEyeContour,
            LeftBrowContour = source.LeftBrowContour,
            RightBrowContour = source.RightBrowContour,
            OuterLipContour = source.OuterLipContour,
            InnerLipContour = source.InnerLipContour,
            JawContour = source.JawContour
        };
    }

    private static FaceFeatureDetection CreateHeldFeatureDetection(FaceFeatureDetection source, double decay)
    {
        return new FaceFeatureDetection
        {
            HasFace = true,
            Source = $"{source.Source}; temporal hold",
            FaceBox = source.FaceBox,
            LeftEyeBox = source.LeftEyeBox,
            RightEyeBox = source.RightEyeBox,
            MouthBox = source.MouthBox,
            TrackingConfidence = Math.Max(0.24d, source.TrackingConfidence * decay),
            EyeConfidence = Math.Max(0.22d, source.EyeConfidence * decay),
            MouthConfidence = Math.Max(0.20d, source.MouthConfidence * decay),
            FaceContour = source.FaceContour,
            LeftEyeContour = source.LeftEyeContour,
            RightEyeContour = source.RightEyeContour,
            OuterLipContour = source.OuterLipContour,
            InnerLipContour = source.InnerLipContour,
            JawContour = source.JawContour
        };
    }

    private CvRect DetectPrimaryFace(Mat gray)
    {
        var previousFace = GetPreviousFace(gray.Width, gray.Height);
        if (previousFace is CvRect lastFace)
        {
            var localFace = DetectLocalFace(gray, lastFace, _framesSinceLandmarkLock);
            if (localFace.Width > 0 && localFace.Height > 0)
            {
                return localFace;
            }
        }

        var yuNetFaces = _yuNetDetector.DetectAll(gray);
        var yuNetCandidate = FaceCandidateSelector.SelectBest(
            yuNetFaces.Select(face => new FaceCandidate(face.FaceBox, $"YuNet DNN lock {face.Score:P0}", face, face.Score)),
            previousFace,
            gray.Width,
            gray.Height);
        if (yuNetCandidate is not null
            && FaceCandidateSelector.IsAcceptableTrackingCandidate(yuNetCandidate, previousFace, gray.Width, gray.Height, _framesSinceLandmarkLock))
        {
            return yuNetCandidate.Face;
        }

        var faces = _faceCascade?.DetectMultiScale(
            gray,
            1.08,
            4,
            HaarDetectionTypes.ScaleImage,
            new OpenCvSharp.Size(Math.Max(40, gray.Width / 12), Math.Max(40, gray.Height / 12))) ?? [];

        var candidate = FaceCandidateSelector.SelectBest(
            faces.Select(face => new FaceCandidate(face, "global Haar lock", null, 0.58d)),
            previousFace,
            gray.Width,
            gray.Height);
        return candidate is not null
            && FaceCandidateSelector.IsAcceptableTrackingCandidate(candidate, previousFace, gray.Width, gray.Height, _framesSinceLandmarkLock)
            ? candidate.Face
            : default;
    }

    private CvRect? GetPreviousFace(int width, int height)
    {
        if (_lastFeatureDetection is not { HasFace: true } detection
            || detection.FaceBox.Width <= 0d
            || detection.FaceBox.Height <= 0d)
        {
            return null;
        }

        return new CvRect(
            (int)Math.Round(detection.FaceBox.X * width),
            (int)Math.Round(detection.FaceBox.Y * height),
            Math.Max(1, (int)Math.Round(detection.FaceBox.Width * width)),
            Math.Max(1, (int)Math.Round(detection.FaceBox.Height * height)));
    }

    private CvRect DetectLocalFace(Mat gray, CvRect lastFace, int framesSinceLock)
    {
        var expansion = 0.58d + Math.Clamp(framesSinceLock, 0, LandmarkHoldFrameLimit) * 0.14d;
        var search = ExpandRect(lastFace, expansion, gray.Width, gray.Height);
        if (search.Width < Math.Max(40, gray.Width / 14) || search.Height < Math.Max(40, gray.Height / 14))
        {
            return default;
        }

        using var view = new Mat(gray, search);
        var faces = _faceCascade?.DetectMultiScale(
            view,
            1.05,
            2,
            HaarDetectionTypes.ScaleImage,
            new OpenCvSharp.Size(Math.Max(28, search.Width / 8), Math.Max(28, search.Height / 8))) ?? [];

        var localCandidates = faces
            .Where(rect => IsPlausibleLocalFace(rect, search))
            .Select(rect => new FaceCandidate(
                new CvRect(search.X + rect.X, search.Y + rect.Y, rect.Width, rect.Height),
                "local LBF reacquire",
                null,
                0.56d));
        var localFace = FaceCandidateSelector.SelectBest(localCandidates, lastFace, gray.Width, gray.Height);
        return localFace is null
            ? default
            : localFace.Face;
    }

    private static bool IsPlausibleLocalFace(CvRect localFace, CvRect search)
    {
        if (localFace.Width <= 0 || localFace.Height <= 0)
        {
            return false;
        }

        var aspect = localFace.Width / (double)Math.Max(1, localFace.Height);
        var areaRatio = localFace.Width * localFace.Height / (double)Math.Max(1, search.Width * search.Height);
        return aspect is > 0.58d and < 1.55d
            && areaRatio is > 0.055d and < 0.96d;
    }

    private static CvRect ExpandRect(CvRect rect, double fraction, int width, int height)
    {
        var expandX = (int)Math.Round(rect.Width * fraction);
        var expandY = (int)Math.Round(rect.Height * fraction);
        return ClampRect(
            new CvRect(
                rect.X - expandX,
                rect.Y - expandY,
                rect.Width + expandX * 2,
                rect.Height + expandY * 2),
            width,
            height);
    }

    private static CvRect ClampRect(CvRect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, x + 1, width);
        var bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return new CvRect(x, y, right - x, bottom - y);
    }

    private static FaceLandmarkFrame CreateLandmarkFrame(
        CvRect face,
        IReadOnlyList<Point2f> points,
        int width,
        int height,
        DateTime capturedAtUtc)
    {
        var normalized = points
            .Select(point => new WpfPoint(
                Math.Clamp(point.X / Math.Max(1d, width), 0d, 1d),
                Math.Clamp(point.Y / Math.Max(1d, height), 0d, 1d)))
            .ToList();
        var frame = CreateLandmarkFrameFrom68Points(normalized, capturedAtUtc);
        var faceConfidence = CalculateFaceConfidence(face, width, height);
        return new FaceLandmarkFrame
        {
            HasFace = frame.HasFace,
            Source = frame.Source,
            CapturedAtUtc = frame.CapturedAtUtc,
            TrackingConfidence = Math.Max(frame.TrackingConfidence, faceConfidence),
            EyeConfidence = frame.EyeConfidence,
            MouthConfidence = frame.MouthConfidence,
            HeadYawDegrees = frame.HeadYawDegrees,
            HeadPitchDegrees = frame.HeadPitchDegrees,
            HeadRollDegrees = frame.HeadRollDegrees,
            FaceContour = frame.FaceContour,
            LeftEyeContour = frame.LeftEyeContour,
            RightEyeContour = frame.RightEyeContour,
            LeftBrowContour = frame.LeftBrowContour,
            RightBrowContour = frame.RightBrowContour,
            OuterLipContour = frame.OuterLipContour,
            InnerLipContour = frame.InnerLipContour,
            JawContour = frame.JawContour
        };
    }

    private static FaceFeatureDetection CreateFeatureDetection(
        CvRect face,
        FaceLandmarkFrame landmarkFrame,
        int width,
        int height)
    {
        var leftEyeBox = BoundingRect(landmarkFrame.LeftEyeContour);
        var rightEyeBox = BoundingRect(landmarkFrame.RightEyeContour);
        var mouthBox = BoundingRect(landmarkFrame.OuterLipContour.Count >= 4
            ? landmarkFrame.OuterLipContour
            : landmarkFrame.InnerLipContour);
        return new FaceFeatureDetection
        {
            HasFace = true,
            Source = "OpenCV LBF 68-point facemark",
            FaceBox = ToNormalizedRect(face, width, height),
            LeftEyeBox = leftEyeBox,
            RightEyeBox = rightEyeBox,
            MouthBox = mouthBox,
            TrackingConfidence = landmarkFrame.TrackingConfidence,
            EyeConfidence = landmarkFrame.EyeConfidence,
            MouthConfidence = landmarkFrame.MouthConfidence,
            FaceContour = landmarkFrame.FaceContour,
            LeftEyeContour = landmarkFrame.LeftEyeContour,
            RightEyeContour = landmarkFrame.RightEyeContour,
            OuterLipContour = landmarkFrame.OuterLipContour,
            InnerLipContour = landmarkFrame.InnerLipContour,
            JawContour = landmarkFrame.JawContour
        };
    }

    private static WpfRect ToNormalizedRect(CvRect rect, int width, int height)
    {
        return new WpfRect(
            rect.X / (double)Math.Max(1, width),
            rect.Y / (double)Math.Max(1, height),
            rect.Width / (double)Math.Max(1, width),
            rect.Height / (double)Math.Max(1, height));
    }

    private static WpfRect? BoundingRect(IReadOnlyList<WpfPoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        return new WpfRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double EstimateYawDegrees(IReadOnlyList<WpfPoint> points)
    {
        if (points.Count < 68)
        {
            return 0d;
        }

        var leftCheek = points[0];
        var rightCheek = points[16];
        var noseTip = points[30];
        var faceCenterX = (leftCheek.X + rightCheek.X) / 2d;
        var halfWidth = Math.Abs(rightCheek.X - leftCheek.X) / 2d;
        if (halfWidth <= 0.001d)
        {
            return 0d;
        }

        return Math.Clamp((noseTip.X - faceCenterX) / halfWidth * 34d, -45d, 45d);
    }

    private static double EstimatePitchDegrees(IReadOnlyList<WpfPoint> points)
    {
        if (points.Count < 68)
        {
            return 0d;
        }

        var eyeY = (AverageY(points, 36, 6) + AverageY(points, 42, 6)) / 2d;
        var mouthY = AverageY(points, 48, 12);
        var noseTip = points[30];
        var eyeToMouth = mouthY - eyeY;
        if (eyeToMouth <= 0.001d)
        {
            return 0d;
        }

        var noseRatio = (noseTip.Y - eyeY) / eyeToMouth;
        return Math.Clamp((noseRatio - 0.52d) * 50d, -35d, 35d);
    }

    private static double AverageY(IReadOnlyList<WpfPoint> points, int start, int count)
    {
        return points.Skip(start).Take(count).Average(static point => point.Y);
    }

    private static IReadOnlyList<WpfPoint> Slice(IReadOnlyList<WpfPoint> points, int start, int count)
    {
        return points.Skip(start).Take(count).ToList();
    }

    private static (IReadOnlyList<WpfPoint> Left, IReadOnlyList<WpfPoint> Right) SortByFramePosition(
        IReadOnlyList<WpfPoint> first,
        IReadOnlyList<WpfPoint> second)
    {
        var firstCenter = first.Count == 0 ? 0d : first.Average(static point => point.X);
        var secondCenter = second.Count == 0 ? 1d : second.Average(static point => point.X);
        return firstCenter <= secondCenter ? (first, second) : (second, first);
    }

    private static IReadOnlyList<WpfPoint> CreateFaceContour(IReadOnlyList<WpfPoint> points)
    {
        var jaw = Slice(points, 0, 17).ToList();
        var leftBrow = Slice(points, 17, 5);
        var rightBrow = Slice(points, 22, 5);
        if (jaw.Count < 17 || leftBrow.Count < 5 || rightBrow.Count < 5)
        {
            return jaw;
        }

        var browTop = Math.Min(leftBrow.Min(point => point.Y), rightBrow.Min(point => point.Y));
        var jawTop = Math.Min(jaw[0].Y, jaw[^1].Y);
        var foreheadY = Math.Clamp(browTop - Math.Abs(jawTop - browTop) * 0.72d, 0d, 1d);
        var centerX = jaw.Average(point => point.X);
        var upper = new List<WpfPoint>
        {
            new((jaw[0].X * 0.88d) + (centerX * 0.12d), jaw[0].Y),
            new((leftBrow[0].X * 0.78d) + (jaw[0].X * 0.22d), (leftBrow[0].Y + foreheadY) / 2d),
            new((leftBrow[2].X + centerX) / 2d, foreheadY),
            new(centerX, Math.Max(0d, foreheadY - 0.018d)),
            new((rightBrow[2].X + centerX) / 2d, foreheadY),
            new((rightBrow[^1].X * 0.78d) + (jaw[^1].X * 0.22d), (rightBrow[^1].Y + foreheadY) / 2d),
            new((jaw[^1].X * 0.88d) + (centerX * 0.12d), jaw[^1].Y)
        };

        upper.AddRange(jaw.AsEnumerable().Reverse().Skip(1).Take(15));
        return upper;
    }

    private static double EstimateRollDegrees(IReadOnlyList<WpfPoint> leftEye, IReadOnlyList<WpfPoint> rightEye)
    {
        if (leftEye.Count == 0 || rightEye.Count == 0)
        {
            return 0d;
        }

        var leftCenter = new WpfPoint(leftEye.Average(point => point.X), leftEye.Average(point => point.Y));
        var rightCenter = new WpfPoint(rightEye.Average(point => point.X), rightEye.Average(point => point.Y));
        return Math.Atan2(rightCenter.Y - leftCenter.Y, rightCenter.X - leftCenter.X) * 180d / Math.PI;
    }

    private static double CalculateFaceConfidence(CvRect face, int width, int height)
    {
        var frameArea = Math.Max(1d, width * height);
        var faceArea = face.Width * face.Height;
        var relativeArea = faceArea / frameArea;
        var sizeScore = Math.Clamp(relativeArea / 0.12d, 0d, 1d);
        return Math.Clamp(0.50d + sizeScore * 0.34d, 0.50d, 0.84d);
    }

    private static CascadeClassifier? LoadCascade(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var cascade = new CascadeClassifier(path);
        return cascade.Empty() ? null : cascade;
    }

    private static Mat CreateGrayMat(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        using var bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels);
        var gray = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        return gray;
    }
}
