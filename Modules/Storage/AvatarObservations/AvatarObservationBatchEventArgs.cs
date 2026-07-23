using System;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationBatchEventArgs : EventArgs
{
	public string SubjectId => Batch.SubjectId;

	public AvatarObservationBatch Batch { get; }

	public Exception? FinalizationError { get; }

	public long CompletedBatchCount { get; }

	public long IgnoredCandidateCount { get; }

	public AvatarObservationBatchEventArgs(AvatarObservationBatch batch, Exception? finalizationError, long completedBatchCount, long ignoredCandidateCount)
	{
		Batch = batch;
		FinalizationError = finalizationError;
		CompletedBatchCount = completedBatchCount;
		IgnoredCandidateCount = ignoredCandidateCount;
	}
}
