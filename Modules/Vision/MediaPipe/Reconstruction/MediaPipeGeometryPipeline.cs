using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeGeometryPipeline : IAsyncDisposable
{
	private static readonly long SaveIntervalTicks = Stopwatch.Frequency * 5L;

	private readonly MediaPipeNormalizedFaceReconstructor _reconstructor = new MediaPipeNormalizedFaceReconstructor();

	private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

	private readonly SemaphoreSlim _ownerGate = new SemaphoreSlim(1, 1);

	private readonly object _configurationLock = new object();

	private readonly object _workerLock = new object();

	private Task? _activeWorker;

	private string _profileFolder = "";

	private string _subjectId = "";

	private string _subjectDisplayName = "";

	private int _generation;

	private int _configured;

	private long _lastSaveTimestamp;

	private long _submittedFrameCount;

	private long _busyDropCount;

	private int _workerBusy;

	private bool _disposed;

	public bool IsConfigured
	{
		get => Volatile.Read(ref _configured) != 0;
	}

	public long SubmittedFrameCount => Interlocked.Read(in _submittedFrameCount);

	public long BusyDropCount => Interlocked.Read(in _busyDropCount);

	public event EventHandler<MediaPipeGeometryModelUpdatedEventArgs>? ModelUpdated;

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
			Volatile.Write(ref _configured, 1);
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
			MediaPipeNormalizedFaceStore.WriteData(profileFolder2, _reconstructor.CreateState());
			long timestamp = Stopwatch.GetTimestamp();
			_lastSaveTimestamp = timestamp;
		}
		finally
		{
			_ownerGate.Release();
		}
		Publish(mediaPipeNormalizedFaceModel, TimeSpan.Zero);
		return mediaPipeNormalizedFaceModel;
	}

	public bool TryStart(Func<MediaPipeGeometryFrame> frameFactory)
	{
		ArgumentNullException.ThrowIfNull(frameFactory, "frameFactory");
		if (_disposed)
		{
			return false;
		}
		if (Volatile.Read(ref _configured) == 0)
		{
			return false;
		}
		int generation = Volatile.Read(ref _generation);
		if (Interlocked.CompareExchange(ref _workerBusy, 1, 0) != 0)
		{
			Interlocked.Increment(ref _busyDropCount);
			return false;
		}
		MediaPipeGeometryFrame frame;
		try
		{
			frame = frameFactory();
		}
		catch
		{
			Interlocked.Exchange(ref _workerBusy, 0);
			throw;
		}
		Interlocked.Increment(ref _submittedFrameCount);
		Task task = Task.Run(() => ProcessFrameAsync(generation, frame));
		lock (_workerLock)
		{
			_activeWorker = task;
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
				MediaPipeNormalizedFaceStore.WriteData(profileFolder, _reconstructor.CreateState());
				_lastSaveTimestamp = Stopwatch.GetTimestamp();
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
				MediaPipeNormalizedFaceStore.WriteData(folder, _reconstructor.CreateState());
			}
			long timestamp = Stopwatch.GetTimestamp();
			_lastSaveTimestamp = timestamp;
		}
		finally
		{
			_ownerGate.Release();
		}
		Publish(mediaPipeNormalizedFaceModel, TimeSpan.Zero);
		return mediaPipeNormalizedFaceModel;
	}

	private async Task ProcessFrameAsync(int generation, MediaPipeGeometryFrame frame)
	{
		try
		{
			if (!IsCurrentGeneration(generation))
			{
				return;
			}
			long startedAt = Stopwatch.GetTimestamp();
			await _ownerGate.WaitAsync(_shutdown.Token).ConfigureAwait(continueOnCapturedContext: false);
			MediaPipeNormalizedFaceModel? model = null;
			bool publishModel = false;
			try
			{
				if (!IsCurrentGeneration(generation) || !_reconstructor.TryAddFrame(frame))
				{
					return;
				}
				long timestamp = Stopwatch.GetTimestamp();
				bool shouldSave = timestamp - Volatile.Read(ref _lastSaveTimestamp) >= SaveIntervalTicks;
				if (shouldSave)
				{
					model = _reconstructor.CreateModel();
				}
				if (shouldSave)
				{
					string profileFolder;
					lock (_configurationLock)
					{
						profileFolder = _profileFolder;
					}
					if (!string.IsNullOrWhiteSpace(profileFolder))
					{
						MediaPipeNormalizedFaceStore.WriteData(profileFolder, _reconstructor.CreateState());
						Volatile.Write(ref _lastSaveTimestamp, timestamp);
					}
				}
				if (shouldSave && model != null)
				{
					publishModel = true;
				}
			}
			finally
			{
				_ownerGate.Release();
			}
			if (publishModel && model != null)
			{
				Publish(model, Stopwatch.GetElapsedTime(startedAt));
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
		finally
		{
			Interlocked.Exchange(ref _workerBusy, 0);
		}
	}

	private bool IsCurrentGeneration(int generation)
	{
		return Volatile.Read(ref _configured) != 0
			&& generation == Volatile.Read(ref _generation);
	}

	private void Publish(MediaPipeNormalizedFaceModel model, TimeSpan processingDuration)
	{
		this.ModelUpdated?.Invoke(this, new MediaPipeGeometryModelUpdatedEventArgs(model, processingDuration, SubmittedFrameCount, BusyDropCount));
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
			Task? activeWorker;
			lock (_workerLock)
			{
				activeWorker = _activeWorker;
			}
			try
			{
				if (activeWorker != null)
				{
					await activeWorker.ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (OperationCanceledException)
			{
			}
			_ownerGate.Dispose();
			_shutdown.Dispose();
		}
	}
}
