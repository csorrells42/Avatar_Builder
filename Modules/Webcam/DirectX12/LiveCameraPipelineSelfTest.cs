using System;
using System.Diagnostics;
using System.Threading;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record LiveCameraPipelineSelfTestResult(bool Succeeded, string Detail);

public static class LiveCameraPipelineSelfTest
{
	public static LiveCameraPipelineSelfTestResult Run()
	{
		using ManualResetEventSlim processingStarted = new ManualResetEventSlim(initialState: false);
		using ManualResetEventSlim allowProcessingToFinish = new ManualResetEventSlim(initialState: false);
		using ManualResetEventSlim processingFinished = new ManualResetEventSlim(initialState: false);
		int processedFrames = 0;

		using LatestTextureFrameWorker worker = new LatestTextureFrameWorker(
			"Avatar Builder pipeline self-test",
			frame =>
			{
				Interlocked.Increment(ref processedFrames);
				processingStarted.Set();
				allowProcessingToFinish.Wait(TimeSpan.FromSeconds(2));
				processingFinished.Set();
			});

		using PooledFrameBuffer buffer = PooledFrameBuffer.Rent(24);
		using TextureNativeFrameLease source = new TextureNativeFrameLease(
			resource: IntPtr.Zero,
			subresource: 0,
			width: 4,
			height: 4,
			framesPerSecond: 600,
			deviceMode: "self-test",
			mediaSubtype: "NV12",
			frameNumber: 1,
			nv12PreviewBuffer: buffer.AddReference(),
			nv12PreviewStride: 4,
			capturedAtTimestamp: Stopwatch.GetTimestamp(),
			capturedAtUtc: DateTime.UtcNow);

		bool firstAccepted = worker.TryAcceptPreviewData(source);
		bool workerStarted = processingStarted.Wait(TimeSpan.FromSeconds(2));
		bool busyArrivalDroppedBeforeWork = !worker.TryAcceptPreviewData(source);
		allowProcessingToFinish.Set();
		bool workerFinished = processingFinished.Wait(TimeSpan.FromSeconds(2));

		long oldTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
		bool currentOverlayIsFresh = new PreviewTrackingOverlay
		{
			FaceBox = new PreviewOverlayRect(0.1, 0.1, 0.9, 0.9),
			SourceTimestamp = Stopwatch.GetTimestamp(),
			MaximumAge = TimeSpan.FromMilliseconds(250)
		}.IsFresh;
		bool oldOverlayExpired = !new PreviewTrackingOverlay
		{
			FaceBox = new PreviewOverlayRect(0.1, 0.1, 0.9, 0.9),
			SourceTimestamp = oldTimestamp,
			MaximumAge = TimeSpan.FromMilliseconds(250)
		}.IsFresh;

		using ManualResetEventSlim isolatedFailureObserved =
			new ManualResetEventSlim(initialState: false);
		using ManualResetEventSlim postFailureFrameCompleted =
			new ManualResetEventSlim(initialState: false);
		int isolationAttempts = 0;
		int isolatedFailures = 0;
		using LatestTextureFrameWorker failureIsolatedWorker =
			new LatestTextureFrameWorker(
				"Avatar Builder failure isolation self-test",
				frame =>
				{
					if (Interlocked.Increment(ref isolationAttempts) == 1)
					{
						throw new InvalidOperationException(
							"Injected observer failure.");
					}
					postFailureFrameCompleted.Set();
				},
				failureHandler: exception =>
				{
					Interlocked.Increment(ref isolatedFailures);
					isolatedFailureObserved.Set();
				});
		bool failureFrameAccepted =
			failureIsolatedWorker.TryAcceptPreviewData(source);
		bool failureWasIsolated =
			isolatedFailureObserved.Wait(TimeSpan.FromSeconds(2));
		bool recoveryFrameAccepted = false;
		long recoveryAttemptStarted = Stopwatch.GetTimestamp();
		while (!recoveryFrameAccepted
			&& Stopwatch.GetElapsedTime(recoveryAttemptStarted) <
				TimeSpan.FromSeconds(2))
		{
			recoveryFrameAccepted =
				failureIsolatedWorker.TryAcceptPreviewData(source);
			if (!recoveryFrameAccepted)
			{
				Thread.Sleep(1);
			}
		}
		bool recoveredAfterFailure =
			postFailureFrameCompleted.Wait(TimeSpan.FromSeconds(2));

		bool succeeded = firstAccepted
			&& workerStarted
			&& busyArrivalDroppedBeforeWork
			&& workerFinished
			&& Volatile.Read(ref processedFrames) == 1
			&& currentOverlayIsFresh
			&& oldOverlayExpired
			&& failureFrameAccepted
			&& failureWasIsolated
			&& recoveryFrameAccepted
			&& recoveredAfterFailure
			&& Volatile.Read(ref isolatedFailures) == 1;
		return new LiveCameraPipelineSelfTestResult(
			succeeded,
			succeeded
				? "One frame completed on the dedicated lane; the busy arrival was rejected before conversion; an injected worker exception was isolated and the next frame completed; stale overlays expired."
				: $"accepted={firstAccepted}, started={workerStarted}, busyDrop={busyArrivalDroppedBeforeWork}, finished={workerFinished}, processed={processedFrames}, freshOverlay={currentOverlayIsFresh}, expiredOverlay={oldOverlayExpired}, failureAccepted={failureFrameAccepted}, failureIsolated={failureWasIsolated}, recoveryAccepted={recoveryFrameAccepted}, recovered={recoveredAfterFailure}, failures={isolatedFailures}");
	}
}
