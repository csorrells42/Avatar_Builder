using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Storage.AvatarObservations;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed class ManualStandardModelCaptureService
{
	private readonly AvatarObservationRepository _repository;

	private readonly AvatarStandardModelStore _modelStore;

	public ManualStandardModelCaptureService(AvatarObservationRepository repository, AvatarStandardModelStore modelStore)
	{
		_repository = repository ?? throw new ArgumentNullException("repository");
		_modelStore = modelStore ?? throw new ArgumentNullException("modelStore");
	}

	public ManualStandardModelCaptureResult Save(string profileFolder, string subjectId, string subjectDisplayName, AvatarReconstructionSnapshot liveSnapshot, BitmapSource sourceFrame, AvatarCaptureQualityAssessment captureQuality, FaceFrameGeometry faceGeometry)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentException.ThrowIfNullOrWhiteSpace(subjectId, "subjectId");
		ArgumentNullException.ThrowIfNull(liveSnapshot, "liveSnapshot");
		ArgumentNullException.ThrowIfNull(sourceFrame, "sourceFrame");
		if (!string.Equals(liveSnapshot.BackendId, "deca-flame-recurrent-v4", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Manual Standard Model capture requires a live DECA/FLAME reconstruction.");
		}
		if (liveSnapshot.CurrentModelShapeCoefficients.Count != 100 || liveSnapshot.CanonicalIdentityVertices.Count < 1000)
		{
			throw new InvalidOperationException("The current DECA/FLAME identity is structurally incomplete; wait for a complete recurrent result.");
		}
		if (liveSnapshot.ObservedLandmarks.Count != 478 || liveSnapshot.ObservedLandmarks.Select((FaceMeshLandmarkPoint point) => point.Index).Distinct().Count() != 478)
		{
			throw new InvalidOperationException("The current frame does not contain the complete 478-point MediaPipe measurement set.");
		}
		AvatarReconstructionSnapshot avatarReconstructionSnapshot = CreateHumanAcceptedCheckpoint(liveSnapshot);
		AvatarObservationWriteResult avatarObservationWriteResult = _repository.SaveCapture(new AvatarObservationCapture(profileFolder, subjectId, subjectDisplayName, avatarReconstructionSnapshot, sourceFrame, captureQuality, faceGeometry));
		if (!avatarObservationWriteResult.Accepted)
		{
			return new ManualStandardModelCaptureResult(avatarObservationWriteResult, null, null);
		}
		AvatarStandardPoseSample avatarStandardPoseSample = AvatarStandardPoseGrid.CreateSample(avatarObservationWriteResult.ObservationId, avatarReconstructionSnapshot.CapturedAtUtc, avatarReconstructionSnapshot.ARotationAroundXDegrees, avatarReconstructionSnapshot.BRotationAroundYDegrees, avatarReconstructionSnapshot.CRotationAroundZDegrees, avatarReconstructionSnapshot.ReconstructionConfidencePercent, avatarReconstructionSnapshot.CurrentModelCoefficientDeltaRms, avatarReconstructionSnapshot.SourceFrameWidthPixels, avatarReconstructionSnapshot.SourceFrameHeightPixels, avatarReconstructionSnapshot.ObservedLandmarks, avatarReconstructionSnapshot.CurrentModelShapeCoefficients, avatarReconstructionSnapshot.CanonicalIdentityVertices);
		AvatarStandardModel avatarStandardModel = _modelStore.Read(profileFolder);
		Dictionary<string, AvatarStandardPoseSample> dictionary = avatarStandardModel?.PoseAtlas.ToDictionary<KeyValuePair<string, AvatarStandardPoseSample>, string, AvatarStandardPoseSample>((KeyValuePair<string, AvatarStandardPoseSample> pair) => pair.Key, (KeyValuePair<string, AvatarStandardPoseSample> pair) => pair.Value, StringComparer.Ordinal) ?? new Dictionary<string, AvatarStandardPoseSample>(StringComparer.Ordinal);
		dictionary[avatarStandardPoseSample.DirectionKey] = avatarStandardPoseSample;
		AvatarStandardIdentityFusionResult avatarStandardIdentityFusionResult = AvatarStandardIdentityFusion.Fuse(dictionary, avatarStandardModel?.ShapeCoefficients, avatarStandardModel?.CanonicalIdentityVertices);
		AvatarStandardModel model = new AvatarStandardModel
		{
			SubjectId = subjectId,
			SubjectDisplayName = subjectDisplayName,
			SourceFolder = "human accepted frame",
			UpdatedAtUtc = DateTime.UtcNow,
			SourceImageCount = avatarObservationWriteResult.RetainedCount,
			CompletedImageCount = avatarStandardIdentityFusionResult.PoseEvidenceCount,
			ConvergedImageCount = 0,
			FailedImageCount = 0,
			LastSourceImageName = "Human accepted " + avatarStandardPoseSample.DirectionKey,
			ModelSequenceNumber = avatarReconstructionSnapshot.CurrentModelSequenceNumber,
			CoefficientDeltaRms = avatarReconstructionSnapshot.CurrentModelCoefficientDeltaRms,
			LastStillConverged = false,
			LastStillPassCount = 0,
			LastMeasuredFitPercent = avatarReconstructionSnapshot.ReconstructionConfidencePercent,
			IdentityEvidencePoseCount = avatarStandardIdentityFusionResult.PoseEvidenceCount,
			UsesLegacyIdentityAnchor = avatarStandardIdentityFusionResult.UsesLegacyAnchor,
			ShapeCoefficients = avatarStandardIdentityFusionResult.ShapeCoefficients,
			CanonicalIdentityVertices = avatarStandardIdentityFusionResult.CanonicalIdentityVertices,
			TopologyEdges = avatarReconstructionSnapshot.TopologyEdges.ToList(),
			PoseAtlas = dictionary
		};
		_modelStore.Write(profileFolder, model);
		return new ManualStandardModelCaptureResult(avatarObservationWriteResult, model, avatarStandardPoseSample);
	}

	private static AvatarReconstructionSnapshot CreateHumanAcceptedCheckpoint(AvatarReconstructionSnapshot source)
	{
		return new AvatarReconstructionSnapshot
		{
			BackendId = "deca-flame-standard-model-checkpoint-v1",
			RequestId = $"manual-standard-{Guid.NewGuid():N}",
			CapturedAtUtc = source.CapturedAtUtc,
			Source = source.Source + " | human accepted current frame",
			CoordinateSpace = source.CoordinateSpace,
			DenseVertexCount = source.DenseVertexCount,
			DenseSampleStride = source.DenseSampleStride,
			ReconstructionConfidencePercent = source.ReconstructionConfidencePercent,
			ARotationAroundXDegrees = source.ARotationAroundXDegrees,
			BRotationAroundYDegrees = source.BRotationAroundYDegrees,
			CRotationAroundZDegrees = source.CRotationAroundZDegrees,
			PoseSource = source.PoseSource,
			TrustDecision = "The logged-in user accepted this exact DECA/FLAME model and paired source frame with NumPad 0.",
			SourceImageUri = source.SourceImageUri,
			Vertices = source.Vertices.ToList(),
			CanonicalIdentityVertices = source.CanonicalIdentityVertices.ToList(),
			AlignedIdentityVertices = source.AlignedIdentityVertices.ToList(),
			CurrentModelShapeCoefficients = source.CurrentModelShapeCoefficients.ToList(),
			CurrentModelSequenceNumber = source.CurrentModelSequenceNumber,
			CurrentModelCoefficientDeltaRms = source.CurrentModelCoefficientDeltaRms,
			PinnedStillConverged = false,
			PinnedStillPassCount = 0,
			PinnedStillStablePassCount = 0,
			TopologyEdges = source.TopologyEdges.ToList(),
			SparseLandmarks = source.SparseLandmarks.ToList(),
			CameraMatrixCoefficients = source.CameraMatrixCoefficients.ToList(),
			ShapeCoefficients = source.CurrentModelShapeCoefficients.ToList(),
			ExpressionCoefficients = source.ExpressionCoefficients.ToList(),
			PoseCoefficients = source.PoseCoefficients.ToList(),
			SourceFrameWidthPixels = source.SourceFrameWidthPixels,
			SourceFrameHeightPixels = source.SourceFrameHeightPixels,
			InputFaceBox = CloneFaceBox(source.InputFaceBox),
			ObservedLandmarks = source.ObservedLandmarks.ToList(),
			Warnings = source.Warnings.Append("Human accepted this exact Standard Model and source-frame pair with NumPad 0.").ToList()
		};
	}

	private static ReconstructionInputFaceBox? CloneFaceBox(ReconstructionInputFaceBox? source)
	{
		if (source != null)
		{
			return new ReconstructionInputFaceBox
			{
				Left = source.Left,
				Top = source.Top,
				Right = source.Right,
				Bottom = source.Bottom,
				Normalized = source.Normalized,
				Confidence = source.Confidence
			};
		}
		return null;
	}
}
