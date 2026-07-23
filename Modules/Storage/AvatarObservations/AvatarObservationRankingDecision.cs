namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record AvatarObservationRankingDecision(bool Accepted, AvatarObservation? Candidate, AvatarObservation? Replacement, string Reason)
{
	public static AvatarObservationRankingDecision Accept(AvatarObservation candidate, AvatarObservation? replacement, string reason)
	{
		return new AvatarObservationRankingDecision(Accepted: true, candidate, replacement, reason);
	}

	public static AvatarObservationRankingDecision Reject(string reason)
	{
		return new AvatarObservationRankingDecision(Accepted: false, null, null, reason);
	}
}
