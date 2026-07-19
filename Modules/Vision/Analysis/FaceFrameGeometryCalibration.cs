namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceFrameGeometryCalibration
{
    public static FaceFrameGeometryCalibration None { get; } = new();

    public double? ReferenceDistanceInches { get; init; }

    public double? ReferenceInterEyeFrameWidth { get; init; }

    public int ReferenceSampleCount { get; init; }

    public string ReferenceSource { get; init; } = "";

    public double? CameraHorizontalFovDegrees { get; init; }

    public double? InterpupillaryDistanceInches { get; init; }

    public bool HasDistanceReference =>
        ReferenceDistanceInches is > 0d
        && ReferenceInterEyeFrameWidth is > 0d;

    public bool HasApparentReference =>
        ReferenceInterEyeFrameWidth is > 0d;

    public bool HasCameraIntrinsics =>
        CameraHorizontalFovDegrees is > 0d and < 180d
        && InterpupillaryDistanceInches is > 0d;
}
