using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraCalibrationModel
{
	public int Version { get; init; } = 1;

	public DateTime CalibratedAtUtc { get; init; }

	public required string CameraAName { get; init; }

	public required string CameraBName { get; init; }

	public int ImageWidth { get; init; }

	public int ImageHeight { get; init; }

	public int AcceptedPairCount { get; init; }

	public double CameraAReprojectionErrorPixels { get; init; }

	public double CameraBReprojectionErrorPixels { get; init; }

	public double StereoReprojectionErrorPixels { get; init; }

	public required double[] CameraAMatrix { get; init; }

	public required double[] CameraADistortion { get; init; }

	public required double[] CameraBMatrix { get; init; }

	public required double[] CameraBDistortion { get; init; }

	public required double[] CameraAToBRotation { get; init; }

	public required double[] CameraAToBTranslationInches { get; init; }

	public required double[] EssentialMatrix { get; init; }

	public required double[] FundamentalMatrix { get; init; }

	public double BaselineInches
	{
		get
		{
			if (CameraAToBTranslationInches.Length >= 3)
			{
				return Math.Sqrt(CameraAToBTranslationInches[0] * CameraAToBTranslationInches[0] + CameraAToBTranslationInches[1] * CameraAToBTranslationInches[1] + CameraAToBTranslationInches[2] * CameraAToBTranslationInches[2]);
			}
			return 0.0;
		}
	}

	public string ReconstructionId
	{
		get
		{
			InlineArray5<string> buffer = default(InlineArray5<string>);
			buffer[0] = Version.ToString(CultureInfo.InvariantCulture);
			buffer[1] = CalibratedAtUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
			buffer[2] = CameraAName;
			buffer[3] = CameraBName;
			buffer[4] = $"{ImageWidth}x{ImageHeight}";
			return string.Join('|', (ReadOnlySpan<string>)buffer);
		}
	}

	public bool IsUsable
	{
		get
		{
			if (Version == 1 && ImageWidth > 0 && ImageHeight > 0 && AcceptedPairCount >= 10 && CameraAMatrix.Length == 9 && CameraBMatrix.Length == 9 && CameraAToBRotation.Length == 9 && CameraAToBTranslationInches.Length == 3 && double.IsFinite(CameraAReprojectionErrorPixels) && double.IsFinite(CameraBReprojectionErrorPixels) && double.IsFinite(StereoReprojectionErrorPixels) && StereoReprojectionErrorPixels <= 5.0)
			{
				return BaselineInches > 0.1;
			}
			return false;
		}
	}

	public const int CurrentVersion = 1;

	public const int BoardColumns = 9;

	public const int BoardRows = 6;

	public const double SquareSizeInches = 0.75;

	public const double MaximumStereoReprojectionErrorPixels = 5.0;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static string GetFolder(string outputRoot)
	{
		return Path.Combine(outputRoot, "Calibration", "DualCamera");
	}

	public static string GetPath(string outputRoot)
	{
		return Path.Combine(GetFolder(outputRoot), "dual-camera-calibration.json");
	}

	public static void Save(string outputRoot, DualCameraCalibrationModel calibration)
	{
		string path = GetPath(outputRoot);
		Directory.CreateDirectory(GetFolder(outputRoot));
		string text = path + ".tmp";
		File.WriteAllText(text, JsonSerializer.Serialize(calibration, JsonOptions));
		File.Move(text, path, overwrite: true);
	}

	public static DualCameraCalibrationModel? Load(string outputRoot)
	{
		try
		{
			string path = GetPath(outputRoot);
			if (!File.Exists(path))
			{
				return null;
			}
			DualCameraCalibrationModel? dualCameraCalibrationModel = JsonSerializer.Deserialize<DualCameraCalibrationModel>(File.ReadAllText(path), JsonOptions);
			return (dualCameraCalibrationModel is not null && dualCameraCalibrationModel.IsUsable) ? dualCameraCalibrationModel : null;
		}
		catch
		{
			return null;
		}
	}

	[CompilerGenerated]
	[SetsRequiredMembers]
	private DualCameraCalibrationModel(DualCameraCalibrationModel original)
	{
		Version = original.Version;
		CalibratedAtUtc = original.CalibratedAtUtc;
		CameraAName = original.CameraAName;
		CameraBName = original.CameraBName;
		ImageWidth = original.ImageWidth;
		ImageHeight = original.ImageHeight;
		AcceptedPairCount = original.AcceptedPairCount;
		CameraAReprojectionErrorPixels = original.CameraAReprojectionErrorPixels;
		CameraBReprojectionErrorPixels = original.CameraBReprojectionErrorPixels;
		StereoReprojectionErrorPixels = original.StereoReprojectionErrorPixels;
		CameraAMatrix = original.CameraAMatrix;
		CameraADistortion = original.CameraADistortion;
		CameraBMatrix = original.CameraBMatrix;
		CameraBDistortion = original.CameraBDistortion;
		CameraAToBRotation = original.CameraAToBRotation;
		CameraAToBTranslationInches = original.CameraAToBTranslationInches;
		EssentialMatrix = original.EssentialMatrix;
		FundamentalMatrix = original.FundamentalMatrix;
	}
}
