using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.OpenCv;

namespace AvatarBuilder.Modules.Vision.Pipeline;

public sealed class CompositeFaceLandmarkTracker : IStatefulFaceLandmarkTracker, IFaceLandmarkTracker, IDisposable
{
	private static readonly TimeSpan PreviousFaceRecoveryWindow = TimeSpan.FromSeconds(2.5);

	private readonly IReadOnlyList<IFaceLandmarkTracker> _trackers;

	private int _maxDetectionDimension = 960;

	private string _lastBackendStatus = "waiting";

	private Rect? _lastFusedFace;

	private DateTime _lastFusedFaceCapturedAtUtc = DateTime.MinValue;

	public string Name => "Composite landmark tracker";

	public bool IsAvailable
	{
		get
		{
			foreach (IFaceLandmarkTracker tracker in _trackers)
			{
				if (tracker.IsAvailable)
				{
					return true;
				}
			}
			return false;
		}
	}

	public string LastBackendStatus => _lastBackendStatus;

	public int MaxDetectionDimension
	{
		get => _maxDetectionDimension;
		set
		{
			_maxDetectionDimension = value;
			foreach (IFaceLandmarkTracker tracker in _trackers)
			{
				tracker.MaxDetectionDimension = value;
			}
		}
	}

	public CompositeFaceLandmarkTracker(MediaPipeExecutionBackend executionBackend = MediaPipeExecutionBackend.Cpu)
		: this(CreateDefaultTrackers(executionBackend))
	{
	}

	public CompositeFaceLandmarkTracker(IReadOnlyList<IFaceLandmarkTracker> trackers)
	{
		_trackers = trackers;
		for (int i = 0; i < trackers.Count; i++)
		{
			_maxDetectionDimension = Math.Max(_maxDetectionDimension, trackers[i].MaxDetectionDimension);
		}
	}

	public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
	{
		List<string>? list = null;
		FaceLandmarkTrackingResult? faceLandmarkTrackingResult = null;
		IFaceLandmarkCropRefiner? faceLandmarkCropRefiner = null;
		foreach (IFaceLandmarkTracker tracker in _trackers)
		{
			if (faceLandmarkCropRefiner == null && tracker is IFaceLandmarkCropRefiner { IsAvailable: not false } faceLandmarkCropRefiner2)
			{
				faceLandmarkCropRefiner = faceLandmarkCropRefiner2;
			}
			if (!tracker.IsAvailable)
			{
				FaceLandmarkTrackingResult faceLandmarkTrackingResult2 = tracker.Detect(bitmap, capturedAtUtc);
				if (!string.IsNullOrWhiteSpace(faceLandmarkTrackingResult2.BackendStatus))
				{
					(list ??= new List<string>()).Add(tracker.Name + ": " + faceLandmarkTrackingResult2.BackendStatus);
				}
				continue;
			}
			FaceLandmarkTrackingResult faceLandmarkTrackingResult3 = tracker.Detect(bitmap, capturedAtUtc);
			if (faceLandmarkTrackingResult3.HasFace)
			{
				if (HasMediaPipeDenseLock(faceLandmarkTrackingResult3) && HasUsableFaceCueGeometry(faceLandmarkTrackingResult3))
				{
					_lastBackendStatus = faceLandmarkTrackingResult3.BackendStatus;
					_lastFusedFace = GetFaceBounds(faceLandmarkTrackingResult3) ?? _lastFusedFace;
					_lastFusedFaceCapturedAtUtc = capturedAtUtc;
					return faceLandmarkTrackingResult3;
				}
				(list ??= new List<string>()).Add(faceLandmarkTrackingResult3.BackendName + ": " + faceLandmarkTrackingResult3.BackendStatus);
				faceLandmarkTrackingResult = ((faceLandmarkTrackingResult == null) ? faceLandmarkTrackingResult3 : FuseResults(faceLandmarkTrackingResult, faceLandmarkTrackingResult3, capturedAtUtc, _lastFusedFace));
			}
			else if (!string.IsNullOrWhiteSpace(faceLandmarkTrackingResult3.BackendStatus))
			{
				(list ??= new List<string>()).Add(faceLandmarkTrackingResult3.BackendName + ": " + faceLandmarkTrackingResult3.BackendStatus);
			}
		}
		if (faceLandmarkTrackingResult != null)
		{
			list ??= new List<string>();
			faceLandmarkTrackingResult = TryRefineWithFaceCrop(bitmap, capturedAtUtc, faceLandmarkTrackingResult, faceLandmarkCropRefiner, list);
			_lastBackendStatus = string.Join(" | ", list);
			_lastFusedFace = GetFaceBounds(faceLandmarkTrackingResult) ?? _lastFusedFace;
			_lastFusedFaceCapturedAtUtc = capturedAtUtc;
			return new FaceLandmarkTrackingResult
			{
				BackendName = faceLandmarkTrackingResult.BackendName,
				BackendStatus = _lastBackendStatus,
				FeatureDetection = faceLandmarkTrackingResult.FeatureDetection,
				LandmarkFrame = faceLandmarkTrackingResult.LandmarkFrame,
				Diagnostics = faceLandmarkTrackingResult.Diagnostics
			};
		}
		list ??= new List<string>();
			FaceLandmarkTrackingResult? faceLandmarkTrackingResult4 = TryRecoverWithPreviousFaceCrop(bitmap, capturedAtUtc, faceLandmarkCropRefiner, list);
		if (faceLandmarkTrackingResult4 != null)
		{
			_lastBackendStatus = string.Join(" | ", list);
			_lastFusedFace = GetFaceBounds(faceLandmarkTrackingResult4) ?? _lastFusedFace;
			_lastFusedFaceCapturedAtUtc = capturedAtUtc;
			return new FaceLandmarkTrackingResult
			{
				BackendName = faceLandmarkTrackingResult4.BackendName,
				BackendStatus = _lastBackendStatus,
				FeatureDetection = faceLandmarkTrackingResult4.FeatureDetection,
				LandmarkFrame = faceLandmarkTrackingResult4.LandmarkFrame,
				Diagnostics = faceLandmarkTrackingResult4.Diagnostics
			};
		}
		_lastBackendStatus = ((list.Count != 0) ? string.Join(" | ", list) : (IsAvailable ? "all trackers searching" : "no landmark backend available"));
		if (capturedAtUtc - _lastFusedFaceCapturedAtUtc > PreviousFaceRecoveryWindow)
		{
			_lastFusedFace = null;
			_lastFusedFaceCapturedAtUtc = DateTime.MinValue;
		}
		return new FaceLandmarkTrackingResult
		{
			BackendName = Name,
			BackendStatus = _lastBackendStatus
		};
	}

