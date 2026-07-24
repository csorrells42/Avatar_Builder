using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvFaceFeatureTracker : IDisposable
{
	private sealed record FaceLocatorResult(OpenCvSharp.Rect Face, string Source, YuNetFaceDetection? YuNetFace);

	private const int FaceHoldFrameLimit = 8;

	private readonly CascadeClassifier? _faceCascade;

	private readonly CascadeClassifier? _eyeCascade;

	private readonly CascadeClassifier? _mouthCascade;

	private readonly OpenCvYuNetFaceDetector _yuNetDetector = new OpenCvYuNetFaceDetector();

	private OpenCvSharp.Rect? _lastFace;

	private int _framesSinceFaceLock;

	public int MaxDetectionDimension { get; set; } = 960;

	public bool IsAvailable
	{
		get
		{
			if (_faceCascade != null && _eyeCascade != null)
			{
				return _mouthCascade != null;
			}
			return false;
		}
	}

	public OpenCvFaceFeatureTracker()
	{
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = AppContext.BaseDirectory;
		buffer[1] = "dependencies";
		buffer[2] = "vision";
		buffer[3] = "opencv";
		buffer[4] = "haarcascades";
		string path = Path.Combine(buffer);
		_faceCascade = LoadCascade(Path.Combine(path, "haarcascade_frontalface_alt2.xml"));
		_eyeCascade = LoadCascade(Path.Combine(path, "haarcascade_eye_tree_eyeglasses.xml"));
		_mouthCascade = LoadCascade(Path.Combine(path, "haarcascade_smile.xml"));
	}

	public FaceFeatureDetection Detect(BitmapSource bitmap)
	{
		if (!IsAvailable || _faceCascade == null || _eyeCascade == null || _mouthCascade == null)
		{
			return FaceFeatureDetection.None;
		}
		using Mat mat = CreateGrayMat(bitmap);
		using Mat mat2 = new Mat();
		int num = Math.Clamp(MaxDetectionDimension, 320, 1920);
		double num2 = Math.Min(1.0, (double)num / (double)Math.Max(mat.Width, mat.Height));
		if (num2 < 1.0)
		{
			Cv2.Resize(mat, mat2, new OpenCvSharp.Size(Math.Max(1, (int)((double)mat.Width * num2)), Math.Max(1, (int)((double)mat.Height * num2))));
		}
		else
		{
			mat.CopyTo(mat2);
		}
		Cv2.EqualizeHist(mat2, mat2);
		FaceLocatorResult faceLocatorResult = DetectPrimaryFace(mat2);
		OpenCvSharp.Rect face = faceLocatorResult.Face;
		if (face.Width <= 0 || face.Height <= 0)
		{
			FaceFeatureDetection faceFeatureDetection = TryCreateHeldFaceDetection(mat2);
			if (faceFeatureDetection.HasFace)
			{
				return faceFeatureDetection;
			}
			_framesSinceFaceLock++;
			return FaceFeatureDetection.None;
		}
		RememberFace(face);
		YuNetCueBoxes? yuNetCueBoxes = ((faceLocatorResult.YuNetFace is null) ? null : EstimateCueBoxesFromYuNet(faceLocatorResult.YuNetFace, mat2.Width, mat2.Height));
		OpenCvSharp.Rect? rect = DetectEye(mat2, face, _eyeCascade, leftSide: true);
		OpenCvSharp.Rect? rect2 = DetectEye(mat2, face, _eyeCascade, leftSide: false);
		OpenCvSharp.Rect? rect3 = DetectMouth(mat2, face, _mouthCascade);
		OpenCvSharp.Rect value = ClampRect(EstimateEyeBoxFromFace(face, leftSide: true), mat2.Width, mat2.Height);
		OpenCvSharp.Rect value2 = ClampRect(EstimateEyeBoxFromFace(face, leftSide: false), mat2.Width, mat2.Height);
		OpenCvSharp.Rect value3 = ClampRect(EstimateMouthBoxFromFace(face), mat2.Width, mat2.Height);
		ApertureRegionRefinement apertureRegionRefinement = ChooseBestEyeRefinement(mat2, face, true, rect, yuNetCueBoxes?.LeftEye, value);
		ApertureRegionRefinement apertureRegionRefinement2 = ChooseBestEyeRefinement(mat2, face, false, rect2, yuNetCueBoxes?.RightEye, value2);
		ApertureRegionRefinement apertureRegionRefinement3 = ChooseBestMouthRefinement(mat2, face, rect3, yuNetCueBoxes?.Mouth, value3);
		OpenCvSharp.Rect box = apertureRegionRefinement.Box;
		OpenCvSharp.Rect box2 = apertureRegionRefinement2.Box;
		OpenCvSharp.Rect box3 = apertureRegionRefinement3.Box;
		ApertureEstimate apertureEstimate = apertureRegionRefinement.Estimate;
		ApertureEstimate apertureEstimate2 = apertureRegionRefinement2.Estimate;
		ApertureEstimate apertureEstimate3 = apertureRegionRefinement3.Estimate;
		if (!apertureEstimate.HasAperture)
		{
			apertureEstimate = OpenCvApertureEstimator.FromBox(box, 0.34, 0.2);
		}
		if (!apertureEstimate2.HasAperture)
		{
			apertureEstimate2 = OpenCvApertureEstimator.FromBox(box2, 0.34, 0.2);
		}
		if (!apertureEstimate3.HasAperture)
		{
			apertureEstimate3 = OpenCvApertureEstimator.FromBox(box3, 0.26, 0.18);
		}
		return new FaceFeatureDetection
		{
			HasFace = true,
			Source = "OpenCV Haar dynamic face tracker with aperture refinement (" + faceLocatorResult.Source + ((yuNetCueBoxes is null) ? "" : ", YuNet cue boxes") + ")",
			FaceBox = ToNormalizedRect(face, mat2.Width, mat2.Height),
			LeftEyeBox = ToNormalizedRect(box, mat2.Width, mat2.Height),
			RightEyeBox = ToNormalizedRect(box2, mat2.Width, mat2.Height),
			MouthBox = ToNormalizedRect(box3, mat2.Width, mat2.Height),
			TrackingConfidence = CalculateFaceConfidence(face, mat2.Width, mat2.Height),
			EyeConfidence = AverageConfidence(apertureEstimate, apertureEstimate2),
			MouthConfidence = apertureEstimate3.Confidence,
			EyeImageQualityAvailable = (HasImageDiagnostics(apertureEstimate) || HasImageDiagnostics(apertureEstimate2)),
			MouthImageQualityAvailable = HasImageDiagnostics(apertureEstimate3),
			EyeGlarePercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.GlareRatio * 100.0),
			MouthGlarePercent = apertureEstimate3.GlareRatio * 100.0,
			EyeContrastPercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.ContrastScore * 100.0),
			MouthContrastPercent = apertureEstimate3.ContrastScore * 100.0,
			EyeSharpnessPercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.SharpnessScore * 100.0),
			MouthSharpnessPercent = apertureEstimate3.SharpnessScore * 100.0,
			EyeDarkCoveragePercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.DarkCoverageRatio * 100.0),
			MouthDarkCoveragePercent = apertureEstimate3.DarkCoverageRatio * 100.0,
			FaceContour = NormalizePoints(CreateOvalContour(face, 24), mat2.Width, mat2.Height),
			LeftEyeContour = NormalizePoints(apertureEstimate.Contour, mat2.Width, mat2.Height),
			RightEyeContour = NormalizePoints(apertureEstimate2.Contour, mat2.Width, mat2.Height),
			OuterLipContour = NormalizePoints(OpenCvApertureEstimator.FromBox(box3, 0.48, apertureEstimate3.Confidence).Contour, mat2.Width, mat2.Height),
			InnerLipContour = NormalizePoints(apertureEstimate3.Contour, mat2.Width, mat2.Height),
			JawContour = NormalizePoints(CreateJawContour(face), mat2.Width, mat2.Height)
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
		Point2f point = ((detection.LeftEye.X <= detection.RightEye.X) ? detection.LeftEye : detection.RightEye);
		Point2f point2 = ((detection.LeftEye.X > detection.RightEye.X) ? detection.LeftEye : detection.RightEye);
		OpenCvSharp.Rect leftEye = CreateEyeBoxFromYuNetPoint(point, detection.FaceBox, width, height);
		OpenCvSharp.Rect rightEye = CreateEyeBoxFromYuNetPoint(point2, detection.FaceBox, width, height);
		OpenCvSharp.Rect mouth = CreateMouthBoxFromYuNetPoints(detection.LeftMouthCorner, detection.RightMouthCorner, detection.FaceBox, width, height);
		return new YuNetCueBoxes(leftEye, rightEye, mouth);
	}

	private FaceLocatorResult DetectPrimaryFace(Mat gray)
	{
		OpenCvSharp.Rect? lastFace = _lastFace;
		if (lastFace.HasValue)
		{
			OpenCvSharp.Rect valueOrDefault = lastFace.GetValueOrDefault();
			OpenCvSharp.Rect face = DetectLocalFace(gray, valueOrDefault, _framesSinceFaceLock);
			if (face.Width > 0 && face.Height > 0)
			{
				return new FaceLocatorResult(face, "local reacquire", null);
			}
		}
		FaceCandidate? faceCandidate = FaceCandidateSelector.SelectBest(from yuNetFaceDetection in _yuNetDetector.DetectAll(gray)
			select new FaceCandidate(yuNetFaceDetection.FaceBox, $"YuNet DNN lock {yuNetFaceDetection.Score:P0}", yuNetFaceDetection, yuNetFaceDetection.Score), _lastFace, gray.Width, gray.Height);
		if (faceCandidate is not null && FaceCandidateSelector.IsAcceptableTrackingCandidate(faceCandidate, _lastFace, gray.Width, gray.Height, _framesSinceFaceLock))
		{
			return new FaceLocatorResult(faceCandidate.Face, FormatFaceSelectionSource(faceCandidate, _lastFace), faceCandidate.YuNetFace);
		}
		FaceCandidate? faceCandidate2 = FaceCandidateSelector.SelectBest((_faceCascade?.DetectMultiScale(gray, 1.08, 4, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(Math.Max(40, gray.Width / 12), Math.Max(40, gray.Height / 12))) ?? Array.Empty<OpenCvSharp.Rect>()).Select((OpenCvSharp.Rect face2) => new FaceCandidate(face2, "global Haar lock", null, 0.58)), _lastFace, gray.Width, gray.Height);
		if (faceCandidate2 is not null && FaceCandidateSelector.IsAcceptableTrackingCandidate(faceCandidate2, _lastFace, gray.Width, gray.Height, _framesSinceFaceLock))
		{
			return new FaceLocatorResult(faceCandidate2.Face, FormatFaceSelectionSource(faceCandidate2, _lastFace), null);
		}
		return new FaceLocatorResult(default(OpenCvSharp.Rect), "searching", null);
	}

	private OpenCvSharp.Rect DetectLocalFace(Mat gray, OpenCvSharp.Rect lastFace, int framesSinceLock)
	{
		double fraction = 0.55 + (double)Math.Clamp(framesSinceLock, 0, 8) * 0.13;
		OpenCvSharp.Rect search = ExpandRect(lastFace, fraction, gray.Width, gray.Height);
		if (search.Width < Math.Max(40, gray.Width / 14) || search.Height < Math.Max(40, gray.Height / 14))
		{
			return default(OpenCvSharp.Rect);
		}
		using Mat image = new Mat(gray, search);
		return FaceCandidateSelector.SelectBest(from rect in _faceCascade?.DetectMultiScale(image, 1.05, 2, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(Math.Max(28, search.Width / 8), Math.Max(28, search.Height / 8))) ?? Array.Empty<OpenCvSharp.Rect>()
			where IsPlausibleLocalFace(rect, search)
			select new FaceCandidate(new OpenCvSharp.Rect(search.X + rect.X, search.Y + rect.Y, rect.Width, rect.Height), "local Haar reacquire", null, 0.52), lastFace, gray.Width, gray.Height)?.Face ?? default(OpenCvSharp.Rect);
	}

	private FaceFeatureDetection TryCreateHeldFaceDetection(Mat gray)
	{
		OpenCvSharp.Rect? lastFace = _lastFace;
		if (lastFace.HasValue)
		{
			OpenCvSharp.Rect valueOrDefault = lastFace.GetValueOrDefault();
			if (_framesSinceFaceLock < 8)
			{
				_framesSinceFaceLock++;
				double trackingConfidence = Math.Max(0.16, CalculateFaceConfidence(valueOrDefault, gray.Width, gray.Height) * Math.Pow(0.72, _framesSinceFaceLock));
				OpenCvSharp.Rect rect = ClampRect(EstimateEyeBoxFromFace(valueOrDefault, leftSide: true), gray.Width, gray.Height);
				OpenCvSharp.Rect rect2 = ClampRect(EstimateEyeBoxFromFace(valueOrDefault, leftSide: false), gray.Width, gray.Height);
				OpenCvSharp.Rect rect3 = ClampRect(EstimateMouthBoxFromFace(valueOrDefault), gray.Width, gray.Height);
				ApertureEstimate apertureEstimate = OpenCvApertureEstimator.EstimateEye(gray, rect);
				ApertureEstimate apertureEstimate2 = OpenCvApertureEstimator.EstimateEye(gray, rect2);
				ApertureEstimate apertureEstimate3 = OpenCvApertureEstimator.EstimateMouth(gray, rect3);
				if (!apertureEstimate.HasAperture)
				{
					apertureEstimate = OpenCvApertureEstimator.FromBox(rect, 0.28, 0.12);
				}
				if (!apertureEstimate2.HasAperture)
				{
					apertureEstimate2 = OpenCvApertureEstimator.FromBox(rect2, 0.28, 0.12);
				}
				if (!apertureEstimate3.HasAperture)
				{
					apertureEstimate3 = OpenCvApertureEstimator.FromBox(rect3, 0.2, 0.1);
				}
				return new FaceFeatureDetection
				{
					HasFace = true,
					Source = $"OpenCV Haar dynamic face tracker with aperture refinement (temporal face hold {_framesSinceFaceLock}/{8})",
					FaceBox = ToNormalizedRect(valueOrDefault, gray.Width, gray.Height),
					LeftEyeBox = ToNormalizedRect(rect, gray.Width, gray.Height),
					RightEyeBox = ToNormalizedRect(rect2, gray.Width, gray.Height),
					MouthBox = ToNormalizedRect(rect3, gray.Width, gray.Height),
					TrackingConfidence = trackingConfidence,
					EyeConfidence = Math.Min(0.34, AverageConfidence(apertureEstimate, apertureEstimate2) * 0.78),
					MouthConfidence = Math.Min(0.28, apertureEstimate3.Confidence * 0.75),
					EyeImageQualityAvailable = (HasImageDiagnostics(apertureEstimate) || HasImageDiagnostics(apertureEstimate2)),
					MouthImageQualityAvailable = HasImageDiagnostics(apertureEstimate3),
					EyeGlarePercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.GlareRatio * 100.0),
					MouthGlarePercent = apertureEstimate3.GlareRatio * 100.0,
					EyeContrastPercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.ContrastScore * 100.0),
					MouthContrastPercent = apertureEstimate3.ContrastScore * 100.0,
					EyeSharpnessPercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.SharpnessScore * 100.0),
					MouthSharpnessPercent = apertureEstimate3.SharpnessScore * 100.0,
					EyeDarkCoveragePercent = AverageDiagnostic(apertureEstimate, apertureEstimate2, (ApertureEstimate estimate) => estimate.DarkCoverageRatio * 100.0),
					MouthDarkCoveragePercent = apertureEstimate3.DarkCoverageRatio * 100.0,
					FaceContour = NormalizePoints(CreateOvalContour(valueOrDefault, 24), gray.Width, gray.Height),
					LeftEyeContour = NormalizePoints(apertureEstimate.Contour, gray.Width, gray.Height),
					RightEyeContour = NormalizePoints(apertureEstimate2.Contour, gray.Width, gray.Height),
					OuterLipContour = NormalizePoints(OpenCvApertureEstimator.FromBox(rect3, 0.44, apertureEstimate3.Confidence).Contour, gray.Width, gray.Height),
					InnerLipContour = NormalizePoints(apertureEstimate3.Contour, gray.Width, gray.Height),
					JawContour = NormalizePoints(CreateJawContour(valueOrDefault), gray.Width, gray.Height)
				};
			}
		}
		return FaceFeatureDetection.None;
	}

	private void RememberFace(OpenCvSharp.Rect face)
	{
		_lastFace = face;
		_framesSinceFaceLock = 0;
	}

	private static bool IsPlausibleLocalFace(OpenCvSharp.Rect localFace, OpenCvSharp.Rect search)
	{
		if (localFace.Width <= 0 || localFace.Height <= 0)
		{
			return false;
		}
		double num = (double)localFace.Width / (double)Math.Max(1, localFace.Height);
		double num2 = (double)(localFace.Width * localFace.Height) / (double)Math.Max(1, search.Width * search.Height);
		if (num > 0.62 && num < 1.48)
		{
			if (num2 > 0.08)
			{
				return num2 < 0.92;
			}
			return false;
		}
		return false;
	}

	private static string FormatFaceSelectionSource(FaceCandidate candidate, OpenCvSharp.Rect? previousFace)
	{
		if (previousFace.HasValue)
		{
			return candidate.Source + ", temporal candidate selection";
		}
		return candidate.Source;
	}

	private static OpenCvSharp.Rect ExpandRect(OpenCvSharp.Rect rect, double fraction, int width, int height)
	{
		int num = (int)Math.Round((double)rect.Width * fraction);
		int num2 = (int)Math.Round((double)rect.Height * fraction);
		return ClampRect(new OpenCvSharp.Rect(rect.X - num, rect.Y - num2, rect.Width + num * 2, rect.Height + num2 * 2), width, height);
	}

	private static OpenCvSharp.Rect EstimateEyeBoxFromFace(OpenCvSharp.Rect face, bool leftSide)
	{
		int num = Math.Max(8, (int)Math.Round((double)face.Width * 0.28));
		int num2 = Math.Max(6, (int)Math.Round((double)face.Height * 0.15));
		double num3 = (double)face.X + (double)face.Width * (leftSide ? 0.33 : 0.67);
		return new OpenCvSharp.Rect(Y: (int)Math.Round((double)face.Y + (double)face.Height * 0.38 - (double)num2 / 2.0), X: (int)Math.Round(num3 - (double)num / 2.0), Width: num, Height: num2);
	}

	private static OpenCvSharp.Rect EstimateMouthBoxFromFace(OpenCvSharp.Rect face)
	{
		int num = Math.Max(12, (int)Math.Round((double)face.Width * 0.46));
		int num2 = Math.Max(8, (int)Math.Round((double)face.Height * 0.18));
		double num3 = (double)face.X + (double)face.Width * 0.5;
		return new OpenCvSharp.Rect(Y: (int)Math.Round((double)face.Y + (double)face.Height * 0.68 - (double)num2 / 2.0), X: (int)Math.Round(num3 - (double)num / 2.0), Width: num, Height: num2);
	}

	private static OpenCvSharp.Rect CreateEyeBoxFromYuNetPoint(Point2f point, OpenCvSharp.Rect face, int width, int height)
	{
		int num = Math.Max(8, (int)Math.Round((double)face.Width * 0.26));
		int num2 = Math.Max(6, (int)Math.Round((double)face.Height * 0.16));
		return ClampRect(new OpenCvSharp.Rect((int)Math.Round((double)point.X - (double)num / 2.0), (int)Math.Round((double)point.Y - (double)num2 / 2.0), num, num2), width, height);
	}

	private static OpenCvSharp.Rect CreateMouthBoxFromYuNetPoints(Point2f firstCorner, Point2f secondCorner, OpenCvSharp.Rect face, int width, int height)
	{
		float num = Math.Abs(firstCorner.X - secondCorner.X);
		int num2 = Math.Max((int)Math.Round((double)face.Width * 0.38), (int)Math.Round((double)num * 1.9));
		int val = Math.Max(8, (int)Math.Round((double)face.Height * 0.18));
		val = Math.Min(val, Math.Max(8, (int)Math.Round((double)face.Height * 0.28)));
		double num3 = (double)(firstCorner.X + secondCorner.X) / 2.0;
		return ClampRect(new OpenCvSharp.Rect(Y: (int)Math.Round((double)(firstCorner.Y + secondCorner.Y) / 2.0 + (double)face.Height * 0.04 - (double)val / 2.0), X: (int)Math.Round(num3 - (double)num2 / 2.0), Width: num2, Height: val), width, height);
	}

	private static ApertureRegionRefinement ChooseBestEyeRefinement(Mat gray, OpenCvSharp.Rect face, bool leftSide, params OpenCvSharp.Rect?[] seeds)
	{
			ApertureRegionRefinement? apertureRegionRefinement = null;
		for (int i = 0; i < seeds.Length; i++)
		{
			OpenCvSharp.Rect? rect = seeds[i];
			if (rect.HasValue)
			{
				OpenCvSharp.Rect valueOrDefault = rect.GetValueOrDefault();
				ApertureRegionRefinement apertureRegionRefinement2 = ApertureRegionRefiner.RefineEye(gray, face, valueOrDefault, leftSide);
				if (IsBetterRefinement(apertureRegionRefinement2, apertureRegionRefinement))
				{
					apertureRegionRefinement = apertureRegionRefinement2;
				}
			}
		}
		return apertureRegionRefinement ?? new ApertureRegionRefinement(default(OpenCvSharp.Rect), ApertureEstimate.None, 0.0);
	}

	private static ApertureRegionRefinement ChooseBestMouthRefinement(Mat gray, OpenCvSharp.Rect face, params OpenCvSharp.Rect?[] seeds)
	{
			ApertureRegionRefinement? apertureRegionRefinement = null;
		for (int i = 0; i < seeds.Length; i++)
		{
			OpenCvSharp.Rect? rect = seeds[i];
			if (rect.HasValue)
			{
				OpenCvSharp.Rect valueOrDefault = rect.GetValueOrDefault();
				ApertureRegionRefinement apertureRegionRefinement2 = ApertureRegionRefiner.RefineMouth(gray, face, valueOrDefault);
				if (IsBetterRefinement(apertureRegionRefinement2, apertureRegionRefinement))
				{
					apertureRegionRefinement = apertureRegionRefinement2;
				}
			}
		}
		return apertureRegionRefinement ?? new ApertureRegionRefinement(default(OpenCvSharp.Rect), ApertureEstimate.None, 0.0);
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
		bool hasAperture = current.Estimate.HasAperture;
		bool hasAperture2 = best.Estimate.HasAperture;
		if (hasAperture != hasAperture2)
		{
			return hasAperture;
		}
		return current.Score > best.Score + 0.008;
	}

	private static CascadeClassifier? LoadCascade(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		CascadeClassifier cascadeClassifier = new CascadeClassifier(path);
		if (!cascadeClassifier.Empty())
		{
			return cascadeClassifier;
		}
		return null;
	}

	private static Mat CreateGrayMat(BitmapSource bitmap)
	{
		FormatConvertedBitmap formatConvertedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
		int pixelWidth = formatConvertedBitmap.PixelWidth;
		int pixelHeight = formatConvertedBitmap.PixelHeight;
		int num = pixelWidth * 4;
		byte[] array = new byte[num * pixelHeight];
		formatConvertedBitmap.CopyPixels(array, num, 0);
		using Mat mat = Mat.FromPixelData(pixelHeight, pixelWidth, MatType.CV_8UC4, array, 0L);
		Mat mat2 = new Mat();
		Cv2.CvtColor(mat, mat2, ColorConversionCodes.BGRA2GRAY);
		return mat2;
	}

	private static OpenCvSharp.Rect? DetectEye(Mat gray, OpenCvSharp.Rect face, CascadeClassifier cascade, bool leftSide)
	{
		int y = face.Y + (int)((double)face.Height * 0.18);
		int height = Math.Max(1, (int)((double)face.Height * 0.34));
		int x = (leftSide ? face.X : (face.X + face.Width / 2));
		int width = Math.Max(1, face.Width / 2);
		OpenCvSharp.Rect roi = ClampRect(new OpenCvSharp.Rect(x, y, width, height), gray.Width, gray.Height);
		if (roi.Width <= 0 || roi.Height <= 0)
		{
			return null;
		}
		using Mat image = new Mat(gray, roi);
		OpenCvSharp.Rect rect = (from rect2 in cascade.DetectMultiScale(image, 1.08, 3, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(Math.Max(12, roi.Width / 8), Math.Max(8, roi.Height / 8)))
			where (double)rect2.Y + (double)rect2.Height / 2.0 < (double)roi.Height * 0.76
			orderby rect2.Width * rect2.Height descending
			select rect2).FirstOrDefault();
		if (rect.Width <= 0 || rect.Height <= 0)
		{
			return null;
		}
		return new OpenCvSharp.Rect(roi.X + rect.X, roi.Y + rect.Y, rect.Width, rect.Height);
	}

	private static OpenCvSharp.Rect? DetectMouth(Mat gray, OpenCvSharp.Rect face, CascadeClassifier cascade)
	{
		OpenCvSharp.Rect roi = ClampRect(new OpenCvSharp.Rect(face.X + (int)((double)face.Width * 0.16), face.Y + (int)((double)face.Height * 0.48), (int)((double)face.Width * 0.68), (int)((double)face.Height * 0.42)), gray.Width, gray.Height);
		if (roi.Width <= 0 || roi.Height <= 0)
		{
			return null;
		}
		using Mat image = new Mat(gray, roi);
		OpenCvSharp.Rect rect = (from rect2 in cascade.DetectMultiScale(image, 1.12, 8, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(Math.Max(18, roi.Width / 5), Math.Max(10, roi.Height / 8)))
			where (double)rect2.Y + (double)rect2.Height / 2.0 > (double)roi.Height * 0.35
			orderby rect2.Width * rect2.Height descending
			select rect2).FirstOrDefault();
		if (rect.Width <= 0 || rect.Height <= 0)
		{
			return null;
		}
		return new OpenCvSharp.Rect(roi.X + rect.X, roi.Y + rect.Y, rect.Width, rect.Height);
	}

	private static OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect rect, int width, int height)
	{
		int num = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
		int num2 = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
		int num3 = Math.Clamp(rect.Right, num + 1, width);
		int num4 = Math.Clamp(rect.Bottom, num2 + 1, height);
		return new OpenCvSharp.Rect(num, num2, num3 - num, num4 - num2);
	}

	private static System.Windows.Rect ToNormalizedRect(OpenCvSharp.Rect rect, int width, int height)
	{
		return new System.Windows.Rect((double)rect.X / (double)Math.Max(1, width), (double)rect.Y / (double)Math.Max(1, height), (double)rect.Width / (double)Math.Max(1, width), (double)rect.Height / (double)Math.Max(1, height));
	}

	private static double CalculateFaceConfidence(OpenCvSharp.Rect face, int width, int height)
	{
		double num = Math.Max(1.0, width * height);
		double num2 = Math.Clamp((double)(face.Width * face.Height) / num / 0.12, 0.0, 1.0);
		return Math.Clamp(0.38 + num2 * 0.34, 0.38, 0.72);
	}

	private static double AverageConfidence(ApertureEstimate first, ApertureEstimate second)
	{
		if (first.HasAperture && second.HasAperture)
		{
			return (first.Confidence + second.Confidence) / 2.0;
		}
		if (first.HasAperture)
		{
			return first.Confidence * 0.72;
		}
		if (second.HasAperture)
		{
			return second.Confidence * 0.72;
		}
		return 0.0;
	}

	private static bool HasImageDiagnostics(ApertureEstimate estimate)
	{
		if (!(estimate.GlareRatio > 0.0) && !(estimate.ContrastScore > 0.0) && !(estimate.SharpnessScore > 0.0))
		{
			return estimate.DarkCoverageRatio > 0.0;
		}
		return true;
	}

	private static double AverageDiagnostic(ApertureEstimate first, ApertureEstimate second, Func<ApertureEstimate, double> selector)
	{
		int num = 0;
		double num2 = 0.0;
		if (HasImageDiagnostics(first))
		{
			num2 += selector(first);
			num++;
		}
		if (HasImageDiagnostics(second))
		{
			num2 += selector(second);
			num++;
		}
		if (num != 0)
		{
			return num2 / (double)num;
		}
		return 0.0;
	}

	private static IReadOnlyList<System.Windows.Point> NormalizePoints(IReadOnlyList<System.Windows.Point> points, int width, int height)
	{
		if (points.Count == 0)
		{
			return Array.Empty<System.Windows.Point>();
		}
		List<System.Windows.Point> list = new List<System.Windows.Point>(points.Count);
		foreach (System.Windows.Point point in points)
		{
			list.Add(new System.Windows.Point(Math.Clamp(point.X / Math.Max(1.0, width), 0.0, 1.0), Math.Clamp(point.Y / Math.Max(1.0, height), 0.0, 1.0)));
		}
		return list;
	}

	private static IReadOnlyList<System.Windows.Point> CreateOvalContour(OpenCvSharp.Rect box, int count)
	{
		List<System.Windows.Point> list = new List<System.Windows.Point>(count);
		double num = (double)box.X + (double)box.Width / 2.0;
		double num2 = (double)box.Y + (double)box.Height / 2.0;
		for (int i = 0; i < count; i++)
		{
			double num3 = Math.PI * 2.0 * (double)i / (double)count;
			list.Add(new System.Windows.Point(num + Math.Cos(num3) * (double)box.Width * 0.5, num2 + Math.Sin(num3) * (double)box.Height * 0.5));
		}
		return list;
	}

	private static IReadOnlyList<System.Windows.Point> CreateJawContour(OpenCvSharp.Rect face)
	{
		return
		[
			new System.Windows.Point((double)face.X + (double)face.Width * 0.12, (double)face.Y + (double)face.Height * 0.62),
			new System.Windows.Point((double)face.X + (double)face.Width * 0.22, (double)face.Y + (double)face.Height * 0.8),
			new System.Windows.Point((double)face.X + (double)face.Width * 0.5, face.Bottom),
			new System.Windows.Point((double)face.X + (double)face.Width * 0.78, (double)face.Y + (double)face.Height * 0.8),
			new System.Windows.Point((double)face.X + (double)face.Width * 0.88, (double)face.Y + (double)face.Height * 0.62)
		];
	}
}
