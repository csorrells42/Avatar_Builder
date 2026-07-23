using System;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationWorkerCompletedEventArgs(DateTime startedAtUtc, DateTime completedAtUtc, TimeSpan duration, Exception? error) : EventArgs
{
	public DateTime StartedAtUtc { get; } = startedAtUtc;

	public DateTime CompletedAtUtc { get; } = completedAtUtc;

	public TimeSpan Duration { get; } = duration;

	public Exception? Error { get; } = error;
}