	public void Dispose()
	{
		foreach (IFaceLandmarkTracker tracker in _trackers)
		{
			tracker.Dispose();
		}
	}

	private static IReadOnlyList<IFaceLandmarkTracker> CreateDefaultTrackers(MediaPipeExecutionBackend executionBackend)
	{
		List<IFaceLandmarkTracker> list = new List<IFaceLandmarkTracker>(3);
		try
		{
			AddOwnedTracker(list, () => new MediaPipeFaceLandmarkerSidecarTracker(executionBackend));
			AddOwnedTracker(list, () => new OpenCvFacemarkLandmarkTracker());
			AddOwnedTracker(list, () => new OpenCvApertureLandmarkTracker());
			return list;
		}
		catch
		{
			foreach (IFaceLandmarkTracker item in list)
			{
				item.Dispose();
			}
			throw;
		}
	}

	private static void AddOwnedTracker(ICollection<IFaceLandmarkTracker> trackers, Func<IFaceLandmarkTracker> createTracker)
	{
		IFaceLandmarkTracker? faceLandmarkTracker = null;
		try
		{
			faceLandmarkTracker = createTracker();
			trackers.Add(faceLandmarkTracker);
			faceLandmarkTracker = null;
		}
		finally
		{
			faceLandmarkTracker?.Dispose();
		}
	}

