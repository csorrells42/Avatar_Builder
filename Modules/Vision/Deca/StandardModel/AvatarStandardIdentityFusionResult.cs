using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed record AvatarStandardIdentityFusionResult(IReadOnlyList<double> ShapeCoefficients, IReadOnlyList<FaceMeshLandmarkPoint> CanonicalIdentityVertices, int PoseEvidenceCount, int TotalEvidenceCount, bool UsesLegacyAnchor);
