using System;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraRegistrationDiagnostics(DateTime CameraACapturedAtUtc, DateTime CameraBCapturedAtUtc, TimeSpan PairSkew, double RootMeanSquareResidualPercent, int CameraAOwnedPointCount, int CameraBOwnedPointCount, double? PhysicalBaselineInches = null, int TriangulatedPointCount = 0, double CameraATrackingConfidence = 0.0, double CameraBTrackingConfidence = 0.0, DualCameraDenseStereoSource? DenseStereoSource = null)
{
	public string ToStatusText()
	{
		double? physicalBaselineInches = PhysicalBaselineInches;
		string value = ((physicalBaselineInches.HasValue && physicalBaselineInches.GetValueOrDefault() > 0.0) ? $" | calibrated stereo {PhysicalBaselineInches:0.00} in baseline | triangulated {TriangulatedPointCount}" : " | physical rig not calibrated");
		return $"Translation live | pair {PairSkew.TotalMilliseconds:0} ms | projected RMS {RootMeanSquareResidualPercent:0.00}% face width | direct-view ownership A {CameraAOwnedPointCount} / B {CameraBOwnedPointCount}{value}";
	}
}
