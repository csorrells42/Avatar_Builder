namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoImagePair
{
	public required int CameraAWidth { get; init; }

	public required int CameraAHeight { get; init; }

	public required int CameraAStride { get; init; }

	public required byte[] CameraABgraPixels { get; init; }

	public required int CameraBWidth { get; init; }

	public required int CameraBHeight { get; init; }

	public required int CameraBStride { get; init; }

	public required byte[] CameraBBgraPixels { get; init; }

	public required MediaPipeStereoImageLandmark[] CameraALandmarks { get; init; }

	public required MediaPipeStereoImageLandmark[] CameraBLandmarks { get; init; }

	public required int CalibrationWidth { get; init; }

	public required int CalibrationHeight { get; init; }

	public required double[] CameraAMatrix { get; init; }

	public required double[] CameraADistortion { get; init; }

	public required double[] CameraBMatrix { get; init; }

	public required double[] CameraBDistortion { get; init; }

	public required double[] CameraAToBRotation { get; init; }

	public required double[] CameraAToBTranslationInches { get; init; }

	public required double[] FundamentalMatrix { get; init; }
}