	private static FaceLandmarkTrackingResult TryRefineWithFaceCrop(BitmapSource bitmap, DateTime capturedAtUtc, FaceLandmarkTrackingResult fused, IFaceLandmarkCropRefiner? cropRefiner, List<string> statuses)
	{
		if (cropRefiner == null || HasMediaPipeDenseLock(fused))
		{
			return fused;
		}
		Rect? faceBounds = GetFaceBounds(fused);
		if (faceBounds.HasValue)
		{
			Rect valueOrDefault = faceBounds.GetValueOrDefault();
			if (!(valueOrDefault.Width <= 0.0) && !(valueOrDefault.Height <= 0.0))
			{
				FaceLandmarkTrackingResult faceLandmarkTrackingResult = cropRefiner.DetectFaceCrop(bitmap, valueOrDefault, capturedAtUtc);
				if (!string.IsNullOrWhiteSpace(faceLandmarkTrackingResult.BackendStatus))
				{
					statuses.Add(faceLandmarkTrackingResult.BackendName + ": " + faceLandmarkTrackingResult.BackendStatus);
				}
				if (!faceLandmarkTrackingResult.HasFace || !HasMediaPipeDenseLock(faceLandmarkTrackingResult) || !FacesAgree(faceLandmarkTrackingResult, fused))
				{
					return fused;
				}
				return FuseResults(faceLandmarkTrackingResult, fused, capturedAtUtc, null);
			}
		}
		return fused;
	}

	private FaceLandmarkTrackingResult? TryRecoverWithPreviousFaceCrop(BitmapSource bitmap, DateTime capturedAtUtc, IFaceLandmarkCropRefiner? cropRefiner, List<string> statuses)
	{
		if (cropRefiner != null)
		{
			Rect? lastFusedFace = _lastFusedFace;
			if (lastFusedFace.HasValue)
			{
				Rect valueOrDefault = lastFusedFace.GetValueOrDefault();
				if (!(valueOrDefault.Width <= 0.0) && !(valueOrDefault.Height <= 0.0))
				{
					TimeSpan timeSpan = capturedAtUtc - _lastFusedFaceCapturedAtUtc;
					if (timeSpan < TimeSpan.Zero || timeSpan > PreviousFaceRecoveryWindow)
					{
						return null;
					}
					FaceLandmarkTrackingResult faceLandmarkTrackingResult = cropRefiner.DetectFaceCrop(bitmap, valueOrDefault, capturedAtUtc);
					if (!string.IsNullOrWhiteSpace(faceLandmarkTrackingResult.BackendStatus))
					{
						statuses.Add(faceLandmarkTrackingResult.BackendName + ": " + faceLandmarkTrackingResult.BackendStatus);
					}
					if (!faceLandmarkTrackingResult.HasFace || !HasMediaPipeDenseLock(faceLandmarkTrackingResult) || !HasUsableFaceCueGeometry(faceLandmarkTrackingResult))
					{
						return null;
					}
					statuses.Add($"{faceLandmarkTrackingResult.BackendName}: temporal recovery from previous face hint ({timeSpan.TotalSeconds:0.00}s old)");
					return new FaceLandmarkTrackingResult
					{
						BackendName = faceLandmarkTrackingResult.BackendName,
						BackendStatus = $"{faceLandmarkTrackingResult.BackendStatus}; temporal recovery from previous face hint ({timeSpan.TotalSeconds:0.00}s old)",
						FeatureDetection = faceLandmarkTrackingResult.FeatureDetection,
						LandmarkFrame = faceLandmarkTrackingResult.LandmarkFrame
					};
				}
			}
		}
		return null;
	}

