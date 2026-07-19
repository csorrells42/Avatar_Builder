namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarTrainingArtifact
{
    public string Name { get; set; } = "";

    public string FileName { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Description { get; set; } = "";

    public bool ContainsRawPixels { get; set; }

    public bool ContainsRawContinuousVideo { get; set; }
}
