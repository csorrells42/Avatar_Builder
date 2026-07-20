using System.IO;
using System.Security.Cryptography;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarStorageLayout
{
    public const string DatabaseFileName = "avatar-builder.sqlite3";

    public static string GetStorageRoot(string profileFolder)
    {
        var profile = new DirectoryInfo(Path.GetFullPath(profileFolder));
        var avatarSystem = profile.Parent?.Parent;
        if (avatarSystem is null)
        {
            throw new InvalidOperationException($"Avatar profile folder has no AvatarSystem parent: {profileFolder}");
        }

        return Path.Combine(avatarSystem.FullName, "Storage");
    }

    public static string GetDatabasePath(string storageRoot) => Path.Combine(storageRoot, DatabaseFileName);

    public static string GetScanObjectFolder(string storageRoot) => Path.Combine(storageRoot, "Objects", "Scans");

    public static string GetImageObjectFolder(string storageRoot) => Path.Combine(storageRoot, "Objects", "Images");

    public static string GetTopologyObjectFolder(string storageRoot) => Path.Combine(storageRoot, "Objects", "Topology");

    public static string ToRelativePath(string storageRoot, string fullPath)
    {
        return Path.GetRelativePath(storageRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string ResolveObjectPath(string storageRoot, string relativePath)
    {
        var root = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(storageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Storage object escaped the configured root: {relativePath}");
        }

        return path;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string PromoteContentAddressedObject(string temporaryPath, string objectFolder, string extension)
    {
        Directory.CreateDirectory(objectFolder);
        var hash = ComputeSha256(temporaryPath);
        var shard = Path.Combine(objectFolder, hash[..2]);
        Directory.CreateDirectory(shard);
        var destination = Path.Combine(shard, hash + extension);
        try
        {
            File.Move(temporaryPath, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            File.Delete(temporaryPath);
        }

        return destination;
    }
}
