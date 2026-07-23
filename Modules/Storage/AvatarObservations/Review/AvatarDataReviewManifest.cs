using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Storage.AvatarObservations.Review;

public sealed record AvatarDataReviewManifest(string SubjectId, string SubjectDisplayName, long Revision, DateTime UpdatedAtUtc, int StoredScanCount, string BackendId, bool IsStandardModelCheckpointReview, IReadOnlyList<AvatarDataReviewScanSummary> Scans);
