namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed record AvatarStandardModelBuildProgress(int SourceImageCount, int CompletedImageCount, int ConvergedImageCount, int FailedImageCount, string CurrentImageName, string Status, double CoefficientDeltaRms = 0.0, int RecurrentPassCount = 0);
