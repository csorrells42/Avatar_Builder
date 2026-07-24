using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Identity;

public readonly record struct PersonFaceBox(
	double Left,
	double Top,
	double Right,
	double Bottom);

public sealed record PersonIdentityObservation(
	string TrackId,
	string IdentityId,
	string DisplayName,
	string AvatarProfileId,
	bool IsRemembered,
	double Similarity,
	PersonFaceBox FaceBox);

public sealed record PersonIdentitySnapshot(
	DateTime CapturedAtUtc,
	IReadOnlyList<PersonIdentityObservation> People,
	int RememberedIdentityCount,
	string Backend,
	string Status)
{
	public static PersonIdentitySnapshot Waiting { get; } = new(
		DateTime.MinValue,
		Array.Empty<PersonIdentityObservation>(),
		0,
		"",
		"People memory waiting");
}

public sealed record AvatarIdentityAuthorization(
	bool Allowed,
	string PersonIdentityId,
	string ExistingAvatarProfileId,
	string Status);
