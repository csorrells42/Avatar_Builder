using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed class PersonIdentityRecord
{
	public string Id { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string AvatarProfileId { get; set; } = "";

	public DateTime FirstSeenAtUtc { get; set; }

	public DateTime LastSeenAtUtc { get; set; }

	public int ObservationCount { get; set; }

	public int EncounterCount { get; set; }

	public List<float[]> Prototypes { get; set; } = [];
}
