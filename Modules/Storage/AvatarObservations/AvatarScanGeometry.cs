using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record AvatarScanGeometry
{
	public List<FaceMeshLandmarkPoint> Vertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<FaceMeshLandmarkPoint> SparseLandmarks { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<double> CameraMatrixCoefficients { get; init; } = new List<double>();

	public List<double> ShapeCoefficients { get; init; } = new List<double>();

	public List<double> ExpressionCoefficients { get; init; } = new List<double>();

	public List<double> PoseCoefficients { get; init; } = new List<double>();

	public List<FaceMeshLandmarkPoint> ObservedLandmarks { get; init; } = new List<FaceMeshLandmarkPoint>();

	public int SourceFrameWidthPixels { get; init; }

	public int SourceFrameHeightPixels { get; init; }

	public ReconstructionInputFaceBox? InputFaceBox { get; init; }
}
