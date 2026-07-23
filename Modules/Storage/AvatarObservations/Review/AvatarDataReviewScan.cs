using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Storage.AvatarObservations.Review;

public sealed record AvatarDataReviewScan(AvatarDataReviewScanSummary Summary, IReadOnlyList<FaceMeshLandmarkPoint> Vertices, IReadOnlyList<FaceMeshLandmarkPoint> CanonicalIdentityVertices, IReadOnlyList<string> Warnings);
