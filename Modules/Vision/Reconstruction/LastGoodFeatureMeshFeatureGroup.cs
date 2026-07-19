namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodFeatureMeshFeatureGroup
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public string Role { get; init; } = "";

    public bool Closed { get; init; }

    public double ConfidencePercent { get; init; }

    public List<int> LandmarkIndices { get; init; } = [];
}
