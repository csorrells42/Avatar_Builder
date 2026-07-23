using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public static class MediaPipeStereoFaceStore
{
	public const string FolderName = "StereoFaceGeometry";

	public const string StateFileName = "stereo-face-state.json";

	public const string ModelFileName = "stereo-face-model.json";

	public const string ViewerFileName = "stereo-face-viewer.html";

	public const string RawViewerFileName = "stereo-face-raw-evidence-viewer.html";

	public const string ProbabilityModelFileName = "stereo-probability-face-model.json";

	public const string ProbabilityViewerFileName = "stereo-probability-face-viewer.html";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	public static string GetFolder(string profileFolder)
	{
		return Path.Combine(profileFolder, "StereoFaceGeometry");
	}

	public static string GetStatePath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-face-state.json");
	}

	public static string GetModelPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-face-model.json");
	}

	public static string GetViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-face-viewer.html");
	}

	public static string GetRawViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-face-raw-evidence-viewer.html");
	}

	public static string GetProbabilityModelPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-probability-face-model.json");
	}

	public static string GetProbabilityViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "stereo-probability-face-viewer.html");
	}

	public static MediaPipeStereoFaceState? ReadState(string profileFolder)
	{
		string statePath = GetStatePath(profileFolder);
		if (!File.Exists(statePath))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<MediaPipeStereoFaceState>(File.ReadAllText(statePath), JsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	public static void Write(string profileFolder, MediaPipeStereoFaceState state, MediaPipeStereoFaceModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetStatePath(profileFolder), JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetModelPath(profileFolder), JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetViewerPath(profileFolder), MediaPipeStereoFaceViewerPage.Build(model), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetRawViewerPath(profileFolder), MediaPipeStereoRawEvidenceViewerPage.Build(state), Encoding.UTF8);
	}

	public static void WriteProbabilityFace(string profileFolder, MediaPipeStereoProbabilityFaceModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetProbabilityModelPath(profileFolder), JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetProbabilityViewerPath(profileFolder), MediaPipeStereoProbabilityFaceViewerPage.Build(model), Encoding.UTF8);
	}
}