	private static FaceLandmarkTrackingResult FuseResults(FaceLandmarkTrackingResult primary, FaceLandmarkTrackingResult candidate, DateTime capturedAtUtc, Rect? previousFusedFace)
	{
		if (!primary.HasFace)
		{
			return candidate;
		}
		if (!candidate.HasFace || !FacesAgree(primary, candidate))
		{
			return ChooseDisagreementResult(primary, candidate, previousFusedFace);
		}
		FaceLandmarkFrame landmarkFrame = primary.LandmarkFrame;
		FaceLandmarkFrame landmarkFrame2 = candidate.LandmarkFrame;
		bool flag = ShouldUseCandidateEyes(landmarkFrame, landmarkFrame2, candidate.BackendName);
		bool flag2 = ShouldUseCandidateMouth(landmarkFrame, landmarkFrame2, candidate.BackendName);
		if (!flag && !flag2)
		{
			return primary;
		}
		FaceFeatureDetection featureDetection = primary.FeatureDetection;
		FaceFeatureDetection featureDetection2 = candidate.FeatureDetection;
		FaceLandmarkFrame faceLandmarkFrame = new FaceLandmarkFrame
		{
			HasFace = true,
			Source = landmarkFrame.Source + "; fused " + landmarkFrame2.Source,
			CapturedAtUtc = capturedAtUtc,
			TrackingConfidence = Math.Max(landmarkFrame.TrackingConfidence, landmarkFrame2.TrackingConfidence * 0.92),
			EyeConfidence = (flag ? landmarkFrame2.EyeConfidence : landmarkFrame.EyeConfidence),
			MouthConfidence = (flag2 ? landmarkFrame2.MouthConfidence : landmarkFrame.MouthConfidence),
			EyeImageQualityAvailable = (flag ? landmarkFrame2.EyeImageQualityAvailable : landmarkFrame.EyeImageQualityAvailable),
			MouthImageQualityAvailable = (flag2 ? landmarkFrame2.MouthImageQualityAvailable : landmarkFrame.MouthImageQualityAvailable),
			EyeGlarePercent = (flag ? landmarkFrame2.EyeGlarePercent : landmarkFrame.EyeGlarePercent),
			MouthGlarePercent = (flag2 ? landmarkFrame2.MouthGlarePercent : landmarkFrame.MouthGlarePercent),
			EyeContrastPercent = (flag ? landmarkFrame2.EyeContrastPercent : landmarkFrame.EyeContrastPercent),
			MouthContrastPercent = (flag2 ? landmarkFrame2.MouthContrastPercent : landmarkFrame.MouthContrastPercent),
			EyeSharpnessPercent = (flag ? landmarkFrame2.EyeSharpnessPercent : landmarkFrame.EyeSharpnessPercent),
			MouthSharpnessPercent = (flag2 ? landmarkFrame2.MouthSharpnessPercent : landmarkFrame.MouthSharpnessPercent),
			EyeDarkCoveragePercent = (flag ? landmarkFrame2.EyeDarkCoveragePercent : landmarkFrame.EyeDarkCoveragePercent),
			MouthDarkCoveragePercent = (flag2 ? landmarkFrame2.MouthDarkCoveragePercent : landmarkFrame.MouthDarkCoveragePercent),
			LeftEyeReconstructed = (flag ? landmarkFrame2.LeftEyeReconstructed : landmarkFrame.LeftEyeReconstructed),
			RightEyeReconstructed = (flag ? landmarkFrame2.RightEyeReconstructed : landmarkFrame.RightEyeReconstructed),
			MouthReconstructed = (flag2 ? landmarkFrame2.MouthReconstructed : landmarkFrame.MouthReconstructed),
			EyeArtifactSuppressed = (flag ? landmarkFrame2.EyeArtifactSuppressed : landmarkFrame.EyeArtifactSuppressed),
			HeadYawDegrees = landmarkFrame.HeadYawDegrees,
			HeadPitchDegrees = landmarkFrame.HeadPitchDegrees,
			HeadRollDegrees = landmarkFrame.HeadRollDegrees,
			BlendshapeScores = ((landmarkFrame.BlendshapeScores.Count > 0) ? landmarkFrame.BlendshapeScores : landmarkFrame2.BlendshapeScores),
			MediaPipeEyeBlinkLeftScore = landmarkFrame.MediaPipeEyeBlinkLeftScore ?? landmarkFrame2.MediaPipeEyeBlinkLeftScore,
			MediaPipeEyeBlinkRightScore = landmarkFrame.MediaPipeEyeBlinkRightScore ?? landmarkFrame2.MediaPipeEyeBlinkRightScore,
			MediaPipeJawOpenScore = landmarkFrame.MediaPipeJawOpenScore ?? landmarkFrame2.MediaPipeJawOpenScore,
			MediaPipeMouthCloseScore = landmarkFrame.MediaPipeMouthCloseScore ?? landmarkFrame2.MediaPipeMouthCloseScore,
			DenseMeshTopology = ((landmarkFrame.DenseMeshPoints.Count > 0) ? landmarkFrame.DenseMeshTopology : landmarkFrame2.DenseMeshTopology),
			DenseMeshPoints = ((landmarkFrame.DenseMeshPoints.Count > 0) ? landmarkFrame.DenseMeshPoints : landmarkFrame2.DenseMeshPoints),
			FacialTransformationMatrix = ((landmarkFrame.FacialTransformationMatrix.Count > 0) ? landmarkFrame.FacialTransformationMatrix : landmarkFrame2.FacialTransformationMatrix),
			FaceContour = ((landmarkFrame.FaceContour.Count > 0) ? landmarkFrame.FaceContour : landmarkFrame2.FaceContour),
			LeftEyeContour = (flag ? landmarkFrame2.LeftEyeContour : landmarkFrame.LeftEyeContour),
			RightEyeContour = (flag ? landmarkFrame2.RightEyeContour : landmarkFrame.RightEyeContour),
			LeftBrowContour = ((flag && landmarkFrame2.LeftBrowContour.Count > 0) ? landmarkFrame2.LeftBrowContour : ((landmarkFrame.LeftBrowContour.Count > 0) ? landmarkFrame.LeftBrowContour : landmarkFrame2.LeftBrowContour)),
			RightBrowContour = ((flag && landmarkFrame2.RightBrowContour.Count > 0) ? landmarkFrame2.RightBrowContour : ((landmarkFrame.RightBrowContour.Count > 0) ? landmarkFrame.RightBrowContour : landmarkFrame2.RightBrowContour)),
			OuterLipContour = (flag2 ? landmarkFrame2.OuterLipContour : landmarkFrame.OuterLipContour),
			InnerLipContour = (flag2 ? landmarkFrame2.InnerLipContour : landmarkFrame.InnerLipContour),
			JawContour = ((landmarkFrame.JawContour.Count > 0) ? landmarkFrame.JawContour : landmarkFrame2.JawContour)
		};
		FaceFeatureDetection featureDetection3 = new FaceFeatureDetection
		{
			HasFace = true,
			Source = featureDetection.Source + "; fused " + featureDetection2.Source,
			FaceBox = (featureDetection.HasFace ? featureDetection.FaceBox : featureDetection2.FaceBox),
			LeftEyeBox = ((!flag) ? featureDetection.LeftEyeBox : (featureDetection2.LeftEyeBox ?? featureDetection.LeftEyeBox)),
			RightEyeBox = ((!flag) ? featureDetection.RightEyeBox : (featureDetection2.RightEyeBox ?? featureDetection.RightEyeBox)),
			MouthBox = ((!flag2) ? featureDetection.MouthBox : (featureDetection2.MouthBox ?? featureDetection.MouthBox)),
			TrackingConfidence = faceLandmarkFrame.TrackingConfidence,
			EyeConfidence = faceLandmarkFrame.EyeConfidence,
			MouthConfidence = faceLandmarkFrame.MouthConfidence,
			EyeImageQualityAvailable = faceLandmarkFrame.EyeImageQualityAvailable,
			MouthImageQualityAvailable = faceLandmarkFrame.MouthImageQualityAvailable,
			EyeGlarePercent = faceLandmarkFrame.EyeGlarePercent,
			MouthGlarePercent = faceLandmarkFrame.MouthGlarePercent,
			EyeContrastPercent = faceLandmarkFrame.EyeContrastPercent,
			MouthContrastPercent = faceLandmarkFrame.MouthContrastPercent,
			EyeSharpnessPercent = faceLandmarkFrame.EyeSharpnessPercent,
			MouthSharpnessPercent = faceLandmarkFrame.MouthSharpnessPercent,
			EyeDarkCoveragePercent = faceLandmarkFrame.EyeDarkCoveragePercent,
			MouthDarkCoveragePercent = faceLandmarkFrame.MouthDarkCoveragePercent,
			FaceContour = faceLandmarkFrame.FaceContour,
			LeftEyeContour = faceLandmarkFrame.LeftEyeContour,
			RightEyeContour = faceLandmarkFrame.RightEyeContour,
			OuterLipContour = faceLandmarkFrame.OuterLipContour,
			InnerLipContour = faceLandmarkFrame.InnerLipContour,
			JawContour = faceLandmarkFrame.JawContour
		};
		return new FaceLandmarkTrackingResult
		{
			BackendName = "Composite landmark fusion",
			BackendStatus = primary.BackendStatus + "; fused " + candidate.BackendStatus,
			FeatureDetection = featureDetection3,
			LandmarkFrame = faceLandmarkFrame
		};
	}

