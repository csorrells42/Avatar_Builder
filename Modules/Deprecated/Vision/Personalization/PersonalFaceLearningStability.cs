namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceLearningStability
{
    public string Policy { get; set; } = "slow weighted measurement averaging";

    public double AcceptedSampleWeight { get; set; }

    public double MinimumTrackedDistributionWeight { get; set; }

    public double AnchorTargetWeight { get; set; }

    public double AnchorPercent { get; set; }

    public string AnchorStatus { get; set; } = "waiting";

    public double BaseExponentialMovingAverageAlphaPercent { get; set; }

    public double MaximumStableSampleWeight { get; set; }

    public double EventLikeSampleWeightMultiplier { get; set; }

    public double MaximumNextSampleInfluencePercent { get; set; }

    public double MaximumEventLikeNextSampleInfluencePercent { get; set; }

    public string Guidance { get; set; } =
        "Treat new measurements as incremental evidence. Consumers should prefer the weighted average and normal range for identity/shape, and use the exponential moving average only as a slow drift indicator.";
}
