using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarProfileStore
{
    public const string RootFolderName = "AvatarSystem";
    public const string PeopleFolderName = "People";
    public const string RegistryFileName = "avatar_profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AvatarProfileRegistry Load(string outputFolder)
    {
        var path = GetRegistryPath(outputFolder);
        AvatarProfileRegistry? registry = null;
        try
        {
            if (File.Exists(path))
            {
                registry = JsonSerializer.Deserialize<AvatarProfileRegistry>(
                    File.ReadAllText(path, Encoding.UTF8),
                    JsonOptions);
            }
        }
        catch
        {
            registry = null;
        }

        registry ??= new AvatarProfileRegistry();
        NormalizeRegistry(registry);
        return registry;
    }

    public string Save(string outputFolder, AvatarProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        NormalizeRegistry(registry);
        var path = GetRegistryPath(outputFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GetRootFolder(outputFolder));
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(registry, JsonOptions), Encoding.UTF8);
        return path;
    }

    public AvatarProfile AddOrUpdateProfile(string outputFolder, AvatarProfileRegistry registry, string displayName)
    {
        ArgumentNullException.ThrowIfNull(registry);

        displayName = CleanDisplayName(displayName);
        NormalizeRegistry(registry);
        var existing = registry.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.DisplayName = displayName;
            existing.UpdatedAtUtc = now;
            existing.LastSelectedAtUtc = now;
            registry.SelectedProfileId = existing.Id;
            Save(outputFolder, registry);
            return existing;
        }

        var id = CreateUniqueId(displayName, registry.Profiles);
        var profile = new AvatarProfile
        {
            Id = id,
            DisplayName = displayName,
            DataFolderName = id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSelectedAtUtc = now
        };
        registry.Profiles.Add(profile);
        registry.SelectedProfileId = profile.Id;
        Directory.CreateDirectory(GetProfileFolder(outputFolder, profile));
        Save(outputFolder, registry);
        return profile;
    }

    public AvatarProfile SelectProfile(string outputFolder, AvatarProfileRegistry registry, string profileId)
    {
        ArgumentNullException.ThrowIfNull(registry);

        NormalizeRegistry(registry);
        var profile = registry.Profiles.FirstOrDefault(item =>
            string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = registry.Profiles.FirstOrDefault()
                ?? AddOrUpdateProfile(outputFolder, registry, "Primary subject");
        }

        profile.LastSelectedAtUtc = DateTime.UtcNow;
        profile.UpdatedAtUtc = profile.UpdatedAtUtc == default ? DateTime.UtcNow : profile.UpdatedAtUtc;
        registry.SelectedProfileId = profile.Id;
        Directory.CreateDirectory(GetProfileFolder(outputFolder, profile));
        Save(outputFolder, registry);
        return profile;
    }

    public string GetRootFolder(string outputFolder)
    {
        return Path.Combine(outputFolder, RootFolderName);
    }

    public string GetProfileFolder(string outputFolder, AvatarProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var root = GetRootFolder(outputFolder);
        return Path.Combine(root, PeopleFolderName, profile.DataFolderName);
    }

    public string GetRegistryPath(string outputFolder)
    {
        return Path.Combine(GetRootFolder(outputFolder), RegistryFileName);
    }

    private static void NormalizeRegistry(AvatarProfileRegistry registry)
    {
        registry.Version = registry.Version <= 0 ? 1 : registry.Version;
        registry.Profiles = registry.Profiles
            .Where(static profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
            .GroupBy(static profile => string.IsNullOrWhiteSpace(profile.Id) ? CreateId(profile.DisplayName) : CleanProfileId(profile.Id), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var profile = group.First();
                profile.Id = CleanProfileId(profile.Id);
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    profile.Id = CreateId(profile.DisplayName);
                }

                profile.DisplayName = CleanDisplayName(profile.DisplayName);
                profile.DataFolderName = CleanDataFolderName(profile.DataFolderName);
                if (string.IsNullOrWhiteSpace(profile.DataFolderName))
                {
                    profile.DataFolderName = profile.Id;
                }
                profile.CreatedAtUtc = profile.CreatedAtUtc == default ? DateTime.UtcNow : profile.CreatedAtUtc;
                profile.UpdatedAtUtc = profile.UpdatedAtUtc == default ? profile.CreatedAtUtc : profile.UpdatedAtUtc;
                return profile;
            })
            .OrderBy(static profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!registry.Profiles.Any(profile => string.Equals(profile.Id, registry.SelectedProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            registry.SelectedProfileId = registry.Profiles
                .OrderByDescending(static profile => profile.LastSelectedAtUtc ?? DateTime.MinValue)
                .ThenBy(static profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.Id ?? "";
        }
    }

    private static string CreateUniqueId(string displayName, IReadOnlyList<AvatarProfile> profiles)
    {
        var baseId = CreateId(displayName);
        var id = baseId;
        for (var suffix = 2; profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            id = $"{baseId}-{suffix}";
        }

        return id;
    }

    private static string CreateId(string displayName)
    {
        var builder = new StringBuilder();
        var lastWasDash = false;
        foreach (var character in displayName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var id = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(id) ? "profile" : id;
    }

    private static string CleanProfileId(string value)
    {
        return CreateId(value);
    }

    private static string CleanDataFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return CreateId(value);
    }

    private static string CleanDisplayName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Primary subject" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, ' ');
        }

        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
