using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoFacePipeline : IAsyncDisposable
{
	private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1L);

	private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10L);

	private readonly MediaPipeStereoFaceReconstructor _reconstructor = new MediaPipeStereoFaceReconstructor();

	private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

	private readonly SemaphoreSlim _ownerGate = new SemaphoreSlim(1, 1);

	private readonly object _configurationLock = new object();

	private readonly object _workerLock = new object();

	private Task? _activeWorker;

	private string _profileFolder = "";

	private string _subjectId = "";

	private string _subjectDisplayName = "";

	private int _generation;

	private long _lastPublishTimestamp;

	private long _lastSaveTimestamp;

	private long _submittedFrameCount;

	private long _busyDropCount;

	private int _workerBusy;

	private int _configured;

	private bool _disposed;

	public bool IsConfigured => Volatile.Read(ref _configured) != 0;

	public event EventHandler<MediaPipeStereoModelUpdatedEventArgs>? ModelUpdated;

	public event EventHandler<string>? ProcessingFailed;

	public async Task<MediaPipeStereoFaceModel> ConfigureProfileAsync(string profileFolder, string subjectId, string subjectDisplayName, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileFolder, "profileFolder");
		ArgumentException.ThrowIfNullOrWhiteSpace(subjectId, "subjectId");
		ThrowIfDisposed();
		int generation;
		lock (_configurationLock)
		{
			Volatile.Write(ref _configured, 0);
			_profileFolder = Path.GetFullPath(profileFolder);
			_subjectId = subjectId.Trim();
			_subjectDisplayName = (string.IsNullOrWhiteSpace(subjectDisplayName) ? _subjectId : subjectDisplayName.Trim());
			generation = ++_generation;
		}
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		MediaPipeStereoFaceModel mediaPipeStereoFaceModel;
		try
		{
			string profileFolder2;
			lock (_configurationLock)
			{
				if (generation != _generation)
				{
					return MediaPipeStereoFaceModel.Empty;
				}
				profileFolder2 = _profileFolder;
			}
			Directory.CreateDirectory(profileFolder2);
			_reconstructor.Restore(MediaPipeStereoFaceStore.ReadState(profileFolder2), _subjectId, _subjectDisplayName);
			mediaPipeStereoFaceModel = _reconstructor.CreateModel();
			long timestamp = Stopwatch.GetTimestamp();
			_lastPublishTimestamp = timestamp;
			_lastSaveTimestamp = timestamp;
			Volatile.Write(ref _configured, 1);
		}
		finally
		{
			_ownerGate.Release();
		}
		Publish(mediaPipeStereoFaceModel, TimeSpan.Zero);
		return mediaPipeStereoFaceModel;
	}

	public bool TryStart(Func<MediaPipeStereoGeometryFrame> frameFactory)
	{
		ArgumentNullException.ThrowIfNull(frameFactory, "frameFactory");
		if (_disposed || !IsConfigured)
		{
			return false;
		}
		int generation = Volatile.Read(ref _generation);
		if (Interlocked.CompareExchange(ref _workerBusy, 1, 0) != 0)
		{
			Interlocked.Increment(ref _busyDropCount);
			return false;
		}
		MediaPipeStereoGeometryFrame frame;
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

	public async Task<MediaPipeStereoFaceModel> FlushAsync(bool writeViewers = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ThrowIfDisposed();
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			MediaPipeStereoFaceModel mediaPipeStereoFaceModel = _reconstructor.CreateModel();
			MediaPipeStereoFaceState state = _reconstructor.CreateState();
			string profileFolder = _profileFolder;
			if (!string.IsNullOrWhiteSpace(profileFolder))
			{
				MediaPipeStereoFaceStore.WriteData(profileFolder, state);
				if (writeViewers)
				{
					MediaPipeStereoFaceStore.WriteViewers(profileFolder, state, mediaPipeStereoFaceModel);
				}
			}
			return mediaPipeStereoFaceModel;
		}
		finally
		{
			_ownerGate.Release();
		}
	}

	public async Task<MediaPipeStereoProbabilityFaceModel> BuildProbabilityFaceAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		ThrowIfDisposed();
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		MediaPipeStereoFaceState state;
		MediaPipeStereoFaceModel sourceModel;
		string folder;
		try
		{
			state = _reconstructor.CreateState();
			sourceModel = _reconstructor.CreateModel();
			lock (_configurationLock)
			{
				folder = _profileFolder;
			}
		}
		finally
		{
			_ownerGate.Release();
		}
		if (string.IsNullOrWhiteSpace(folder))
		{
			throw new InvalidOperationException("Select an avatar profile before building a probability face.");
		}
		return await Task.Run(delegate
		{
			cancellationToken.ThrowIfCancellationRequested();
			MediaPipeStereoProbabilityFaceModel mediaPipeStereoProbabilityFaceModel = MediaPipeStereoProbabilityFaceBuilder.Build(state, sourceModel);
			MediaPipeStereoFaceStore.WriteProbabilityFace(folder, mediaPipeStereoProbabilityFaceModel);
			return mediaPipeStereoProbabilityFaceModel;
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task ProcessFrameAsync(int generation, MediaPipeStereoGeometryFrame frame)
	{
		try
		{
			if (!IsCurrentGeneration(generation))
			{
				return;
			}
			long processingStartedTimestamp = Stopwatch.GetTimestamp();
			await _ownerGate.WaitAsync(_shutdown.Token).ConfigureAwait(continueOnCapturedContext: false);
			MediaPipeStereoFaceModel? model = null;
			try
			{
				if (!IsCurrentGeneration(generation))
				{
					return;
				}
				_reconstructor.TryAddFrame(frame);
				long timestamp = Stopwatch.GetTimestamp();
				bool shouldPublish = Stopwatch.GetElapsedTime(_lastPublishTimestamp, timestamp) >= PublishInterval;
				bool shouldSave = Stopwatch.GetElapsedTime(_lastSaveTimestamp, timestamp) >= SaveInterval;
				if (shouldPublish)
				{
					model = _reconstructor.CreateModel();
				}
				if (shouldSave)
				{
					try
					{
						MediaPipeStereoFaceStore.WriteData(_profileFolder, _reconstructor.CreateState());
						_lastSaveTimestamp = timestamp;
					}
					catch (Exception ex) when (!(ex is OperationCanceledException))
					{
						this.ProcessingFailed?.Invoke(this, "Could not save the calibrated 3D face: " + ex.Message);
					}
				}
				if (shouldPublish && model != null)
				{
					_lastPublishTimestamp = timestamp;
				}
			}
			finally
			{
				_ownerGate.Release();
			}
			if (model != null)
			{
				Publish(model, Stopwatch.GetElapsedTime(processingStartedTimestamp));
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
		return generation == Volatile.Read(ref _generation) && Volatile.Read(ref _configured) != 0;
	}

	private void Publish(MediaPipeStereoFaceModel model, TimeSpan processingDuration)
	{
		this.ModelUpdated?.Invoke(this, new MediaPipeStereoModelUpdatedEventArgs(model, processingDuration, Interlocked.Read(in _submittedFrameCount), Interlocked.Read(in _busyDropCount)));
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
			catch
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
