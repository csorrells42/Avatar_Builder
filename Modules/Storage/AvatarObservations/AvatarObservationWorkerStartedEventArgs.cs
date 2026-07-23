using System;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationWorkerStartedEventArgs(DateTime startedAtUtc, TimeSpan? waitAfterPreviousWorker) : EventArgs
{
	public DateTime StartedAtUtc { get; } = startedAtUtc;

	public TimeSpan? WaitAfterPreviousWorker { get; } = waitAfterPreviousWorker;
}
