using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public static class AvatarStandardPoseGrid
{
	public const double CenterAxisToleranceDegrees = 15.0;

	public static readonly IReadOnlyList<string> DirectionKeys = new global::_003C_003Ez__ReadOnlyArray<string>(new string[9] { "A-/B-", "A-/B0", "A-/B+", "A0/B-", "A0/B0", "A0/B+", "A+/B-", "A+/B0", "A+/B+" });

	public static readonly IReadOnlyList<string> CaptureOrderKeys = new global::_003C_003Ez__ReadOnlyArray<string>(new string[9] { "A-/B-", "A-/B0", "A-/B+", "A0/B+", "A+/B+", "A+/B0", "A+/B-", "A0/B-", "A0/B0" });

	public static AvatarStandardPoseSample CreateSample(string observationId, DateTime capturedAtUtc, double aRotationAroundXDegrees, double bRotationAroundYDegrees, double cRotationAroundZDegrees, double measuredFitPercent, double coefficientDeltaRms, int sourceFrameWidthPixels, int sourceFrameHeightPixels, IReadOnlyList<FaceMeshLandmarkPoint> mediaPipeLandmarks, IReadOnlyList<double> identityShapeCoefficients, IReadOnlyList<FaceMeshLandmarkPoint> canonicalIdentityVertices)
	{
		string directionKey = Classify(aRotationAroundXDegrees, bRotationAroundYDegrees, cRotationAroundZDegrees);
		return new AvatarStandardPoseSample
		{
			DirectionKey = directionKey,
			DisplayName = GetDisplayName(directionKey),
			ObservationId = observationId,
			CapturedAtUtc = capturedAtUtc,
			ARotationAroundXDegrees = aRotationAroundXDegrees,
			BRotationAroundYDegrees = bRotationAroundYDegrees,
			CRotationAroundZDegrees = cRotationAroundZDegrees,
			MeasuredFitPercent = measuredFitPercent,
			CoefficientDeltaRms = coefficientDeltaRms,
			SourceFrameWidthPixels = sourceFrameWidthPixels,
			SourceFrameHeightPixels = sourceFrameHeightPixels,
			MediaPipeLandmarks = (from point in mediaPipeLandmarks
				orderby point.Index
				select new FaceMeshLandmarkPoint
				{
					Index = point.Index,
					X = point.X,
					Y = point.Y,
					Z = point.Z
				}).ToList(),
			IdentityShapeCoefficients = identityShapeCoefficients.ToList(),
			CanonicalIdentityVertices = (from point in canonicalIdentityVertices
				orderby point.Index
				select new FaceMeshLandmarkPoint
				{
					Index = point.Index,
					X = point.X,
					Y = point.Y,
					Z = point.Z
				}).ToList()
		};
	}

	public static string Classify(double aRotationAroundXDegrees, double bRotationAroundYDegrees)
	{
		return Classify(aRotationAroundXDegrees, bRotationAroundYDegrees, 0.0);
	}

	public static string Classify(double aRotationAroundXDegrees, double bRotationAroundYDegrees, double cRotationAroundZDegrees)
	{
		return "A" + ClassifyAxis(aRotationAroundXDegrees) + "/B" + ClassifyAxis(bRotationAroundYDegrees);
	}

	public static bool IsComplete(IReadOnlyCollection<string> acceptedDirectionKeys)
	{
		return DirectionKeys.All(((IEnumerable<string>)acceptedDirectionKeys).Contains<string>);
	}

	public static bool IsComplete(IReadOnlyDictionary<string, AvatarStandardPoseSample> poseAtlas)
	{
		return DirectionKeys.All((string key) => poseAtlas.TryGetValue(key, out AvatarStandardPoseSample value) && IsStructurallyComplete(value) && HasCompleteIdentityEvidence(value));
	}

	public static bool IsStructurallyComplete(AvatarStandardPoseSample sample)
	{
		if (sample.SourceFrameWidthPixels > 0 && sample.SourceFrameHeightPixels > 0 && sample.MediaPipeLandmarks.Count == 478)
		{
			return sample.MediaPipeLandmarks.Select((FaceMeshLandmarkPoint point) => point.Index).Distinct().Order()
				.SequenceEqual(Enumerable.Range(0, 478));
		}
		return false;
	}

	public static bool HasCompleteIdentityEvidence(AvatarStandardPoseSample sample)
	{
		if (sample.IdentityShapeCoefficients.Count == 100 && sample.IdentityShapeCoefficients.All(double.IsFinite) && sample.CanonicalIdentityVertices.Count >= 1000)
		{
			return sample.CanonicalIdentityVertices.All((FaceMeshLandmarkPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z));
		}
		return false;
	}

	public static string? GetNextMissingDirectionKey(IReadOnlyDictionary<string, AvatarStandardPoseSample> poseAtlas)
	{
		return CaptureOrderKeys.FirstOrDefault((string key) => !poseAtlas.TryGetValue(key, out AvatarStandardPoseSample value) || !IsStructurallyComplete(value) || !HasCompleteIdentityEvidence(value));
	}

	public static string GetDisplayName(string directionKey)
	{
		return directionKey switch
		{
			"A-/B-" => "Top Left", 
			"A-/B0" => "Top Middle", 
			"A-/B+" => "Top Right", 
			"A0/B+" => "Middle Right", 
			"A+/B+" => "Bottom Right", 
			"A+/B0" => "Bottom Center", 
			"A+/B-" => "Bottom Left", 
			"A0/B-" => "Left Center", 
			"A0/B0" => "Center", 
			_ => directionKey, 
		};
	}

	private static string ClassifyAxis(double value)
	{
		if (!(value < -15.0))
		{
			if (value > 15.0)
			{
				return "+";
			}
			return "0";
		}
		return "-";
	}
}
