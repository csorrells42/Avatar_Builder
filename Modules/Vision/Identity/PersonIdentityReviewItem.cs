using System;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed record PersonIdentityReviewItem(
	string IdentityId,
	string DisplayName,
	string ContextPhotoPath,
	bool IsRegisteredUser,
	string PermissionLevel,
	string AvatarProfileId,
	DateTime FirstSeenAtUtc,
	DateTime LastSeenAtUtc,
	int ObservationCount,
	int EncounterCount);