	private static bool ShouldUseCandidateEyes(FaceLandmarkFrame primary, FaceLandmarkFrame candidate, string candidateBackend)
	{
		if (!candidate.HasEyeContours || candidate.EyeConfidence < 0.2)
		{
			return false;
		}
		if (!primary.HasEyeContours)
		{
			return true;
		}
		if (IsHighFidelityLandmarkSource(primary.Source) && candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase))
		{
			if (!(primary.EyeConfidence < 0.55) && !(candidate.EyeConfidence >= primary.EyeConfidence + 0.16) && !primary.EyeArtifactSuppressed)
			{
				return !primary.HasEyeContours;
			}
			return true;
		}
		if (!candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase))
		{
			return candidate.EyeConfidence >= primary.EyeConfidence + 0.08;
		}
		return true;
	}

	private static bool ShouldUseCandidateMouth(FaceLandmarkFrame primary, FaceLandmarkFrame candidate, string candidateBackend)
	{
		if (!candidate.HasMouthContours || candidate.MouthConfidence < 0.18)
		{
			return false;
		}
		if (!primary.HasMouthContours)
		{
			return true;
		}
		if (IsHighFidelityLandmarkSource(primary.Source) && candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase))
		{
			if (!(primary.MouthConfidence < 0.52) && !(candidate.MouthConfidence >= primary.MouthConfidence + 0.16) && !primary.MouthReconstructed)
			{
				return !primary.HasMouthContours;
			}
			return true;
		}
		if (!candidateBackend.Contains("aperture", StringComparison.OrdinalIgnoreCase))
		{
			return candidate.MouthConfidence >= primary.MouthConfidence + 0.08;
		}
		return true;
	}

	private static bool IsHighFidelityLandmarkSource(string source)
	{
		if (!source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase) && !source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase) && !source.Contains("dense", StringComparison.OrdinalIgnoreCase))
		{
			return source.Contains("face mesh", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool HasMediaPipeDenseLock(FaceLandmarkTrackingResult result)
	{
		if (!result.BackendStatus.Contains("MediaPipe dense landmark lock", StringComparison.OrdinalIgnoreCase) && !result.LandmarkFrame.Source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase))
		{
			return result.LandmarkFrame.Source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool HasUsableFaceCueGeometry(FaceLandmarkTrackingResult result)
	{
		if (!result.LandmarkFrame.HasEyeContours)
		{
			return result.LandmarkFrame.HasMouthContours;
		}
		return true;
	}

	private static FaceLandmarkTrackingResult ChooseDisagreementResult(FaceLandmarkTrackingResult primary, FaceLandmarkTrackingResult candidate, Rect? previousFusedFace)
	{
		if (!candidate.HasFace)
		{
			return primary;
		}
		if (previousFusedFace.HasValue)
		{
			Rect valueOrDefault = previousFusedFace.GetValueOrDefault();
			double num = CalculateContinuityScore(GetFaceBounds(primary), valueOrDefault);
			double num2 = CalculateContinuityScore(GetFaceBounds(candidate), valueOrDefault);
			if (num2 >= num + 0.18)
			{
				return new FaceLandmarkTrackingResult
				{
					BackendName = candidate.BackendName,
					BackendStatus = candidate.BackendStatus + "; selected over disagreeing " + primary.BackendName + " by temporal face continuity",
					FeatureDetection = candidate.FeatureDetection,
					LandmarkFrame = candidate.LandmarkFrame
				};
			}
			if (num >= num2 + 0.08)
			{
				return primary;
			}
		}
		if (candidate.BackendName.Contains("aperture", StringComparison.OrdinalIgnoreCase) && !primary.BackendName.Contains("dense", StringComparison.OrdinalIgnoreCase) && candidate.LandmarkFrame.TrackingConfidence >= primary.LandmarkFrame.TrackingConfidence - 0.2 && (candidate.LandmarkFrame.EyeConfidence >= primary.LandmarkFrame.EyeConfidence || candidate.LandmarkFrame.MouthConfidence >= primary.LandmarkFrame.MouthConfidence))
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = candidate.BackendName,
				BackendStatus = candidate.BackendStatus + "; selected over disagreeing " + primary.BackendName + " by aperture cue confidence",
				FeatureDetection = candidate.FeatureDetection,
				LandmarkFrame = candidate.LandmarkFrame
			};
		}
		return primary;
	}

	private static double CalculateContinuityScore(Rect? face, Rect previous)
	{
		if (face.HasValue)
		{
			Rect valueOrDefault = face.GetValueOrDefault();
			if (!(valueOrDefault.Width <= 0.0) && !(valueOrDefault.Height <= 0.0))
			{
				Point point = new Point(valueOrDefault.Left + valueOrDefault.Width / 2.0, valueOrDefault.Top + valueOrDefault.Height / 2.0);
				Point point2 = new Point(previous.Left + previous.Width / 2.0, previous.Top + previous.Height / 2.0);
				double num = Math.Sqrt(Math.Pow(point.X - point2.X, 2.0) + Math.Pow(point.Y - point2.Y, 2.0));
				double num2 = Math.Sqrt(previous.Width * previous.Width + previous.Height * previous.Height);
				double num3 = 1.0 - Math.Clamp(num / Math.Max(0.001, num2 * 1.2), 0.0, 1.0);
				double num4 = OverlapOverSmaller(valueOrDefault, previous);
				double num5 = LogSimilarity(valueOrDefault.Width * valueOrDefault.Height, Math.Max(1E-06, previous.Width * previous.Height), 4.0);
				return num3 * 0.42 + num4 * 0.42 + num5 * 0.16;
			}
		}
		return 0.0;
	}

	private static bool FacesAgree(FaceLandmarkTrackingResult primary, FaceLandmarkTrackingResult candidate)
	{
		Rect? faceBounds = GetFaceBounds(primary);
		Rect? faceBounds2 = GetFaceBounds(candidate);
		if (!faceBounds.HasValue || !faceBounds2.HasValue)
		{
			return true;
		}
		Rect rect = Rect.Intersect(faceBounds.Value, faceBounds2.Value);
		double num = Math.Max(0.0, rect.Width) * Math.Max(0.0, rect.Height);
		double num2 = Math.Min(faceBounds.Value.Width * faceBounds.Value.Height, faceBounds2.Value.Width * faceBounds2.Value.Height);
		if (num2 > 0.0 && num / num2 >= 0.2)
		{
			return true;
		}
		Point point = new Point(faceBounds.Value.Left + faceBounds.Value.Width / 2.0, faceBounds.Value.Top + faceBounds.Value.Height / 2.0);
		Point point2 = new Point(faceBounds2.Value.Left + faceBounds2.Value.Width / 2.0, faceBounds2.Value.Top + faceBounds2.Value.Height / 2.0);
		return Math.Sqrt(Math.Pow(point.X - point2.X, 2.0) + Math.Pow(point.Y - point2.Y, 2.0)) <= Math.Max(faceBounds.Value.Height, faceBounds2.Value.Height) * 0.45;
	}

	private static double OverlapOverSmaller(Rect first, Rect second)
	{
		Rect rect = Rect.Intersect(first, second);
		double num = Math.Max(0.0, rect.Width) * Math.Max(0.0, rect.Height);
		double num2 = Math.Min(first.Width * first.Height, second.Width * second.Height);
		if (!(num2 <= 0.0))
		{
			return num / num2;
		}
		return 0.0;
	}

	private static double LogSimilarity(double value, double target, double toleranceFactor)
	{
		if (value <= 0.0 || target <= 0.0)
		{
			return 0.0;
		}
		double num = Math.Abs(Math.Log(value / target));
		return 1.0 - Math.Clamp(num / Math.Log(Math.Max(1.01, toleranceFactor)), 0.0, 1.0);
	}

	private static Rect? GetFaceBounds(FaceLandmarkTrackingResult result)
	{
		if (result.FeatureDetection.HasFace && result.FeatureDetection.FaceBox.Width > 0.0 && result.FeatureDetection.FaceBox.Height > 0.0)
		{
			return result.FeatureDetection.FaceBox;
		}
		return Bounds(result.LandmarkFrame.FaceContour);
	}

	private static Rect? Bounds(IReadOnlyList<Point> points)
	{
		if (points.Count == 0)
		{
			return null;
		}
		Point point = points[0];
		double num = point.X;
		double num2 = point.X;
		double num3 = point.Y;
		double num4 = point.Y;
		for (int i = 1; i < points.Count; i++)
		{
			point = points[i];
			num = Math.Min(num, point.X);
			num2 = Math.Max(num2, point.X);
			num3 = Math.Min(num3, point.Y);
			num4 = Math.Max(num4, point.Y);
		}
		if (!(num2 <= num) && !(num4 <= num3))
		{
			return new Rect(num, num3, num2 - num, num4 - num3);
		}
		return null;
	}

	public void Reset()
	{
		_lastBackendStatus = "waiting";
		_lastFusedFace = null;
		_lastFusedFaceCapturedAtUtc = DateTime.MinValue;
		foreach (IFaceLandmarkTracker tracker in _trackers)
		{
			if (tracker is IStatefulFaceLandmarkTracker statefulTracker)
			{
				statefulTracker.Reset();
			}
		}
	}
}
