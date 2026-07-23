using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed record DecaProjectionFitEvaluation(bool IsMeasured, bool PassesRetentionGate, double FitConfidencePercent, int AnchorCount, double AnchorRmseFaceWidthPercent, double ProjectedToObservedJawWidthRatio, double CenterOffsetFaceWidthPercent, string Summary, IReadOnlyList<string> Warnings)
{
	public static DecaProjectionFitEvaluation NotMeasured(string reason)
	{
		return new DecaProjectionFitEvaluation(IsMeasured: false, PassesRetentionGate: false, 0.0, 0, 0.0, 0.0, 0.0, reason, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { reason, "Rejected: DECA identity evidence requires a measured same-frame projection fit." }));
	}
}
