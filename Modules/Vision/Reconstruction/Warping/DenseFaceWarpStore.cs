using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public static class DenseFaceWarpStore
{
	public const string FolderName = "DenseWarp";

	public const string ResultFileName = "three-ddfa-mediapipe-warp.json";

	public const string ViewerFileName = "three-ddfa-mediapipe-warp-viewer.html";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	public static string GetFolder(string profileFolder)
	{
		return Path.Combine(profileFolder, "DenseWarp");
	}

	public static string GetResultPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "three-ddfa-mediapipe-warp.json");
	}

	public static string GetViewerPath(string profileFolder)
	{
		return Path.Combine(GetFolder(profileFolder), "three-ddfa-mediapipe-warp-viewer.html");
	}

	public static DenseFaceWarpSaveResult Write(string profileFolder, DenseFaceWarpResult result)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(result, "result");
		if (!result.HasGeometry)
		{
			throw new InvalidOperationException(result.Status);
		}
		string text = JsonSerializer.Serialize(DenseFaceWarpDocument.Create(result), JsonOptions);
		Directory.CreateDirectory(GetFolder(profileFolder));
		AtomicTextFileWriter.WriteAllText(GetResultPath(profileFolder), text, Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetViewerPath(profileFolder), DenseFaceWarpViewerPage.Build(text), Encoding.UTF8);
		return new DenseFaceWarpSaveResult(GetResultPath(profileFolder), GetViewerPath(profileFolder));
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
