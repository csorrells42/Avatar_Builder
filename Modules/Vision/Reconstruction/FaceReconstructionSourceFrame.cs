namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionSourceFrame
{
    public string SourceKind { get; set; } = FaceReconstructionSourceKinds.ExplicitTrainingImage;

    public string SourcePath { get; set; } = "";

    public DateTime? CapturedAtUtc { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? FaceReliabilityPercent { get; set; }

    public double? EyeReliabilityPercent { get; set; }

    public double? MouthReliabilityPercent { get; set; }

    public double? OverallQualityPercent { get; set; }

    public double? HeadYawDegrees { get; set; }

    public double? HeadPitchDegrees { get; set; }

    public double? HeadRollDegrees { get; set; }

    public bool? GlassesVisible { get; set; }

    public string Notes { get; set; } = "";
}
