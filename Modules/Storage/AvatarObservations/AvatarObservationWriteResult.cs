namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record AvatarObservationWriteResult(bool Accepted, bool ReplacedExisting, string ObservationId, string Detail, long Revision, int RetainedCount, AvatarObservation? AcceptedObservation = null, AvatarObservation? ReplacedObservation = null);
