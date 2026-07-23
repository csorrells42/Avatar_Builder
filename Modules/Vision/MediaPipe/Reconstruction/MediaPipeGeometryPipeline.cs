using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeGeometryPipeline : IAsyncDisposable
{
	private sealed record QueuedFrame(int Generation, MediaPipeGeometryFrame Frame);

	private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1L);

	private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2L);

	private readonly MediaPipeNormalizedFaceReconstructor _reconstructor = new MediaPipeNormalizedFaceReconstructor();

	private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

	private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, 1);

	private readonly SemaphoreSlim _ownerGate = new SemaphoreSlim(1, 1);

	private readonly object _configurationLock = new object();

	private readonly Task _worker;

	private QueuedFrame? _pending;

	private string _profileFolder = "";

	private string _subjectId = "";

	private string _subjectDisplayName = "";

	private int _generation;

	private DateTime _lastPublishUtc = DateTime.MinValue;

	private DateTime _lastSaveUtc = DateTime.MinValue;

	private long _submittedFrameCount;

	private long _replacedFrameCount;

	private int _workerBusy;

	private bool _disposed;

	public bool IsConfigured
	{
		get
		{
			lock (_configurationLock)
			{
				return !string.IsNullOrWhiteSpace(_profileFolder);
			}
		}
	}

	public long SubmittedFrameCount => Interlocked.Read(in _submittedFrameCount);

	public long ReplacedFrameCount => Interlocked.Read(in _replacedFrameCount);

	public event EventHandler<MediaPipeGeometryModelUpdatedEventArgs>? ModelUpdated;

	public MediaPipeGeometryPipeline()
	{
		_worker = Task.Run((Func<Task?>)WorkerLoopAsync);
	}

	public async Task<MediaPipeNormalizedFaceModel> ConfigureProfileAsync(string profileFolder, string subjectId, string subjectDisplayName, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentException.ThrowIfNullOrWhiteSpace(subjectId, "subjectId");
		ThrowIfDisposed();
		int generation;
		lock (_configurationLock)
		{
			_profileFolder = Path.GetFullPath(profileFolder);
			_subjectId = subjectId.Trim();
			_subjectDisplayName = (string.IsNullOrWhiteSpace(subjectDisplayName) ? _subjectId : subjectDisplayName.Trim());
			generation = ++_generation;
			DiscardPendingFrame();
		}
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		MediaPipeNormalizedFaceModel mediaPipeNormalizedFaceModel;
		try
		{
			string profileFolder2;
			string subjectId2;
			string subjectDisplayName2;
			lock (_configurationLock)
			{
				if (generation != _generation)
				{
					return MediaPipeNormalizedFaceModel.Empty;
				}
				profileFolder2 = _profileFolder;
				subjectId2 = _subjectId;
				subjectDisplayName2 = _subjectDisplayName;
			}
			Directory.CreateDirectory(profileFolder2);
			_reconstructor.Restore(MediaPipeNormalizedFaceStore.ReadState(profileFolder2), subjectId2, subjectDisplayName2);
			mediaPipeNormalizedFaceModel = _reconstructor.CreateModel();
			MediaPipeNormalizedFaceStore.Write(profileFolder2, _reconstructor.CreateState(), mediaPipeNormalizedFaceModel);
			_lastPublishUtc = DateTime.UtcNow;
			_lastSaveUtc = _lastPublishUtc;
		}
		finally
		{
			_ownerGate.Release();
		}
		Publish(mediaPipeNormalizedFaceModel, TimeSpan.Zero);
		return mediaPipeNormalizedFaceModel;
	}

	public bool Queue(MediaPipeGeometryFrame frame)
	{
		ArgumentNullException.ThrowIfNull(frame, "frame");
		if (_disposed)
		{
			return false;
		}
		int generation;
		lock (_configurationLock)
		{
			if (string.IsNullOrWhiteSpace(_profileFolder))
			{
				return false;
			}
			generation = _generation;
		}
		if (Interlocked.CompareExchange(ref _workerBusy, 1, 0) != 0)
		{
			Interlocked.Increment(ref _replacedFrameCount);
			return false;
		}
		QueuedFrame queuedFrame = new QueuedFrame(generation, frame);
		if ((object)Interlocked.CompareExchange(ref _pending, queuedFrame, null) != null)
		{
			Interlocked.Increment(ref _replacedFrameCount);
			Interlocked.Exchange(ref _workerBusy, 0);
			return false;
		}
		Interlocked.Increment(ref _submittedFrameCount);
		try
		{
			_signal.Release();
		}
		catch (SemaphoreFullException)
		{
		}
		catch (ObjectDisposedException)
		{
			Interlocked.CompareExchange(ref _pending, null, queuedFrame);
			Interlocked.Exchange(ref _workerBusy, 0);
			return false;
		}
		return true;
	}

	public async Task<MediaPipeNormalizedFaceModel> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		ThrowIfDisposed();
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			MediaPipeNormalizedFaceModel mediaPipeNormalizedFaceModel = _reconstructor.CreateModel();
			string profileFolder;
			lock (_configurationLock)
			{
				profileFolder = _profileFolder;
			}
			if (!string.IsNullOrWhiteSpace(profileFolder))
			{
				MediaPipeNormalizedFaceStore.Write(profileFolder, _reconstructor.CreateState(), mediaPipeNormalizedFaceModel);
				_lastSaveUtc = DateTime.UtcNow;
			}
			return mediaPipeNormalizedFaceModel;
		}
		finally
		{
			_ownerGate.Release();
		}
	}

	public async Task<MediaPipeNormalizedFaceModel> ResetProfileAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		ThrowIfDisposed();
		int generation;
		string folder;
		string id;
		string displayName;
		lock (_configurationLock)
		{
			generation = ++_generation;
			folder = _profileFolder;
			id = _subjectId;
			displayName = _subjectDisplayName;
			DiscardPendingFrame();
		}
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		MediaPipeNormalizedFaceModel mediaPipeNormalizedFaceModel;
		try
		{
			if (generation != _generation)
			{
				return MediaPipeNormalizedFaceModel.Empty;
			}
			if (!string.IsNullOrWhiteSpace(folder))
			{
				MediaPipeNormalizedFaceStore.Delete(folder);
			}
			_reconstructor.Reset(id, displayName);
			mediaPipeNormalizedFaceModel = _reconstructor.CreateModel();
			if (!string.IsNullOrWhiteSpace(folder))
			{
				MediaPipeNormalizedFaceStore.Write(folder, _reconstructor.CreateState(), mediaPipeNormalizedFaceModel);
			}
		}
		finally
		{
			_ownerGate.Release();
		}
		Publish(mediaPipeNormalizedFaceModel, TimeSpan.Zero);
		return mediaPipeNormalizedFaceModel;
	}

	private async Task WorkerLoopAsync()
	{
		_ = 1;
		try
		{
			while (!_shutdown.IsCancellationRequested)
			{
				await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(continueOnCapturedContext: false);
				QueuedFrame queued = Interlocked.Exchange(ref _pending, null);
				if ((object)queued == null)
				{
					continue;
				}
				try
				{
					if (!IsCurrentGeneration(queued.Generation))
					{
						continue;
					}
					Stopwatch stopwatch = Stopwatch.StartNew();
					await _ownerGate.WaitAsync(_shutdown.Token).ConfigureAwait(continueOnCapturedContext: false);
					MediaPipeNormalizedFaceModel mediaPipeNormalizedFaceModel = null;
					try
					{
						if (!IsCurrentGeneration(queued.Generation) || !_reconstructor.TryAddFrame(queued.Frame))
						{
							continue;
						}
						DateTime utcNow = DateTime.UtcNow;
						bool flag = utcNow - _lastPublishUtc >= PublishInterval;
						bool flag2 = utcNow - _lastSaveUtc >= SaveInterval;
						if (flag || flag2)
						{
							mediaPipeNormalizedFaceModel = _reconstructor.CreateModel();
						}
						if (flag2 && mediaPipeNormalizedFaceModel != null)
						{
							string profileFolder;
							lock (_configurationLock)
							{
								profileFolder = _profileFolder;
							}
							if (!string.IsNullOrWhiteSpace(profileFolder))
							{
								MediaPipeNormalizedFaceStore.Write(profileFolder, _reconstructor.CreateState(), mediaPipeNormalizedFaceModel);
								_lastSaveUtc = utcNow;
							}
						}
						if (flag && mediaPipeNormalizedFaceModel != null)
						{
							_lastPublishUtc = utcNow;
						}
						goto IL_024d;
					}
					finally
					{
						_ownerGate.Release();
					}
					IL_024d:
					stopwatch.Stop();
					if (mediaPipeNormalizedFaceModel != null)
					{
						Publish(mediaPipeNormalizedFaceModel, stopwatch.Elapsed);
					}
				}
				finally
				{
					Interlocked.Exchange(ref _workerBusy, 0);
				}
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
	}

	private bool IsCurrentGeneration(int generation)
	{
		lock (_configurationLock)
		{
			return generation == _generation && !string.IsNullOrWhiteSpace(_profileFolder);
		}
	}

	private void DiscardPendingFrame()
	{
		if ((object)Interlocked.Exchange(ref _pending, null) != null)
		{
			Interlocked.Exchange(ref _workerBusy, 0);
		}
	}

	private void Publish(MediaPipeNormalizedFaceModel model, TimeSpan processingDuration)
	{
		this.ModelUpdated?.Invoke(this, new MediaPipeGeometryModelUpdatedEventArgs(model, processingDuration, SubmittedFrameCount, ReplacedFrameCount));
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	public async ValueTask DisposeAsync()
	{
		if (!_disposed)
		{
			try
			{
				await FlushAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception)
			{
			}
			_disposed = true;
			_shutdown.Cancel();
			try
			{
				_signal.Release();
			}
			catch (SemaphoreFullException)
			{
			}
			try
			{
				await _worker.ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
			}
			_ownerGate.Dispose();
			_signal.Dispose();
			_shutdown.Dispose();
		}
	}
}
