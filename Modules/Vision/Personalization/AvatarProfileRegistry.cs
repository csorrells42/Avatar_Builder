using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarProfileRegistry
{
	public int Version { get; set; } = 1;

	public string SelectedProfileId { get; set; } = "";

	public List<AvatarProfile> Profiles { get; set; } = new List<AvatarProfile>();
}
