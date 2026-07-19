namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodFeatureMeshStabilityReport
{
    public int SampleCount { get; set; }

    public int HeadLockedSampleCount { get; set; }

    public int ComparedFeatureCount { get; set; }

    public double HealthPercent { get; set; }

    public double WorstFeatureDriftPercent { get; set; }

    public string Status { get; set; } = "waiting for head-locked samples";

    public int YawLeftSampleCount { get; set; }

    public int YawRightSampleCount { get; set; }

    public double YawRangeDegrees { get; set; }

    public double YawHealthPercent { get; set; }

    public int YawComparedFeatureCount { get; set; }

    public double YawWorstFeatureDriftPercent { get; set; }

    public string YawStatus { get; set; } = "waiting for left/right head turns";

    public int ANegativeSampleCount { get; set; }

    public int APositiveSampleCount { get; set; }

    public double ARangeDegrees { get; set; }

    public double AHealthPercent { get; set; }

    public int AComparedFeatureCount { get; set; }

    public double AWorstFeatureDriftPercent { get; set; }

    public string AStatus { get; set; } = "waiting for A-axis tilt samples";

    public int CNegativeSampleCount { get; set; }

    public int CPositiveSampleCount { get; set; }

    public double CRangeDegrees { get; set; }

    public double CHealthPercent { get; set; }

    public int CComparedFeatureCount { get; set; }

    public double CWorstFeatureDriftPercent { get; set; }

    public string CStatus { get; set; } = "waiting for C-axis tilt samples";

    public int ZCloseSampleCount { get; set; }

    public int ZFarSampleCount { get; set; }

    public double ZFaceScaleRangePercent { get; set; }

    public double ZHealthPercent { get; set; }

    public int ZComparedFeatureCount { get; set; }

    public double ZWorstFeatureDriftPercent { get; set; }

    public string ZStatus { get; set; } = "waiting for Z distance-change samples";

    public List<LastGoodFeatureMeshFeatureStability> Features { get; set; } = [];

    public List<string> Findings { get; set; } = [];

    public List<string> YawFindings { get; set; } = [];

    public List<string> AFindings { get; set; } = [];

    public List<string> CFindings { get; set; } = [];

    public List<string> ZFindings { get; set; } = [];
}
