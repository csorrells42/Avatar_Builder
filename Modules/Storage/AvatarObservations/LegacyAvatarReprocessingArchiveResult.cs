namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record LegacyAvatarReprocessingArchiveResult(int DeletedObservationCount, int ArchivedImageCount, string ArchiveRoot, string ManifestPath, long ArchivedImageBytes);
