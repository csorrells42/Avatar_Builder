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
using OpenCvSharp.Face;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvFacemarkLandmarkTracker : IStatefulFaceLandmarkTracker, IFaceLandmarkTracker, IDisposable
{
	private const int LandmarkHoldFrameLimit = 6;

	private readonly OpenCvFacemarkModelInfo _modelInfo = OpenCvFacemarkModelInfo.Load();

	private readonly CascadeClassifier? _faceCascade;

	private readonly OpenCvYuNetFaceDetector _yuNetDetector = new OpenCvYuNetFaceDetector();

	private FacemarkLBF? _facemark;

	private string _initializationStatus = "";

	private FaceLandmarkFrame? _lastLandmarkFrame;

	private FaceFeatureDetection? _lastFeatureDetection;

	private int _framesSinceLandmarkLock;

	public string Name => "OpenCV LBF facemark backend";

	public bool IsAvailable
	{
		get
		{
			if (_modelInfo.IsReady && _faceCascade != null)
			{
				return EnsureFacemark();
			}
			return false;
		}
	}

	public string Status
	{
		get
		{
			if (!_modelInfo.IsReady)
			{
				return _modelInfo.Status;
			}
			if (_faceCascade == null)
			{
				return "OpenCV face cascade missing";
			}
			if (!string.IsNullOrWhiteSpace(_initializationStatus))
			{
				return _initializationStatus;
			}
			return "OpenCV LBF facemark waiting";
		}
	}

	public int MaxDetectionDimension { get; set; } = 1280;

	public OpenCvFacemarkLandmarkTracker()
	{
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = AppContext.BaseDirectory;
		buffer[1] = "dependencies";
		buffer[2] = "vision";
		buffer[3] = "opencv";
		buffer[4] = "haarcascades";
		string path = Path.Combine(buffer);
		_faceCascade = LoadCascade(Path.Combine(path, "haarcascade_frontalface_alt2.xml"));
	}

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
		if (_faceCascade == null)
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
				BackendStatus = (string.IsNullOrWhiteSpace(_initializationStatus) ? "OpenCV LBF facemark unavailable" : _initializationStatus)
			};
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
		OpenCvSharp.Rect rect = DetectPrimaryFace(mat2);
		if (rect.Width <= 0 || rect.Height <= 0)
		{
			return TryCreateHeldResult(capturedAtUtc, "LBF model ready; searching for face") ?? new FaceLandmarkTrackingResult
			{
				BackendName = Name,
				BackendStatus = "LBF model ready; searching for face"
			};
		}
		Point2f[][] landmarks;
		Facemark facemark = _facemark ?? throw new InvalidOperationException("OpenCV facemark initialization reported success without a model instance.");
		using (InputArray image = InputArray.Create(mat2))
		{
			using InputArray faces = InputArray.Create(new OpenCvSharp.Rect[1] { rect });
			if (!facemark.Fit(image, faces, out landmarks) || landmarks.Length == 0 || landmarks[0].Length < 68)
			{
				string text = $"LBF face lock but landmark fit failed ({landmarks.Length} face result{((landmarks.Length == 1) ? "" : "s")})";
				return TryCreateHeldResult(capturedAtUtc, text) ?? new FaceLandmarkTrackingResult
				{
					BackendName = Name,
					BackendStatus = text
				};
			}
		}
		FaceLandmarkFrame landmarkFrame = CreateLandmarkFrame(rect, landmarks[0], mat2.Width, mat2.Height, capturedAtUtc);
		FaceFeatureDetection featureDetection = CreateFeatureDetection(rect, landmarkFrame, mat2.Width, mat2.Height);
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

	public static FaceLandmarkFrame CreateLandmarkFrameFrom68Points(IReadOnlyList<System.Windows.Point> normalizedPoints, DateTime capturedAtUtc, string source = "OpenCV LBF 68-point facemark")
	{
		if (normalizedPoints.Count < 68)
		{
			return FaceLandmarkFrame.None;
		}
		IReadOnlyList<System.Windows.Point> readOnlyList = Slice(normalizedPoints, 36, 6);
		IReadOnlyList<System.Windows.Point> readOnlyList2 = Slice(normalizedPoints, 42, 6);
		IReadOnlyList<System.Windows.Point> first = Slice(normalizedPoints, 17, 5);
		IReadOnlyList<System.Windows.Point> second = Slice(normalizedPoints, 22, 5);
		(IReadOnlyList<System.Windows.Point> Left, IReadOnlyList<System.Windows.Point> Right) tuple = SortByFramePosition(first, second);
		IReadOnlyList<System.Windows.Point> item = tuple.Left;
		IReadOnlyList<System.Windows.Point> item2 = tuple.Right;
		IReadOnlyList<System.Windows.Point> outerLipContour = Slice(normalizedPoints, 48, 12);
		IReadOnlyList<System.Windows.Point> innerLipContour = Slice(normalizedPoints, 60, 8);
		IReadOnlyList<System.Windows.Point> jawContour = Slice(normalizedPoints, 0, 17);
		return new FaceLandmarkFrame
		{
			HasFace = true,
			Source = source,
			CapturedAtUtc = capturedAtUtc,
			TrackingConfidence = 0.82,
			EyeConfidence = 0.78,
			MouthConfidence = 0.78,
			HeadYawDegrees = EstimateYawDegrees(normalizedPoints),
			HeadPitchDegrees = EstimatePitchDegrees(normalizedPoints),
			HeadRollDegrees = EstimateRollDegrees(readOnlyList, readOnlyList2),
			FaceContour = CreateFaceContour(normalizedPoints),
			LeftEyeContour = readOnlyList,
			RightEyeContour = readOnlyList2,
			LeftBrowContour = item,
			RightBrowContour = item2,
			OuterLipContour = outerLipContour,
			InnerLipContour = innerLipContour,
			JawContour = jawContour
		};
	}

	private bool EnsureFacemark()
	{
		if (_facemark != null)
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
			using FacemarkLBF.Params parameters = new FacemarkLBF.Params
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
			_initializationStatus = "OpenCV LBF facemark failed to load: " + ex.Message;
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
		if (_lastLandmarkFrame == null || _lastFeatureDetection == null || _framesSinceLandmarkLock >= 6)
		{
			return null;
		}
		_framesSinceLandmarkLock++;
		double decay = Math.Pow(0.78, _framesSinceLandmarkLock);
		return new FaceLandmarkTrackingResult
		{
			BackendName = Name,
			BackendStatus = $"{missStatus}; LBF temporal landmark hold {_framesSinceLandmarkLock}/{6}",
			FeatureDetection = CreateHeldFeatureDetection(_lastFeatureDetection, decay),
			LandmarkFrame = CreateHeldLandmarkFrame(_lastLandmarkFrame, capturedAtUtc, _framesSinceLandmarkLock, decay)
		};
	}

	private static FaceLandmarkFrame CreateHeldLandmarkFrame(FaceLandmarkFrame source, DateTime capturedAtUtc, int framesSinceLock, double decay)
	{
		return new FaceLandmarkFrame
		{
			HasFace = true,
			Source = $"{source.Source}; LBF temporal hold {framesSinceLock}/{6}",
			CapturedAtUtc = capturedAtUtc,
			TrackingConfidence = Math.Max(0.24, source.TrackingConfidence * decay),
			EyeConfidence = Math.Max(0.22, source.EyeConfidence * decay),
			MouthConfidence = Math.Max(0.2, source.MouthConfidence * decay),
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
			Source = source.Source + "; temporal hold",
			FaceBox = source.FaceBox,
			LeftEyeBox = source.LeftEyeBox,
			RightEyeBox = source.RightEyeBox,
			MouthBox = source.MouthBox,
			TrackingConfidence = Math.Max(0.24, source.TrackingConfidence * decay),
			EyeConfidence = Math.Max(0.22, source.EyeConfidence * decay),
			MouthConfidence = Math.Max(0.2, source.MouthConfidence * decay),
			FaceContour = source.FaceContour,
			LeftEyeContour = source.LeftEyeContour,
			RightEyeContour = source.RightEyeContour,
			OuterLipContour = source.OuterLipContour,
			InnerLipContour = source.InnerLipContour,
			JawContour = source.JawContour
		};
	}

	private OpenCvSharp.Rect DetectPrimaryFace(Mat gray)
	{
		OpenCvSharp.Rect? previousFace = GetPreviousFace(gray.Width, gray.Height);
		if (previousFace.HasValue)
		{
			OpenCvSharp.Rect valueOrDefault = previousFace.GetValueOrDefault();
			OpenCvSharp.Rect result = DetectLocalFace(gray, valueOrDefault, _framesSinceLandmarkLock);
			if (result.Width > 0 && result.Height > 0)
			{
				return result;
			}
		}
		FaceCandidate? faceCandidate = FaceCandidateSelector.SelectBest(from face in _yuNetDetector.DetectAll(gray)
			select new FaceCandidate(face.FaceBox, $"YuNet DNN lock {face.Score:P0}", face, face.Score), previousFace, gray.Width, gray.Height);
		if (faceCandidate is not null && FaceCandidateSelector.IsAcceptableTrackingCandidate(faceCandidate, previousFace, gray.Width, gray.Height, _framesSinceLandmarkLock))
		{
			return faceCandidate.Face;
		}
		FaceCandidate? faceCandidate2 = FaceCandidateSelector.SelectBest((_faceCascade?.DetectMultiScale(gray, 1.08, 4, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(Math.Max(40, gray.Width / 12), Math.Max(40, gray.Height / 12))) ?? Array.Empty<OpenCvSharp.Rect>()).Select((OpenCvSharp.Rect face) => new FaceCandidate(face, "global Haar lock", null, 0.58)), previousFace, gray.Width, gray.Height);
		if (faceCandidate2 is null || !FaceCandidateSelector.IsAcceptableTrackingCandidate(faceCandidate2, previousFace, gray.Width, gray.Height, _framesSinceLandmarkLock))
		{
			return default(OpenCvSharp.Rect);
		}
		return faceCandidate2.Face;
	}

	private OpenCvSharp.Rect? GetPreviousFace(int width, int height)
	{
		FaceFeatureDetection? lastFeatureDetection = _lastFeatureDetection;
		if (lastFeatureDetection == null || !lastFeatureDetection.HasFace || lastFeatureDetection.FaceBox.Width <= 0.0 || lastFeatureDetection.FaceBox.Height <= 0.0)
		{
			return null;
		}
		return new OpenCvSharp.Rect((int)Math.Round(lastFeatureDetection.FaceBox.X * (double)width), (int)Math.Round(lastFeatureDetection.FaceBox.Y * (double)height), Math.Max(1, (int)Math.Round(lastFeatureDetection.FaceBox.Width * (double)width)), Math.Max(1, (int)Math.Round(lastFeatureDetection.FaceBox.Height * (double)height)));
	}

	private OpenCvSharp.Rect DetectLocalFace(Mat gray, OpenCvSharp.Rect lastFace, int framesSinceLock)
	{
		double fraction = 0.58 + (double)Math.Clamp(framesSinceLock, 0, 6) * 0.14;
		OpenCvSharp.Rect search = ExpandRect(lastFace, fraction, gray.Width, gray.Height);
		if (search.Width < Math.Max(40, gray.Width / 14) || search.Height < Math.Max(40, gray.Height / 14))
		{
			return default(OpenCvSharp.Rect);
		}
		using Mat image = new Mat(gray, search);
		return FaceCandidateSelector.SelectBest(from rect in _faceCascade?.DetectMultiScale(image, 1.05, 2, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(Math.Max(28, search.Width / 8), Math.Max(28, search.Height / 8))) ?? Array.Empty<OpenCvSharp.Rect>()
			where IsPlausibleLocalFace(rect, search)
			select new FaceCandidate(new OpenCvSharp.Rect(search.X + rect.X, search.Y + rect.Y, rect.Width, rect.Height), "local LBF reacquire", null, 0.56), lastFace, gray.Width, gray.Height)?.Face ?? default(OpenCvSharp.Rect);
	}

	private static bool IsPlausibleLocalFace(OpenCvSharp.Rect localFace, OpenCvSharp.Rect search)
	{
		if (localFace.Width <= 0 || localFace.Height <= 0)
		{
			return false;
		}
		double num = (double)localFace.Width / (double)Math.Max(1, localFace.Height);
		double num2 = (double)(localFace.Width * localFace.Height) / (double)Math.Max(1, search.Width * search.Height);
		if (num > 0.58 && num < 1.55)
		{
			if (num2 > 0.055)
			{
				return num2 < 0.96;
			}
			return false;
		}
		return false;
	}

	private static OpenCvSharp.Rect ExpandRect(OpenCvSharp.Rect rect, double fraction, int width, int height)
	{
		int num = (int)Math.Round((double)rect.Width * fraction);
		int num2 = (int)Math.Round((double)rect.Height * fraction);
		return ClampRect(new OpenCvSharp.Rect(rect.X - num, rect.Y - num2, rect.Width + num * 2, rect.Height + num2 * 2), width, height);
	}

	private static OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect rect, int width, int height)
	{
		int num = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
		int num2 = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
		int num3 = Math.Clamp(rect.Right, num + 1, width);
		int num4 = Math.Clamp(rect.Bottom, num2 + 1, height);
		return new OpenCvSharp.Rect(num, num2, num3 - num, num4 - num2);
	}

	private static FaceLandmarkFrame CreateLandmarkFrame(OpenCvSharp.Rect face, IReadOnlyList<Point2f> points, int width, int height, DateTime capturedAtUtc)
	{
		FaceLandmarkFrame faceLandmarkFrame = CreateLandmarkFrameFrom68Points(points.Select((Point2f point) => new System.Windows.Point(Math.Clamp((double)point.X / Math.Max(1.0, width), 0.0, 1.0), Math.Clamp((double)point.Y / Math.Max(1.0, height), 0.0, 1.0))).ToList(), capturedAtUtc);
		double val = CalculateFaceConfidence(face, width, height);
		return new FaceLandmarkFrame
		{
			HasFace = faceLandmarkFrame.HasFace,
			Source = faceLandmarkFrame.Source,
			CapturedAtUtc = faceLandmarkFrame.CapturedAtUtc,
			TrackingConfidence = Math.Max(faceLandmarkFrame.TrackingConfidence, val),
			EyeConfidence = faceLandmarkFrame.EyeConfidence,
			MouthConfidence = faceLandmarkFrame.MouthConfidence,
			HeadYawDegrees = faceLandmarkFrame.HeadYawDegrees,
			HeadPitchDegrees = faceLandmarkFrame.HeadPitchDegrees,
			HeadRollDegrees = faceLandmarkFrame.HeadRollDegrees,
			FaceContour = faceLandmarkFrame.FaceContour,
			LeftEyeContour = faceLandmarkFrame.LeftEyeContour,
			RightEyeContour = faceLandmarkFrame.RightEyeContour,
			LeftBrowContour = faceLandmarkFrame.LeftBrowContour,
			RightBrowContour = faceLandmarkFrame.RightBrowContour,
			OuterLipContour = faceLandmarkFrame.OuterLipContour,
			InnerLipContour = faceLandmarkFrame.InnerLipContour,
			JawContour = faceLandmarkFrame.JawContour
		};
	}

	private static FaceFeatureDetection CreateFeatureDetection(OpenCvSharp.Rect face, FaceLandmarkFrame landmarkFrame, int width, int height)
	{
		System.Windows.Rect? leftEyeBox = BoundingRect(landmarkFrame.LeftEyeContour);
		System.Windows.Rect? rightEyeBox = BoundingRect(landmarkFrame.RightEyeContour);
		System.Windows.Rect? mouthBox = BoundingRect((landmarkFrame.OuterLipContour.Count >= 4) ? landmarkFrame.OuterLipContour : landmarkFrame.InnerLipContour);
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

	private static System.Windows.Rect ToNormalizedRect(OpenCvSharp.Rect rect, int width, int height)
	{
		return new System.Windows.Rect((double)rect.X / (double)Math.Max(1, width), (double)rect.Y / (double)Math.Max(1, height), (double)rect.Width / (double)Math.Max(1, width), (double)rect.Height / (double)Math.Max(1, height));
	}

	private static System.Windows.Rect? BoundingRect(IReadOnlyList<System.Windows.Point> points)
	{
		if (points.Count == 0)
		{
			return null;
		}
		double num = points.Min((System.Windows.Point point) => point.X);
		double num2 = points.Max((System.Windows.Point point) => point.X);
		double num3 = points.Min((System.Windows.Point point) => point.Y);
		double num4 = points.Max((System.Windows.Point point) => point.Y);
		return new System.Windows.Rect(num, num3, num2 - num, num4 - num3);
	}

	private static double EstimateYawDegrees(IReadOnlyList<System.Windows.Point> points)
	{
		if (points.Count < 68)
		{
			return 0.0;
		}
		System.Windows.Point point = points[0];
		System.Windows.Point point2 = points[16];
		System.Windows.Point point3 = points[30];
		double num = (point.X + point2.X) / 2.0;
		double num2 = Math.Abs(point2.X - point.X) / 2.0;
		if (num2 <= 0.001)
		{
			return 0.0;
		}
		return Math.Clamp((point3.X - num) / num2 * 34.0, -45.0, 45.0);
	}

	private static double EstimatePitchDegrees(IReadOnlyList<System.Windows.Point> points)
	{
		if (points.Count < 68)
		{
			return 0.0;
		}
		double num = (AverageY(points, 36, 6) + AverageY(points, 42, 6)) / 2.0;
		double num2 = AverageY(points, 48, 12);
		System.Windows.Point point = points[30];
		double num3 = num2 - num;
		if (num3 <= 0.001)
		{
			return 0.0;
		}
		return Math.Clamp(((point.Y - num) / num3 - 0.52) * 50.0, -35.0, 35.0);
	}

	private static double AverageY(IReadOnlyList<System.Windows.Point> points, int start, int count)
	{
		return points.Skip(start).Take(count).Average((System.Windows.Point point) => point.Y);
	}

	private static IReadOnlyList<System.Windows.Point> Slice(IReadOnlyList<System.Windows.Point> points, int start, int count)
	{
		return points.Skip(start).Take(count).ToList();
	}

	private static (IReadOnlyList<System.Windows.Point> Left, IReadOnlyList<System.Windows.Point> Right) SortByFramePosition(IReadOnlyList<System.Windows.Point> first, IReadOnlyList<System.Windows.Point> second)
	{
		double num = ((first.Count == 0) ? 0.0 : first.Average((System.Windows.Point point) => point.X));
		double num2 = ((second.Count == 0) ? 1.0 : second.Average((System.Windows.Point point) => point.X));
		if (!(num <= num2))
		{
			return (Left: second, Right: first);
		}
		return (Left: first, Right: second);
	}

	private static IReadOnlyList<System.Windows.Point> CreateFaceContour(IReadOnlyList<System.Windows.Point> points)
	{
		List<System.Windows.Point> list = Slice(points, 0, 17).ToList();
		IReadOnlyList<System.Windows.Point> readOnlyList = Slice(points, 17, 5);
		IReadOnlyList<System.Windows.Point> readOnlyList2 = Slice(points, 22, 5);
		if (list.Count < 17 || readOnlyList.Count < 5 || readOnlyList2.Count < 5)
		{
			return list;
		}
		double num = Math.Min(readOnlyList.Min((System.Windows.Point point) => point.Y), readOnlyList2.Min((System.Windows.Point point) => point.Y));
		double num2 = Math.Min(list[0].Y, list[list.Count - 1].Y);
		double num3 = Math.Clamp(num - Math.Abs(num2 - num) * 0.72, 0.0, 1.0);
		double num4 = list.Average((System.Windows.Point point) => point.X);
		List<System.Windows.Point> obj = new List<System.Windows.Point>
		{
			new System.Windows.Point(list[0].X * 0.88 + num4 * 0.12, list[0].Y),
			new System.Windows.Point(readOnlyList[0].X * 0.78 + list[0].X * 0.22, (readOnlyList[0].Y + num3) / 2.0),
			new System.Windows.Point((readOnlyList[2].X + num4) / 2.0, num3),
			new System.Windows.Point(num4, Math.Max(0.0, num3 - 0.018)),
			new System.Windows.Point((readOnlyList2[2].X + num4) / 2.0, num3)
		};
		obj.Add(new System.Windows.Point(readOnlyList2[readOnlyList2.Count - 1].X * 0.78 + list[list.Count - 1].X * 0.22, (readOnlyList2[readOnlyList2.Count - 1].Y + num3) / 2.0));
		obj.Add(new System.Windows.Point(list[list.Count - 1].X * 0.88 + num4 * 0.12, list[list.Count - 1].Y));
		obj.AddRange(list.AsEnumerable().Reverse().Skip(1)
			.Take(15));
		return obj;
	}

	private static double EstimateRollDegrees(IReadOnlyList<System.Windows.Point> leftEye, IReadOnlyList<System.Windows.Point> rightEye)
	{
		if (leftEye.Count == 0 || rightEye.Count == 0)
		{
			return 0.0;
		}
		System.Windows.Point point = new System.Windows.Point(leftEye.Average((System.Windows.Point point3) => point3.X), leftEye.Average((System.Windows.Point point3) => point3.Y));
		System.Windows.Point point2 = new System.Windows.Point(rightEye.Average((System.Windows.Point point3) => point3.X), rightEye.Average((System.Windows.Point point3) => point3.Y));
		return Math.Atan2(point2.Y - point.Y, point2.X - point.X) * 180.0 / Math.PI;
	}

	private static double CalculateFaceConfidence(OpenCvSharp.Rect face, int width, int height)
	{
		double num = Math.Max(1.0, width * height);
		double num2 = Math.Clamp((double)(face.Width * face.Height) / num / 0.12, 0.0, 1.0);
		return Math.Clamp(0.5 + num2 * 0.34, 0.5, 0.84);
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
}
