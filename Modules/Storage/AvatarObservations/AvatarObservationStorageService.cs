using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationStorageService : IAsyncDisposable
{
	private sealed record DetachedBatchResult(AvatarObservationBatch Batch, Exception? FinalizationError);

	public const int DefaultBatchSize = 5;

	private readonly AvatarObservationRepository _repository;

	private readonly Func<AvatarObservationBatch, CancellationToken, Task>? _acceptedBatchFinalizer;

	private readonly int _batchSize;

	private readonly object _gate = new object();

	private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

	private List<AvatarObservationCapture> _pendingCandidates;

	private Task<DetachedBatchResult>? _worker;

	private Task? _workerLifecycle;

	private int _activeCandidateCount;

	private long _completedBatchCount;

	private long _ignoredCandidateCount;

	private DateTime? _lastWorkerCompletedAtUtc;

	private bool _workerBusy;

	private bool _disposed;

	public int BatchSize => _batchSize;

	public bool IsBusy
	{
		get
		{
			lock (_gate)
			{
				return _workerBusy;
			}
		}
	}

	public int PendingCandidateCount
	{
		get
		{
			lock (_gate)
			{
				return _pendingCandidates.Count;
			}
		}
	}

	public bool CanAcceptCandidate
	{
		get
		{
			lock (_gate)
			{
				return !_disposed && _pendingCandidates.Count < _batchSize;
			}
		}
	}

	public int WorkItemCount
	{
		get
		{
			lock (_gate)
			{
				return _pendingCandidates.Count + _activeCandidateCount;
			}
		}
	}

	public long CompletedBatchCount => Interlocked.Read(in _completedBatchCount);

	public long IgnoredCandidateCount => Interlocked.Read(in _ignoredCandidateCount);

	public event EventHandler<AvatarObservationBatchEventArgs>? BatchCompleted;

	public event EventHandler<AvatarObservationWorkerStartedEventArgs>? WorkerStarted;

	public event EventHandler<AvatarObservationWorkerCompletedEventArgs>? WorkerCompleted;

	public AvatarObservationStorageService(AvatarObservationRepository repository, Func<AvatarObservationBatch, CancellationToken, Task>? acceptedBatchFinalizer = null, int batchSize = 5)
	{
		_repository = repository ?? throw new ArgumentNullException("repository");
		_acceptedBatchFinalizer = acceptedBatchFinalizer;
		_batchSize = Math.Max(1, batchSize);
		_pendingCandidates = new List<AvatarObservationCapture>(_batchSize);
	}

	public AvatarObservationBatchAdmission AddCandidate(AvatarObservationCapture capture)
	{
		ArgumentNullException.ThrowIfNull(capture, "capture");
		AvatarObservationWorkerStartedEventArgs eventArgs = null;
		AvatarObservationBatchAdmission result;
		lock (_gate)
		{
			if (_disposed)
			{
				return AvatarObservationBatchAdmission.Stopped;
			}
			if (_pendingCandidates.Count > 0 && !BelongsToSameProfile(_pendingCandidates[0], capture))
			{
				_pendingCandidates.Clear();
			}
			if (_pendingCandidates.Count >= _batchSize)
			{
				Interlocked.Increment(ref _ignoredCandidateCount);
				return AvatarObservationBatchAdmission.IgnoredWaitingBatch;
			}
			_pendingCandidates.Add(capture);
			if (_pendingCandidates.Count < _batchSize)
			{
				return AvatarObservationBatchAdmission.Buffered;
			}
			if (_workerBusy)
			{
				return AvatarObservationBatchAdmission.HeldWorkerBusy;
			}
			eventArgs = StartWorkerLocked();
			result = AvatarObservationBatchAdmission.Launched;
		}
		RaiseWorkerStarted(eventArgs);
		return result;
	}

	public int DiscardPendingCandidates()
	{
		lock (_gate)
		{
			int count = _pendingCandidates.Count;
			_pendingCandidates.Clear();
			return count;
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task workerLifecycle;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			_pendingCandidates.Clear();
			workerLifecycle = _workerLifecycle;
		}
		if (workerLifecycle != null)
		{
			try
			{
				await workerLifecycle.WaitAsync(TimeSpan.FromSeconds(8L)).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (TimeoutException)
			{
				_shutdown.Cancel();
				try
				{
					await workerLifecycle.WaitAsync(TimeSpan.FromSeconds(2L)).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
				{
				}
				catch (TimeoutException)
				{
				}
			}
		}
		_shutdown.Cancel();
		_shutdown.Dispose();
	}

	private AvatarObservationWorkerStartedEventArgs StartWorkerLocked()
	{
		List<AvatarObservationCapture> detachedBatch = _pendingCandidates;
		_pendingCandidates = new List<AvatarObservationCapture>(_batchSize);
		_workerBusy = true;
		_activeCandidateCount = detachedBatch.Count;
		DateTime startedAtUtc = DateTime.UtcNow;
		DateTime? lastWorkerCompletedAtUtc = _lastWorkerCompletedAtUtc;
		TimeSpan? timeSpan;
		if (lastWorkerCompletedAtUtc.HasValue)
		{
			DateTime valueOrDefault = lastWorkerCompletedAtUtc.GetValueOrDefault();
			if (startedAtUtc >= valueOrDefault)
			{
				timeSpan = startedAtUtc - valueOrDefault;
				goto IL_0091;
			}
		}
		timeSpan = null;
		goto IL_0091;
		IL_0091:
		TimeSpan? waitAfterPreviousWorker = timeSpan;
		_workerLifecycle = (_worker = Task.Run(() => ProcessDetachedBatchAsync(detachedBatch, _shutdown.Token))).ContinueWith(delegate(Task<DetachedBatchResult> completedWorker)
		{
			CompleteDetachedWorker(completedWorker, startedAtUtc);
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		return new AvatarObservationWorkerStartedEventArgs(startedAtUtc, waitAfterPreviousWorker);
	}

	private async Task<DetachedBatchResult> ProcessDetachedBatchAsync(IReadOnlyList<AvatarObservationCapture> candidates, CancellationToken cancellationToken)
	{
		IReadOnlyList<AvatarObservationCapture> readOnlyList = SelectPersistenceCandidates(candidates);
		List<AvatarObservationWriteResult> list = new List<AvatarObservationWriteResult>(readOnlyList.Count);
		List<string> list2 = new List<string>();
		Exception finalizationError = null;
		foreach (AvatarObservationCapture item in readOnlyList)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				list.Add(_repository.SaveCapture(item));
			}
			catch (Exception ex)
			{
				list2.Add(ex.Message);
			}
		}
		AvatarObservationBatch batch = new AvatarObservationBatch(candidates, list, list2);
		if (batch.Candidates.Count > 0 && _acceptedBatchFinalizer != null)
		{
			try
			{
				await _acceptedBatchFinalizer(batch, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex2) when (!(ex2 is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
			{
				finalizationError = ex2;
			}
		}
		return new DetachedBatchResult(batch, finalizationError);
	}

	private static IReadOnlyList<AvatarObservationCapture> SelectPersistenceCandidates(IReadOnlyList<AvatarObservationCapture> candidates)
	{
		if (candidates.Count == 0 || candidates.Any((AvatarObservationCapture capture) => !string.Equals(capture.Reconstruction.BackendId, "deca-flame-recurrent-v4", StringComparison.Ordinal)))
		{
			return candidates;
		}
		AvatarObservationCapture avatarObservationCapture = null;
		double num = double.PositiveInfinity;
		foreach (AvatarObservationCapture candidate in candidates)
		{
			double currentModelCoefficientDeltaRms = candidate.Reconstruction.CurrentModelCoefficientDeltaRms;
			if (double.IsFinite(currentModelCoefficientDeltaRms) && !(currentModelCoefficientDeltaRms < 0.0) && ((object)avatarObservationCapture == null || currentModelCoefficientDeltaRms < num || (Math.Abs(currentModelCoefficientDeltaRms - num) <= 1E-09 && candidate.Reconstruction.CurrentModelSequenceNumber > avatarObservationCapture.Reconstruction.CurrentModelSequenceNumber)))
			{
				avatarObservationCapture = candidate;
				num = currentModelCoefficientDeltaRms;
			}
		}
		if ((object)avatarObservationCapture != null)
		{
			return new global::_003C_003Ez__ReadOnlySingleElementList<AvatarObservationCapture>(avatarObservationCapture);
		}
		return Array.Empty<AvatarObservationCapture>();
	}

	private void CompleteDetachedWorker(Task<DetachedBatchResult> completedWorker, DateTime startedAtUtc)
	{
		DateTime utcNow = DateTime.UtcNow;
		DetachedBatchResult detachedBatchResult = null;
		Exception ex = null;
		if (completedWorker.IsCompletedSuccessfully)
		{
			detachedBatchResult = completedWorker.Result;
		}
		else if (!completedWorker.IsCanceled)
		{
			ex = completedWorker.Exception?.GetBaseException();
		}
		AvatarObservationWorkerStartedEventArgs eventArgs = null;
		bool flag = false;
		lock (_gate)
		{
			_activeCandidateCount = 0;
			_workerBusy = false;
			_worker = null;
			_workerLifecycle = null;
			_lastWorkerCompletedAtUtc = utcNow;
			flag = !_disposed && !completedWorker.IsCanceled;
			if (!_disposed && _pendingCandidates.Count >= _batchSize)
			{
				eventArgs = StartWorkerLocked();
			}
		}
		if (!flag)
		{
			return;
		}
		if ((object)detachedBatchResult != null)
		{
			Interlocked.Increment(ref _completedBatchCount);
			try
			{
				this.BatchCompleted?.Invoke(this, new AvatarObservationBatchEventArgs(detachedBatchResult.Batch, detachedBatchResult.FinalizationError, CompletedBatchCount, IgnoredCandidateCount));
			}
			catch
			{
			}
		}
		try
		{
			this.WorkerCompleted?.Invoke(this, new AvatarObservationWorkerCompletedEventArgs(startedAtUtc, utcNow, utcNow - startedAtUtc, ex ?? detachedBatchResult?.FinalizationError));
		}
		catch
		{
		}
		RaiseWorkerStarted(eventArgs);
	}

	private void RaiseWorkerStarted(AvatarObservationWorkerStartedEventArgs? eventArgs)
	{
		if (eventArgs == null)
		{
			return;
		}
		try
		{
			this.WorkerStarted?.Invoke(this, eventArgs);
		}
		catch
		{
		}
	}

	private static bool BelongsToSameProfile(AvatarObservationCapture left, AvatarObservationCapture right)
	{
		if (string.Equals(left.ProfileFolder, right.ProfileFolder, StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal);
		}
		return false;
	}
}
