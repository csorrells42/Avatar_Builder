using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record AvatarObservationBatch(IReadOnlyList<AvatarObservationCapture> Candidates, IReadOnlyList<AvatarObservationWriteResult> Results, IReadOnlyList<string> Errors)
{
	public string ProfileFolder
	{
		get
		{
			if (Candidates.Count != 0)
			{
				return Candidates[0].ProfileFolder;
			}
			return "";
		}
	}

	public string SubjectId
	{
		get
		{
			if (Candidates.Count != 0)
			{
				return Candidates[0].SubjectId;
			}
			return "";
		}
	}

	public string SubjectDisplayName
	{
		get
		{
			if (Candidates.Count != 0)
			{
				return Candidates[0].SubjectDisplayName;
			}
			return "";
		}
	}

	public int AcceptedCount => Results.Count((AvatarObservationWriteResult result) => result.Accepted);

	public int ReplacedCount => Results.Count((AvatarObservationWriteResult result) => result.Accepted && result.ReplacedExisting);

	public int RetainedCount
	{
		get
		{
			if (Results.Count != 0)
			{
				IReadOnlyList<AvatarObservationWriteResult> results = Results;
				return results[results.Count - 1].RetainedCount;
			}
			return 0;
		}
	}
}
