using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvFaceFeatureTracker : IDisposable
{
    private const int FaceHoldFrameLimit = 8;
    private readonly CascadeClassifier? _faceCascade;
    private readonly CascadeClassifier? _eyeCascade;
    private readonly CascadeClassifier? _mouthCascade;
    private readonly OpenCvYuNetFaceDetector _yuNetDetector = new();
    private CvRect? _lastFace;
    private int _framesSinceFaceLock;

    public int MaxDetectionDimension { get; set; } = 960;

    public OpenCvFaceFeatureTracker()
    {
        var cascadeRoot = Path.Combine(AppContext.BaseDirectory, "dependencies", "vision", "opencv", "haarcascades");
        _faceCascade = LoadCascade(Path.Combine(cascadeRoot, "haarcascade_frontalface_alt2.xml"));
        _eyeCascade = LoadCascade(Path.Combine(cascadeRoot, "haarcascade_eye_tree_eyeglasses.xml"));
        _mouthCascade = LoadCascade(Path.Combine(cascadeRoot, "haarcascade_smile.xml"));
    }

    public bool IsAvailable => _faceCascade is not null && _eyeCascade is not null && _mouthCascade is not null;

    public FaceFeatureDetection Detect(BitmapSource bitmap)
    {
        if (!IsAvailable || _faceCascade is null || _eyeCascade is null || _mouthCascade is null)
        {
            return FaceFeatureDetection.None;
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
        var locatedFace = DetectPrimaryFace(small);
        var face = locatedFace.Face;
        if (face.Width <= 0 || face.Height <= 0)
        {
            var held = TryCreateHeldFaceDetection(small);
            if (held.HasFace)
            {
                return held;
            }

            _framesSinceFaceLock++;
            return FaceFeatureDetection.None;
        }

        RememberFace(face);
        var yuNetCueBoxes = locatedFace.YuNetFace is null
            ? null
            : EstimateCueBoxesFromYuNet(locatedFace.YuNetFace, small.Width, small.Height);
        var detectedLeftEye = DetectEye(small, face, _eyeCascade, leftSide: true);
        var detectedRightEye = DetectEye(small, face, _eyeCascade, leftSide: false);
        var detectedMouth = DetectMouth(small, face, _mouthCascade);
        var estimatedLeftEye = ClampRect(EstimateEyeBoxFromFace(face, leftSide: true), small.Width, small.Height);
        var estimatedRightEye = ClampRect(EstimateEyeBoxFromFace(face, leftSide: false), small.Width, small.Height);
        var estimatedMouth = ClampRect(EstimateMouthBoxFromFace(face), small.Width, small.Height);

        var leftEyeRefinement = ChooseBestEyeRefinement(
            small,
            face,
            leftSide: true,
            detectedLeftEye,
            yuNetCueBoxes?.LeftEye,
            estimatedLeftEye);
        var rightEyeRefinement = ChooseBestEyeRefinement(
            small,
            face,
            leftSide: false,
            detectedRightEye,
            yuNetCueBoxes?.RightEye,
            estimatedRightEye);
        var mouthRefinement = ChooseBestMouthRefinement(
            small,
            face,
            detectedMouth,
            yuNetCueBoxes?.Mouth,
            estimatedMouth);
        var leftEye = leftEyeRefinement.Box;
        var rightEye = rightEyeRefinement.Box;
        var mouth = mouthRefinement.Box;
        var leftEyeAperture = leftEyeRefinement.Estimate;
        var rightEyeAperture = rightEyeRefinement.Estimate;
        var mouthAperture = mouthRefinement.Estimate;

        if (!leftEyeAperture.HasAperture)
        {
            leftEyeAperture = OpenCvApertureEstimator.FromBox(leftEye, 0.34d, 0.20d);
        }

        if (!rightEyeAperture.HasAperture)
        {
            rightEyeAperture = OpenCvApertureEstimator.FromBox(rightEye, 0.34d, 0.20d);
        }

        if (!mouthAperture.HasAperture)
        {
            mouthAperture = OpenCvApertureEstimator.FromBox(mouth, 0.26d, 0.18d);
        }

        return new FaceFeatureDetection
        {
            HasFace = true,
            Source = $"OpenCV Haar dynamic face tracker with aperture refinement ({locatedFace.Source}{(yuNetCueBoxes is null ? "" : ", YuNet cue boxes")})",
            FaceBox = ToNormalizedRect(face, small.Width, small.Height),
            LeftEyeBox = ToNormalizedRect(leftEye, small.Width, small.Height),
            RightEyeBox = ToNormalizedRect(rightEye, small.Width, small.Height),
            MouthBox = ToNormalizedRect(mouth, small.Width, small.Height),
            TrackingConfidence = CalculateFaceConfidence(face, small.Width, small.Height),
            EyeConfidence = AverageConfidence(leftEyeAperture, rightEyeAperture),
            MouthConfidence = mouthAperture.Confidence,
            EyeImageQualityAvailable = HasImageDiagnostics(leftEyeAperture) || HasImageDiagnostics(rightEyeAperture),
            MouthImageQualityAvailable = HasImageDiagnostics(mouthAperture),
            EyeGlarePercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.GlareRatio * 100d),
            MouthGlarePercent = mouthAperture.GlareRatio * 100d,
            EyeContrastPercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.ContrastScore * 100d),
            MouthContrastPercent = mouthAperture.ContrastScore * 100d,
            EyeSharpnessPercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.SharpnessScore * 100d),
            MouthSharpnessPercent = mouthAperture.SharpnessScore * 100d,
            EyeDarkCoveragePercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.DarkCoverageRatio * 100d),
            MouthDarkCoveragePercent = mouthAperture.DarkCoverageRatio * 100d,
            FaceContour = NormalizePoints(CreateOvalContour(face, 24), small.Width, small.Height),
            LeftEyeContour = NormalizePoints(leftEyeAperture.Contour, small.Width, small.Height),
            RightEyeContour = NormalizePoints(rightEyeAperture.Contour, small.Width, small.Height),
            OuterLipContour = NormalizePoints(OpenCvApertureEstimator.FromBox(mouth, 0.48d, mouthAperture.Confidence).Contour, small.Width, small.Height),
            InnerLipContour = NormalizePoints(mouthAperture.Contour, small.Width, small.Height),
            JawContour = NormalizePoints(CreateJawContour(face), small.Width, small.Height)
        };
    }

    public void Reset()
    {
        _lastFace = null;
        _framesSinceFaceLock = 0;
    }

    public void Dispose()
    {
        _yuNetDetector.Dispose();
        _faceCascade?.Dispose();
        _eyeCascade?.Dispose();
        _mouthCascade?.Dispose();
    }

    public static YuNetCueBoxes EstimateCueBoxesFromYuNet(YuNetFaceDetection detection, int width, int height)
    {
        var frameLeftEyePoint = detection.LeftEye.X <= detection.RightEye.X
            ? detection.LeftEye
            : detection.RightEye;
        var frameRightEyePoint = detection.LeftEye.X > detection.RightEye.X
            ? detection.LeftEye
            : detection.RightEye;

        var leftEye = CreateEyeBoxFromYuNetPoint(frameLeftEyePoint, detection.FaceBox, width, height);
        var rightEye = CreateEyeBoxFromYuNetPoint(frameRightEyePoint, detection.FaceBox, width, height);
        var mouth = CreateMouthBoxFromYuNetPoints(detection.LeftMouthCorner, detection.RightMouthCorner, detection.FaceBox, width, height);
        return new YuNetCueBoxes(leftEye, rightEye, mouth);
    }

    private FaceLocatorResult DetectPrimaryFace(Mat gray)
    {
        if (_lastFace is CvRect lastFace)
        {
            var localFace = DetectLocalFace(gray, lastFace, _framesSinceFaceLock);
            if (localFace.Width > 0 && localFace.Height > 0)
            {
                return new FaceLocatorResult(localFace, "local reacquire", null);
            }
        }

        var yuNetFaces = _yuNetDetector.DetectAll(gray);
        var yuNetCandidate = FaceCandidateSelector.SelectBest(
            yuNetFaces.Select(face => new FaceCandidate(face.FaceBox, $"YuNet DNN lock {face.Score:P0}", face, face.Score)),
            _lastFace,
            gray.Width,
            gray.Height);
        if (yuNetCandidate is not null
            && FaceCandidateSelector.IsAcceptableTrackingCandidate(yuNetCandidate, _lastFace, gray.Width, gray.Height, _framesSinceFaceLock))
        {
            return new FaceLocatorResult(
                yuNetCandidate.Face,
                FormatFaceSelectionSource(yuNetCandidate, _lastFace),
                yuNetCandidate.YuNetFace);
        }

        var globalFaces = _faceCascade?.DetectMultiScale(
            gray,
            1.08,
            4,
            HaarDetectionTypes.ScaleImage,
            new OpenCvSharp.Size(Math.Max(40, gray.Width / 12), Math.Max(40, gray.Height / 12))) ?? [];

        var globalCandidate = FaceCandidateSelector.SelectBest(
            globalFaces.Select(face => new FaceCandidate(face, "global Haar lock", null, 0.58d)),
            _lastFace,
            gray.Width,
            gray.Height);
        if (globalCandidate is not null
            && FaceCandidateSelector.IsAcceptableTrackingCandidate(globalCandidate, _lastFace, gray.Width, gray.Height, _framesSinceFaceLock))
        {
            return new FaceLocatorResult(
                globalCandidate.Face,
                FormatFaceSelectionSource(globalCandidate, _lastFace),
                null);
        }

        return new FaceLocatorResult(default, "searching", null);
    }

    private CvRect DetectLocalFace(Mat gray, CvRect lastFace, int framesSinceLock)
    {
        var expansion = 0.55d + Math.Clamp(framesSinceLock, 0, FaceHoldFrameLimit) * 0.13d;
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
                "local Haar reacquire",
                null,
                0.52d));
        var localFace = FaceCandidateSelector.SelectBest(localCandidates, lastFace, gray.Width, gray.Height);
        return localFace is null
            ? default
            : localFace.Face;
    }

    private FaceFeatureDetection TryCreateHeldFaceDetection(Mat gray)
    {
        if (_lastFace is not CvRect lastFace || _framesSinceFaceLock >= FaceHoldFrameLimit)
        {
            return FaceFeatureDetection.None;
        }

        _framesSinceFaceLock++;
        var confidence = Math.Max(0.16d, CalculateFaceConfidence(lastFace, gray.Width, gray.Height) * Math.Pow(0.72d, _framesSinceFaceLock));
        var leftEye = ClampRect(EstimateEyeBoxFromFace(lastFace, leftSide: true), gray.Width, gray.Height);
        var rightEye = ClampRect(EstimateEyeBoxFromFace(lastFace, leftSide: false), gray.Width, gray.Height);
        var mouth = ClampRect(EstimateMouthBoxFromFace(lastFace), gray.Width, gray.Height);
        var leftEyeAperture = OpenCvApertureEstimator.EstimateEye(gray, leftEye);
        var rightEyeAperture = OpenCvApertureEstimator.EstimateEye(gray, rightEye);
        var mouthAperture = OpenCvApertureEstimator.EstimateMouth(gray, mouth);

        if (!leftEyeAperture.HasAperture)
        {
            leftEyeAperture = OpenCvApertureEstimator.FromBox(leftEye, 0.28d, 0.12d);
        }

        if (!rightEyeAperture.HasAperture)
        {
            rightEyeAperture = OpenCvApertureEstimator.FromBox(rightEye, 0.28d, 0.12d);
        }

        if (!mouthAperture.HasAperture)
        {
            mouthAperture = OpenCvApertureEstimator.FromBox(mouth, 0.20d, 0.10d);
        }

        return new FaceFeatureDetection
        {
            HasFace = true,
            Source = $"OpenCV Haar dynamic face tracker with aperture refinement (temporal face hold {_framesSinceFaceLock}/{FaceHoldFrameLimit})",
            FaceBox = ToNormalizedRect(lastFace, gray.Width, gray.Height),
            LeftEyeBox = ToNormalizedRect(leftEye, gray.Width, gray.Height),
            RightEyeBox = ToNormalizedRect(rightEye, gray.Width, gray.Height),
            MouthBox = ToNormalizedRect(mouth, gray.Width, gray.Height),
            TrackingConfidence = confidence,
            EyeConfidence = Math.Min(0.34d, AverageConfidence(leftEyeAperture, rightEyeAperture) * 0.78d),
            MouthConfidence = Math.Min(0.28d, mouthAperture.Confidence * 0.75d),
            EyeImageQualityAvailable = HasImageDiagnostics(leftEyeAperture) || HasImageDiagnostics(rightEyeAperture),
            MouthImageQualityAvailable = HasImageDiagnostics(mouthAperture),
            EyeGlarePercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.GlareRatio * 100d),
            MouthGlarePercent = mouthAperture.GlareRatio * 100d,
            EyeContrastPercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.ContrastScore * 100d),
            MouthContrastPercent = mouthAperture.ContrastScore * 100d,
            EyeSharpnessPercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.SharpnessScore * 100d),
            MouthSharpnessPercent = mouthAperture.SharpnessScore * 100d,
            EyeDarkCoveragePercent = AverageDiagnostic(leftEyeAperture, rightEyeAperture, static estimate => estimate.DarkCoverageRatio * 100d),
            MouthDarkCoveragePercent = mouthAperture.DarkCoverageRatio * 100d,
            FaceContour = NormalizePoints(CreateOvalContour(lastFace, 24), gray.Width, gray.Height),
            LeftEyeContour = NormalizePoints(leftEyeAperture.Contour, gray.Width, gray.Height),
            RightEyeContour = NormalizePoints(rightEyeAperture.Contour, gray.Width, gray.Height),
            OuterLipContour = NormalizePoints(OpenCvApertureEstimator.FromBox(mouth, 0.44d, mouthAperture.Confidence).Contour, gray.Width, gray.Height),
            InnerLipContour = NormalizePoints(mouthAperture.Contour, gray.Width, gray.Height),
            JawContour = NormalizePoints(CreateJawContour(lastFace), gray.Width, gray.Height)
        };
    }

    private void RememberFace(CvRect face)
    {
        _lastFace = face;
        _framesSinceFaceLock = 0;
    }

    private static bool IsPlausibleLocalFace(CvRect localFace, CvRect search)
    {
        if (localFace.Width <= 0 || localFace.Height <= 0)
        {
            return false;
        }

        var aspect = localFace.Width / (double)Math.Max(1, localFace.Height);
        var areaRatio = localFace.Width * localFace.Height / (double)Math.Max(1, search.Width * search.Height);
        return aspect is > 0.62d and < 1.48d
            && areaRatio is > 0.08d and < 0.92d;
    }

    private static string FormatFaceSelectionSource(FaceCandidate candidate, CvRect? previousFace)
    {
        return previousFace is null
            ? candidate.Source
            : $"{candidate.Source}, temporal candidate selection";
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

    private static CvRect EstimateEyeBoxFromFace(CvRect face, bool leftSide)
    {
        var eyeWidth = Math.Max(8, (int)Math.Round(face.Width * 0.28d));
        var eyeHeight = Math.Max(6, (int)Math.Round(face.Height * 0.15d));
        var centerX = face.X + face.Width * (leftSide ? 0.33d : 0.67d);
        var centerY = face.Y + face.Height * 0.38d;
        return new CvRect(
            (int)Math.Round(centerX - eyeWidth / 2d),
            (int)Math.Round(centerY - eyeHeight / 2d),
            eyeWidth,
            eyeHeight);
    }

    private static CvRect EstimateMouthBoxFromFace(CvRect face)
    {
        var mouthWidth = Math.Max(12, (int)Math.Round(face.Width * 0.46d));
        var mouthHeight = Math.Max(8, (int)Math.Round(face.Height * 0.18d));
        var centerX = face.X + face.Width * 0.50d;
        var centerY = face.Y + face.Height * 0.68d;
        return new CvRect(
            (int)Math.Round(centerX - mouthWidth / 2d),
            (int)Math.Round(centerY - mouthHeight / 2d),
            mouthWidth,
            mouthHeight);
    }

    private static CvRect CreateEyeBoxFromYuNetPoint(Point2f point, CvRect face, int width, int height)
    {
        var eyeWidth = Math.Max(8, (int)Math.Round(face.Width * 0.26d));
        var eyeHeight = Math.Max(6, (int)Math.Round(face.Height * 0.16d));
        return ClampRect(
            new CvRect(
                (int)Math.Round(point.X - eyeWidth / 2d),
                (int)Math.Round(point.Y - eyeHeight / 2d),
                eyeWidth,
                eyeHeight),
            width,
            height);
    }

    private static CvRect CreateMouthBoxFromYuNetPoints(Point2f firstCorner, Point2f secondCorner, CvRect face, int width, int height)
    {
        var cornerDistance = Math.Abs(firstCorner.X - secondCorner.X);
        var mouthWidth = Math.Max((int)Math.Round(face.Width * 0.38d), (int)Math.Round(cornerDistance * 1.9d));
        var mouthHeight = Math.Max(8, (int)Math.Round(face.Height * 0.18d));
        mouthHeight = Math.Min(mouthHeight, Math.Max(8, (int)Math.Round(face.Height * 0.28d)));
        var centerX = (firstCorner.X + secondCorner.X) / 2d;
        var centerY = (firstCorner.Y + secondCorner.Y) / 2d + face.Height * 0.04d;

        return ClampRect(
            new CvRect(
                (int)Math.Round(centerX - mouthWidth / 2d),
                (int)Math.Round(centerY - mouthHeight / 2d),
                mouthWidth,
                mouthHeight),
            width,
            height);
    }

    private static ApertureRegionRefinement ChooseBestEyeRefinement(
        Mat gray,
        CvRect face,
        bool leftSide,
        params CvRect?[] seeds)
    {
        ApertureRegionRefinement? best = null;
        foreach (var seed in seeds)
        {
            if (seed is not CvRect box)
            {
                continue;
            }

            var refinement = ApertureRegionRefiner.RefineEye(gray, face, box, leftSide);
            if (IsBetterRefinement(refinement, best))
            {
                best = refinement;
            }
        }

        return best ?? new ApertureRegionRefinement(default, ApertureEstimate.None, 0d);
    }

    private static ApertureRegionRefinement ChooseBestMouthRefinement(
        Mat gray,
        CvRect face,
        params CvRect?[] seeds)
    {
        ApertureRegionRefinement? best = null;
        foreach (var seed in seeds)
        {
            if (seed is not CvRect box)
            {
                continue;
            }

            var refinement = ApertureRegionRefiner.RefineMouth(gray, face, box);
            if (IsBetterRefinement(refinement, best))
            {
                best = refinement;
            }
        }

        return best ?? new ApertureRegionRefinement(default, ApertureEstimate.None, 0d);
    }

    private static bool IsBetterRefinement(ApertureRegionRefinement current, ApertureRegionRefinement? best)
    {
        if (current.Box.Width <= 0 || current.Box.Height <= 0)
        {
            return false;
        }

        if (best is null)
        {
            return true;
        }

        var currentHasAperture = current.Estimate.HasAperture;
        var bestHasAperture = best.Estimate.HasAperture;
        if (currentHasAperture != bestHasAperture)
        {
            return currentHasAperture;
        }

        return current.Score > best.Score + 0.008d;
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

    private static CvRect? DetectEye(Mat gray, CvRect face, CascadeClassifier cascade, bool leftSide)
    {
        var top = face.Y + (int)(face.Height * 0.18d);
        var height = Math.Max(1, (int)(face.Height * 0.34d));
        var x = leftSide ? face.X : face.X + face.Width / 2;
        var width = Math.Max(1, face.Width / 2);
        var roi = ClampRect(new CvRect(x, top, width, height), gray.Width, gray.Height);
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            return null;
        }

        using var view = new Mat(gray, roi);
        var eyes = cascade.DetectMultiScale(
            view,
            1.08,
            3,
            HaarDetectionTypes.ScaleImage,
            new OpenCvSharp.Size(Math.Max(12, roi.Width / 8), Math.Max(8, roi.Height / 8)));

        var eye = eyes
            .Where(rect => rect.Y + rect.Height / 2d < roi.Height * 0.76d)
            .OrderByDescending(rect => rect.Width * rect.Height)
            .FirstOrDefault();
        if (eye.Width <= 0 || eye.Height <= 0)
        {
            return null;
        }

        return new CvRect(roi.X + eye.X, roi.Y + eye.Y, eye.Width, eye.Height);
    }

    private static CvRect? DetectMouth(Mat gray, CvRect face, CascadeClassifier cascade)
    {
        var roi = ClampRect(
            new CvRect(
                face.X + (int)(face.Width * 0.16d),
                face.Y + (int)(face.Height * 0.48d),
                (int)(face.Width * 0.68d),
                (int)(face.Height * 0.42d)),
            gray.Width,
            gray.Height);
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            return null;
        }

        using var view = new Mat(gray, roi);
        var mouths = cascade.DetectMultiScale(
            view,
            1.12,
            8,
            HaarDetectionTypes.ScaleImage,
            new OpenCvSharp.Size(Math.Max(18, roi.Width / 5), Math.Max(10, roi.Height / 8)));

        var mouth = mouths
            .Where(rect => rect.Y + rect.Height / 2d > roi.Height * 0.35d)
            .OrderByDescending(rect => rect.Width * rect.Height)
            .FirstOrDefault();
        if (mouth.Width <= 0 || mouth.Height <= 0)
        {
            return null;
        }

        return new CvRect(roi.X + mouth.X, roi.Y + mouth.Y, mouth.Width, mouth.Height);
    }

    private static CvRect ClampRect(CvRect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, x + 1, width);
        var bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return new CvRect(x, y, right - x, bottom - y);
    }

    private static WpfRect ToNormalizedRect(CvRect rect, int width, int height)
    {
        return new WpfRect(
            rect.X / (double)Math.Max(1, width),
            rect.Y / (double)Math.Max(1, height),
            rect.Width / (double)Math.Max(1, width),
            rect.Height / (double)Math.Max(1, height));
    }

    private static double CalculateFaceConfidence(CvRect face, int width, int height)
    {
        var frameArea = Math.Max(1d, width * height);
        var faceArea = face.Width * face.Height;
        var relativeArea = faceArea / frameArea;
        var sizeScore = Math.Clamp(relativeArea / 0.12d, 0d, 1d);
        return Math.Clamp(0.38d + sizeScore * 0.34d, 0.38d, 0.72d);
    }

    private static double AverageConfidence(ApertureEstimate first, ApertureEstimate second)
    {
        if (first.HasAperture && second.HasAperture)
        {
            return (first.Confidence + second.Confidence) / 2d;
        }

        if (first.HasAperture)
        {
            return first.Confidence * 0.72d;
        }

        if (second.HasAperture)
        {
            return second.Confidence * 0.72d;
        }

        return 0d;
    }

    private static bool HasImageDiagnostics(ApertureEstimate estimate)
    {
        return estimate.GlareRatio > 0d
            || estimate.ContrastScore > 0d
            || estimate.SharpnessScore > 0d
            || estimate.DarkCoverageRatio > 0d;
    }

    private static double AverageDiagnostic(
        ApertureEstimate first,
        ApertureEstimate second,
        Func<ApertureEstimate, double> selector)
    {
        var count = 0;
        var total = 0d;
        if (HasImageDiagnostics(first))
        {
            total += selector(first);
            count++;
        }

        if (HasImageDiagnostics(second))
        {
            total += selector(second);
            count++;
        }

        return count == 0 ? 0d : total / count;
    }

    private static IReadOnlyList<WpfPoint> NormalizePoints(IReadOnlyList<WpfPoint> points, int width, int height)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var normalized = new List<WpfPoint>(points.Count);
        foreach (var point in points)
        {
            normalized.Add(new WpfPoint(
                Math.Clamp(point.X / Math.Max(1d, width), 0d, 1d),
                Math.Clamp(point.Y / Math.Max(1d, height), 0d, 1d)));
        }

        return normalized;
    }

    private static IReadOnlyList<WpfPoint> CreateOvalContour(CvRect box, int count)
    {
        var points = new List<WpfPoint>(count);
        var centerX = box.X + box.Width / 2d;
        var centerY = box.Y + box.Height / 2d;
        for (var index = 0; index < count; index++)
        {
            var angle = Math.PI * 2d * index / count;
            points.Add(new WpfPoint(
                centerX + Math.Cos(angle) * box.Width * 0.50d,
                centerY + Math.Sin(angle) * box.Height * 0.50d));
        }

        return points;
    }

    private static IReadOnlyList<WpfPoint> CreateJawContour(CvRect face)
    {
        return
        [
            new(face.X + face.Width * 0.12d, face.Y + face.Height * 0.62d),
            new(face.X + face.Width * 0.22d, face.Y + face.Height * 0.80d),
            new(face.X + face.Width * 0.50d, face.Bottom),
            new(face.X + face.Width * 0.78d, face.Y + face.Height * 0.80d),
            new(face.X + face.Width * 0.88d, face.Y + face.Height * 0.62d)
        ];
    }

    private sealed record FaceLocatorResult(CvRect Face, string Source, YuNetFaceDetection? YuNetFace);
}

public sealed record YuNetCueBoxes(CvRect LeftEye, CvRect RightEye, CvRect Mouth);
