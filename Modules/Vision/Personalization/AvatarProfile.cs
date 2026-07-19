namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarProfile
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string DataFolderName { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSelectedAtUtc { get; set; }

    public bool HasDataFolder => !string.IsNullOrWhiteSpace(DataFolderName);
}

public sealed class AvatarProfileRegistry
{
    public int Version { get; set; } = 1;

    public string SelectedProfileId { get; set; } = "";

    public List<AvatarProfile> Profiles { get; set; } = [];
}
