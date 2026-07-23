using System;
using System.IO;
using System.Security.Cryptography;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarStorageLayout
{
	public const string DatabaseFileName = "avatar-builder.sqlite3";

	public static string GetStorageRoot(string profileFolder)
	{
		return Path.Combine((new DirectoryInfo(Path.GetFullPath(profileFolder)).Parent?.Parent ?? throw new InvalidOperationException("Avatar profile folder has no AvatarSystem parent: " + profileFolder)).FullName, "Storage");
	}

	public static string GetDatabasePath(string storageRoot)
	{
		return Path.Combine(storageRoot, "avatar-builder.sqlite3");
	}

	public static string GetScanObjectFolder(string storageRoot)
	{
		return Path.Combine(storageRoot, "Objects", "Scans");
	}

	public static string GetImageObjectFolder(string storageRoot)
	{
		return Path.Combine(storageRoot, "Objects", "Images");
	}

	public static string GetTopologyObjectFolder(string storageRoot)
	{
		return Path.Combine(storageRoot, "Objects", "Topology");
	}

	public static string ToRelativePath(string storageRoot, string fullPath)
	{
		return Path.GetRelativePath(storageRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
	}

	public static string ResolveObjectPath(string storageRoot, string relativePath)
	{
		string value = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(Path.Combine(storageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
		if (!fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Storage object escaped the configured root: " + relativePath);
		}
		return fullPath;
	}

	public static string ComputeSha256(string path)
	{
		using FileStream source = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
	}

	public static string PromoteContentAddressedObject(string temporaryPath, string objectFolder, string extension)
	{
		Directory.CreateDirectory(objectFolder);
		string text = ComputeSha256(temporaryPath);
		string text2 = Path.Combine(objectFolder, text.Substring(0, 2));
		Directory.CreateDirectory(text2);
		string text3 = Path.Combine(text2, text + extension);
		try
		{
			File.Move(temporaryPath, text3);
		}
		catch (IOException) when (File.Exists(text3))
		{
			File.Delete(temporaryPath);
		}
		return text3;
	}
}
