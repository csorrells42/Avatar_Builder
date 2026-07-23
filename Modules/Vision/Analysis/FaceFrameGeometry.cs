using System;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceFrameGeometry
{
	public static FaceFrameGeometry None { get; } = new FaceFrameGeometry
	{
		StatusLine = "Face frame: waiting for face lock",
		RotationSource = "none",
		DistanceSource = "none",
		ZEstimateKind = "none",
		ZQualityLabel = "waiting"
	};

	public bool HasFace { get; init; }

	public DateTime CapturedAtUtc { get; init; }

	public double YawDegrees { get; init; }

	public double PitchDegrees { get; init; }

	public double RollDegrees { get; init; }

	public double XHorizontalPercent { get; init; }

	public double YVerticalPercent { get; init; }

	public double? ZApparentDepthUnits => ApparentDistanceUnits;

	public double ARotationAroundXDegrees => PitchDegrees;

	public double BRotationAroundYDegrees => YawDegrees;

	public double CRotationAroundZDegrees => RollDegrees;

	public double? DistanceInches { get; init; }

	public double? ApparentDistanceUnits { get; init; }

	public string ApparentDistanceUnitName { get; init; } = "apparent face units";

	public double? FaceFillWidthPercent { get; init; }

	public double? FaceFillHeightPercent { get; init; }

	public double? RelativeDistanceScale { get; init; }

	public double? ZRelativeToReference => RelativeDistanceScale;

	public double? InterEyeFrameWidthPercent { get; init; }

	public double ConfidencePercent { get; init; }

	public double ZConfidencePercent { get; init; }

	public bool DistanceCalibrated { get; init; }

	public bool ZUsesCameraFov { get; init; }

	public bool ZUsesLearnedReference { get; init; }

	public string ZEstimateKind { get; init; } = "";

	public string ZQualityLabel { get; init; } = "";

	public string RotationSource { get; init; } = "";

	public string DistanceSource { get; set; } = "";

	public string ReferenceScaleSource { get; set; } = "";

	public string ScaleCaveat { get; set; } = "";

	public string StatusLine { get; set; } = "";
}
