using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed class AvatarStandardModelStore
{
	public const string FileName = "avatar_standard_model.json";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public string Write(string profileFolder, AvatarStandardModel model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentNullException.ThrowIfNull(model, "model");
		Directory.CreateDirectory(profileFolder);
		string path = GetPath(profileFolder);
		AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
		return path;
	}

	public AvatarStandardModel? Read(string profileFolder)
	{
		string path = GetPath(profileFolder);
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			AvatarStandardModel avatarStandardModel = JsonSerializer.Deserialize<AvatarStandardModel>(File.ReadAllText(path), JsonOptions);
			return string.Equals(avatarStandardModel?.SchemaVersion, "avatar-standard-model-v1", StringComparison.Ordinal) ? avatarStandardModel : null;
		}
		catch
		{
			return null;
		}
	}

	public void Delete(string profileFolder)
	{
		string path = GetPath(profileFolder);
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	public static string GetPath(string profileFolder)
	{
		return Path.Combine(profileFolder, "avatar_standard_model.json");
	}
}
