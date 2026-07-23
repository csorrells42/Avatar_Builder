using System;

namespace AvatarBuilder.Modules.Storage.AvatarObservations.Review;

public sealed record AvatarDataReviewScanSummary(string ObservationId, DateTime CapturedAtUtc, string Source, int DenseVertexCount, double ReconstructionConfidencePercent, long ModelSequenceNumber, double ModelCoefficientDeltaRms, double SampleQualityPercent, double EyeQualityPercent, double MouthQualityPercent, double BrowQualityPercent, double StabilityQualityPercent, double ARotationAroundXDegrees, double BRotationAroundYDegrees, double CRotationAroundZDegrees, string PoseBucket, string TrustDecision, string TopologySha256, bool HasSourceImage);
