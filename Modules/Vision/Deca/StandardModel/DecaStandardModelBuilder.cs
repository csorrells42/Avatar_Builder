using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Storage.AvatarObservations;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed class DecaStandardModelBuilder
{
	private static readonly HashSet<string> SupportedImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

	private readonly AvatarObservationRepository _repository;

	private readonly AvatarStandardModelStore _modelStore;

	private readonly DecaPinnedStillConvergenceOptions _convergenceOptions;

	public DecaStandardModelBuilder(AvatarObservationRepository? repository = null, AvatarStandardModelStore? modelStore = null, DecaPinnedStillConvergenceOptions? convergenceOptions = null)
	{
		_repository = repository ?? new AvatarObservationRepository();
		_modelStore = modelStore ?? new AvatarStandardModelStore();
		_convergenceOptions = convergenceOptions ?? new DecaPinnedStillConvergenceOptions();
	}

	public Task<AvatarStandardModelBuildResult> BuildAsync(string profileFolder, string subjectId, string subjectDisplayName, string sourceFolder, IProgress<AvatarStandardModelBuildProgress>? progress = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentException.ThrowIfNullOrWhiteSpace(subjectId, "subjectId");
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder, "sourceFolder");
		return Task.Run(() => Build(profileFolder, subjectId, subjectDisplayName, sourceFolder, progress, cancellationToken), cancellationToken);
	}

	private AvatarStandardModelBuildResult Build(string profileFolder, string subjectId, string subjectDisplayName, string sourceFolder, IProgress<AvatarStandardModelBuildProgress>? progress, CancellationToken cancellationToken)
	{
		if (!Directory.Exists(sourceFolder))
		{
			throw new DirectoryNotFoundException("Still-image folder was not found: " + sourceFolder);
		}
		List<string> list = (from path2 in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
			where SupportedImageExtensions.Contains(Path.GetExtension(path2))
			select path2).OrderBy<string, string>((string result) => result, StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			throw new InvalidOperationException("The selected folder does not contain supported still images.");
		}
		MediaPipeSidecarPythonEnvironment mediaPipeSidecarPythonEnvironment = MediaPipeSidecarPythonEnvironment.Detect(DenseFaceLandmarkModelInfo.Load());
		if (!mediaPipeSidecarPythonEnvironment.IsReady)
		{
			throw new InvalidOperationException(mediaPipeSidecarPythonEnvironment.Status);
		}
		DecaSidecarEnvironment decaSidecarEnvironment = DecaSidecarEnvironment.Detect();
		if (!decaSidecarEnvironment.IsReady)
		{
			throw new InvalidOperationException(decaSidecarEnvironment.Status);
		}
		List<string> list2 = new List<string>();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		AvatarStandardModel avatarStandardModel = null;
		_repository.DeleteBackend(profileFolder, subjectId, "deca-flame-standard-model-checkpoint-v1");
		_modelStore.Delete(profileFolder);
		using DecaReconstructionClient decaReconstructionClient = new DecaReconstructionClient(decaSidecarEnvironment);
		decaReconstructionClient.SetCurrentModelShapeCoefficients(Array.Empty<double>());
		for (int num5 = 0; num5 < list.Count; num5++)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return new AvatarStandardModelBuildResult(avatarStandardModel, num4, list2, Cancelled: true);
			}
			string path = list[num5];
			string fileName = Path.GetFileName(path);
			progress?.Report(new AvatarStandardModelBuildProgress(list.Count, num, num2, num3, fileName, $"Analyzing still {num5 + 1} of {list.Count}: {fileName}"));
			try
			{
				BitmapSource bitmapSource = LoadBitmap(path);
				DateTime dateTime = File.GetLastWriteTimeUtc(path);
				if (dateTime == default(DateTime))
				{
					dateTime = DateTime.UtcNow.AddMilliseconds(num5);
				}
				using MediaPipeFaceLandmarkerSidecarClient mediaPipeFaceLandmarkerSidecarClient = new MediaPipeFaceLandmarkerSidecarClient(mediaPipeSidecarPythonEnvironment);
				FaceLandmarkTrackingResult faceLandmarkTrackingResult = MediaPipeFaceLandmarkerMapper.ToTrackingResult(mediaPipeFaceLandmarkerSidecarClient.Analyze(bitmapSource, dateTime, bitmapSource.PixelWidth, bitmapSource.PixelHeight), dateTime, "MediaPipe Standard Model still analysis");
				if (!faceLandmarkTrackingResult.HasFace || !faceLandmarkTrackingResult.LandmarkFrame.HasDenseMesh || faceLandmarkTrackingResult.FeatureDetection.FaceBox.Width <= 0.0 || faceLandmarkTrackingResult.FeatureDetection.FaceBox.Height <= 0.0)
				{
					throw new InvalidOperationException(string.IsNullOrWhiteSpace(faceLandmarkTrackingResult.BackendStatus) ? "MediaPipe did not lock the face in this still." : faceLandmarkTrackingResult.BackendStatus);
				}
				DecaSidecarFaceBox faceBox = CreateFaceBox(faceLandmarkTrackingResult);
				AvatarReconstructionSnapshot avatarReconstructionSnapshot = decaReconstructionClient.Reconstruct(bitmapSource, dateTime, faceBox, faceLandmarkTrackingResult.LandmarkFrame, _convergenceOptions);
				if (avatarReconstructionSnapshot == null)
				{
					throw new InvalidOperationException(decaReconstructionClient.Status);
				}
				AvatarCaptureQualityAssessment captureQuality = CreateQuality(faceLandmarkTrackingResult);
				FaceFrameGeometry faceGeometry = CreateGeometry(faceLandmarkTrackingResult, dateTime);
				AvatarObservationWriteResult avatarObservationWriteResult = _repository.SaveCapture(new AvatarObservationCapture(profileFolder, subjectId, subjectDisplayName, avatarReconstructionSnapshot, bitmapSource, captureQuality, faceGeometry));
				if (!avatarObservationWriteResult.Accepted)
				{
					throw new InvalidOperationException(avatarObservationWriteResult.Detail);
				}
				num4++;
				num++;
				if (avatarReconstructionSnapshot.PinnedStillConverged)
				{
					num2++;
				}
				avatarStandardModel = new AvatarStandardModel
				{
					SubjectId = subjectId,
					SubjectDisplayName = subjectDisplayName,
					SourceFolder = Path.GetFullPath(sourceFolder),
					UpdatedAtUtc = DateTime.UtcNow,
					SourceImageCount = list.Count,
					CompletedImageCount = num,
					ConvergedImageCount = num2,
					FailedImageCount = num3,
					LastSourceImageName = fileName,
					ModelSequenceNumber = avatarReconstructionSnapshot.CurrentModelSequenceNumber,
					CoefficientDeltaRms = avatarReconstructionSnapshot.CurrentModelCoefficientDeltaRms,
					LastStillConverged = avatarReconstructionSnapshot.PinnedStillConverged,
					LastStillPassCount = avatarReconstructionSnapshot.PinnedStillPassCount,
					LastMeasuredFitPercent = avatarReconstructionSnapshot.ReconstructionConfidencePercent,
					ShapeCoefficients = avatarReconstructionSnapshot.CurrentModelShapeCoefficients.ToList(),
					CanonicalIdentityVertices = avatarReconstructionSnapshot.CanonicalIdentityVertices.ToList(),
					TopologyEdges = avatarReconstructionSnapshot.TopologyEdges.ToList()
				};
				_modelStore.Write(profileFolder, avatarStandardModel);
				progress?.Report(new AvatarStandardModelBuildProgress(list.Count, num, num2, num3, fileName, avatarReconstructionSnapshot.PinnedStillConverged ? $"Still {num5 + 1} converged and became the exact seed for the next angle." : $"Still {num5 + 1} reached its pass limit and became the exact seed for the next angle.", avatarReconstructionSnapshot.CurrentModelCoefficientDeltaRms, avatarReconstructionSnapshot.PinnedStillPassCount));
			}
			catch (OperationCanceledException)
			{
				return new AvatarStandardModelBuildResult(avatarStandardModel, num4, list2, Cancelled: true);
			}
			catch (Exception ex2)
			{
				num3++;
				list2.Add(fileName + ": " + ex2.Message);
				progress?.Report(new AvatarStandardModelBuildProgress(list.Count, num, num2, num3, fileName, "Skipped " + fileName + ": " + ex2.Message));
			}
		}
		if ((object)avatarStandardModel != null && avatarStandardModel.FailedImageCount != num3)
		{
			avatarStandardModel = avatarStandardModel with
			{
				FailedImageCount = num3,
				UpdatedAtUtc = DateTime.UtcNow
			};
			_modelStore.Write(profileFolder, avatarStandardModel);
		}
		return new AvatarStandardModelBuildResult(avatarStandardModel, num4, list2, Cancelled: false);
	}

	private static BitmapSource LoadBitmap(string path)
	{
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		bitmapImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
		bitmapImage.UriSource = new Uri(path, UriKind.Absolute);
		bitmapImage.EndInit();
		bitmapImage.Freeze();
		return bitmapImage;
	}

	private static DecaSidecarFaceBox CreateFaceBox(FaceLandmarkTrackingResult tracking)
	{
		Rect faceBox = tracking.FeatureDetection.FaceBox;
		return new DecaSidecarFaceBox
		{
			Left = faceBox.Left,
			Top = faceBox.Top,
			Right = faceBox.Right,
			Bottom = faceBox.Bottom,
			Normalized = true,
			Confidence = Math.Clamp(tracking.FeatureDetection.TrackingConfidence, 0.01, 1.0)
		};
	}

	private static AvatarCaptureQualityAssessment CreateQuality(FaceLandmarkTrackingResult tracking)
	{
		Rect faceBox = tracking.FeatureDetection.FaceBox;
		double num = Math.Clamp(tracking.LandmarkFrame.TrackingConfidence * 100.0, 0.0, 100.0);
		return new AvatarCaptureQualityAssessment
		{
			Label = ((num >= 80.0) ? "strong" : "usable"),
			ScorePercent = num,
			CanCollectMeasurements = true,
			StrongEnoughForAvatarLearning = true,
			PrimaryReason = "MediaPipe locked the supplied still.",
			StatusLine = $"Still quality: {num:0.#}%",
			CameraModeScorePercent = 100.0,
			FaceScaleScorePercent = 100.0,
			EyeEvidenceScorePercent = tracking.LandmarkFrame.EyeConfidence * 100.0,
			MouthEvidenceScorePercent = tracking.LandmarkFrame.MouthConfidence * 100.0,
			StabilityScorePercent = 100.0,
			StorageScorePercent = 100.0,
			FaceWidthPercent = faceBox.Width * 100.0,
			FaceHeightPercent = faceBox.Height * 100.0
		};
	}

	private static FaceFrameGeometry CreateGeometry(FaceLandmarkTrackingResult tracking, DateTime capturedAtUtc)
	{
		Rect faceBox = tracking.FeatureDetection.FaceBox;
		return new FaceFrameGeometry
		{
			HasFace = true,
			CapturedAtUtc = capturedAtUtc,
			YawDegrees = tracking.LandmarkFrame.HeadYawDegrees,
			PitchDegrees = tracking.LandmarkFrame.HeadPitchDegrees,
			RollDegrees = tracking.LandmarkFrame.HeadRollDegrees,
			XHorizontalPercent = (faceBox.Left + faceBox.Width / 2.0) * 100.0,
			YVerticalPercent = (faceBox.Top + faceBox.Height / 2.0) * 100.0,
			ApparentDistanceUnits = ((faceBox.Height <= 0.0) ? ((double?)null) : new double?(1.0 / faceBox.Height)),
			FaceFillWidthPercent = faceBox.Width * 100.0,
			FaceFillHeightPercent = faceBox.Height * 100.0,
			RelativeDistanceScale = 1.0,
			ConfidencePercent = tracking.LandmarkFrame.TrackingConfidence * 100.0,
			ZConfidencePercent = 70.0,
			RotationSource = tracking.LandmarkFrame.Source,
			DistanceSource = "normalized still face scale",
			ZEstimateKind = "relative still scale",
			ZQualityLabel = "usable",
			StatusLine = "Standard Model still geometry captured."
		};
	}
}
