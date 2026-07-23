using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public static class MediaPipeNormalizedFaceStore
{
	public const string FolderName = "MediaPipeGeometry";

	public const string StateFileName = "reconstruction-state.json";

	public const string ModelFileName = "normalized-face-model.json";

	public const string ViewerFileName = "normalized-face-viewer.html";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	public static string GetFolder(string profileFolder)
	{
		return Path.Combine(profileFolder, "MediaPipeGeometry");
	}

	public static string GetStatePath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "reconstruction-state.json");
	}

	public static string GetModelPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "normalized-face-model.json");
	}

	public static string GetViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "normalized-face-viewer.html");
	}

	public static MediaPipeNormalizedFaceState? ReadState(string profileFolder)
	{
		string statePath = GetStatePath(profileFolder);
		if (!File.Exists(statePath))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<MediaPipeNormalizedFaceState>(File.ReadAllText(statePath), JsonOptions);
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

	public static void Write(string profileFolder, MediaPipeNormalizedFaceState state, MediaPipeNormalizedFaceModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetStatePath(profileFolder), JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetModelPath(profileFolder), JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetViewerPath(profileFolder), MediaPipeNormalizedFaceViewerPage.Build(model), Encoding.UTF8);
	}

	public static void Delete(string profileFolder)
	{
		string folder = GetFolder(profileFolder);
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
