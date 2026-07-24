using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarProfileStore
{
	public const string RootFolderName = "AvatarSystem";

	public const string PeopleFolderName = "People";

	public const string RegistryFileName = "avatar_profiles.json";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public AvatarProfileRegistry Load(string outputFolder)
	{
		string registryPath = GetRegistryPath(outputFolder);
		AvatarProfileRegistry? avatarProfileRegistry = null;
		try
		{
			if (File.Exists(registryPath))
			{
				avatarProfileRegistry = JsonSerializer.Deserialize<AvatarProfileRegistry>(File.ReadAllText(registryPath, Encoding.UTF8), JsonOptions);
			}
		}
		catch
		{
			avatarProfileRegistry = null;
		}
		if (avatarProfileRegistry == null)
		{
			avatarProfileRegistry = new AvatarProfileRegistry();
		}
		NormalizeRegistry(avatarProfileRegistry);
		return avatarProfileRegistry;
	}

	public string Save(string outputFolder, AvatarProfileRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(registry, "registry");
		NormalizeRegistry(registry);
		string registryPath = GetRegistryPath(outputFolder);
		Directory.CreateDirectory(Path.GetDirectoryName(registryPath) ?? GetRootFolder(outputFolder));
		AtomicTextFileWriter.WriteAllText(registryPath, JsonSerializer.Serialize(registry, JsonOptions), Encoding.UTF8);
		return registryPath;
	}

	public AvatarProfile AddOrUpdateProfile(string outputFolder, AvatarProfileRegistry registry, string displayName)
	{
		ArgumentNullException.ThrowIfNull(registry, "registry");
		displayName = CleanDisplayName(displayName);
		NormalizeRegistry(registry);
		AvatarProfile? avatarProfile = registry.Profiles.FirstOrDefault((AvatarProfile profile) => string.Equals(profile.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
		DateTime utcNow = DateTime.UtcNow;
		if (avatarProfile != null)
		{
			avatarProfile.DisplayName = displayName;
			avatarProfile.UpdatedAtUtc = utcNow;
			avatarProfile.LastSelectedAtUtc = utcNow;
			registry.SelectedProfileId = avatarProfile.Id;
			Save(outputFolder, registry);
			return avatarProfile;
		}
		string text = CreateUniqueId(displayName, registry.Profiles);
		AvatarProfile avatarProfile2 = new AvatarProfile
		{
			Id = text,
			DisplayName = displayName,
			DataFolderName = text,
			CreatedAtUtc = utcNow,
			UpdatedAtUtc = utcNow,
			LastSelectedAtUtc = utcNow
		};
		registry.Profiles.Add(avatarProfile2);
		registry.SelectedProfileId = avatarProfile2.Id;
		Directory.CreateDirectory(GetProfileFolder(outputFolder, avatarProfile2));
		Save(outputFolder, registry);
		return avatarProfile2;
	}

	public AvatarProfile SelectProfile(string outputFolder, AvatarProfileRegistry registry, string profileId)
	{
		ArgumentNullException.ThrowIfNull(registry, "registry");
		NormalizeRegistry(registry);
		AvatarProfile? avatarProfile = registry.Profiles.FirstOrDefault((AvatarProfile item) => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
		if (avatarProfile == null)
		{
			avatarProfile = registry.Profiles.FirstOrDefault() ?? AddOrUpdateProfile(outputFolder, registry, "Primary subject");
		}
		avatarProfile.LastSelectedAtUtc = DateTime.UtcNow;
		avatarProfile.UpdatedAtUtc = ((avatarProfile.UpdatedAtUtc == default(DateTime)) ? DateTime.UtcNow : avatarProfile.UpdatedAtUtc);
		registry.SelectedProfileId = avatarProfile.Id;
		Directory.CreateDirectory(GetProfileFolder(outputFolder, avatarProfile));
		Save(outputFolder, registry);
		return avatarProfile;
	}

	public string GetRootFolder(string outputFolder)
	{
		return Path.Combine(outputFolder, "AvatarSystem");
	}

	public string GetProfileFolder(string outputFolder, AvatarProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile, "profile");
		return Path.Combine(GetRootFolder(outputFolder), "People", profile.DataFolderName);
	}

	public string GetRegistryPath(string outputFolder)
	{
		return Path.Combine(GetRootFolder(outputFolder), "avatar_profiles.json");
	}

	private static void NormalizeRegistry(AvatarProfileRegistry registry)
	{
		registry.Version = ((registry.Version <= 0) ? 1 : registry.Version);
		registry.Profiles = registry.Profiles.Where((AvatarProfile profile) => !string.IsNullOrWhiteSpace(profile.DisplayName)).GroupBy<AvatarProfile, string>((AvatarProfile profile) => (!string.IsNullOrWhiteSpace(profile.Id)) ? CleanProfileId(profile.Id) : CreateId(profile.DisplayName), StringComparer.OrdinalIgnoreCase).Select(delegate(IGrouping<string, AvatarProfile> group)
		{
			AvatarProfile avatarProfile = group.First();
			avatarProfile.Id = CleanProfileId(avatarProfile.Id);
			if (string.IsNullOrWhiteSpace(avatarProfile.Id))
			{
				avatarProfile.Id = CreateId(avatarProfile.DisplayName);
			}
			avatarProfile.DisplayName = CleanDisplayName(avatarProfile.DisplayName);
			avatarProfile.DataFolderName = CleanDataFolderName(avatarProfile.DataFolderName);
			if (string.IsNullOrWhiteSpace(avatarProfile.DataFolderName))
			{
				avatarProfile.DataFolderName = avatarProfile.Id;
			}
			avatarProfile.CreatedAtUtc = ((avatarProfile.CreatedAtUtc == default(DateTime)) ? DateTime.UtcNow : avatarProfile.CreatedAtUtc);
			avatarProfile.UpdatedAtUtc = ((avatarProfile.UpdatedAtUtc == default(DateTime)) ? avatarProfile.CreatedAtUtc : avatarProfile.UpdatedAtUtc);
			return avatarProfile;
		})
			.OrderBy<AvatarProfile, string>((AvatarProfile profile) => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (!registry.Profiles.Any((AvatarProfile profile) => string.Equals(profile.Id, registry.SelectedProfileId, StringComparison.OrdinalIgnoreCase)))
		{
			registry.SelectedProfileId = registry.Profiles.OrderByDescending((AvatarProfile profile) => profile.LastSelectedAtUtc ?? DateTime.MinValue).ThenBy<AvatarProfile, string>((AvatarProfile profile) => profile.DisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Id ?? "";
		}
	}

	private static string CreateUniqueId(string displayName, IReadOnlyList<AvatarProfile> profiles)
	{
		string id;
		string value = (id = CreateId(displayName));
		int num = 2;
		while (profiles.Any((AvatarProfile profile) => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
		{
			id = $"{value}-{num}";
			num++;
		}
		return id;
	}

	private static string CreateId(string displayName)
	{
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		string text = displayName.Trim().ToLowerInvariant();
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				stringBuilder.Append(c);
				flag = false;
			}
			else if (!flag)
			{
				stringBuilder.Append('-');
				flag = true;
			}
		}
		string text2 = stringBuilder.ToString().Trim('-');
		if (!string.IsNullOrWhiteSpace(text2))
		{
			return text2;
		}
		return "profile";
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
		value = (string.IsNullOrWhiteSpace(value) ? "Primary subject" : value.Trim());
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			value = value.Replace(oldChar, ' ');
		}
		return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
	}
}
