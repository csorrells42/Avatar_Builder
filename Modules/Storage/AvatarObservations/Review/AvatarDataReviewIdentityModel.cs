using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations.Review;

public sealed record AvatarDataReviewIdentityModel(bool HasMappedIdentity, string Status, DateTime? UpdatedAtUtc, int FrameCount, int IterationCount, double InitialLandmarkRmsePercent, double FinalLandmarkRmsePercent, double ImprovementPercent, double GenericIdentityDisplacementPercent, IReadOnlyList<FaceMeshLandmarkPoint> Vertices, IReadOnlyList<MeshTopologyEdge> TopologyEdges);
