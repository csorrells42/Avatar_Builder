namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewPoseBucket
{
    public string BucketId { get; set; } = "";

    public string Label { get; set; } = "";

    public string Description { get; set; } = "";

    public string CaptureInstruction { get; set; } = "";

    public bool PrimaryNeutralReference { get; set; }

    public bool RequiredForAvatarCoverage { get; set; }

    public int SampleCount { get; set; }

    public double TotalWeight { get; set; }

    public double CoveragePercent { get; set; }

    public double HeadYawDegrees { get; set; }

    public double HeadPitchDegrees { get; set; }

    public double HeadRollDegrees { get; set; }

    public double AverageFaceReliabilityPercent { get; set; }

    public double AverageEyeReliabilityPercent { get; set; }

    public double AverageMouthReliabilityPercent { get; set; }

    public string Status { get; set; } = "waiting";
}
