using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoFacePipeline : IAsyncDisposable
{
	private sealed record QueuedFrame(int Generation, MediaPipeStereoGeometryFrame Frame);

	private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1L);

	private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10L);

	private readonly MediaPipeStereoFaceReconstructor _reconstructor = new MediaPipeStereoFaceReconstructor();

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

	public event EventHandler<MediaPipeStereoModelUpdatedEventArgs>? ModelUpdated;

	public event EventHandler<string>? ProcessingFailed;

	public MediaPipeStereoFacePipeline()
	{
		_worker = Task.Run((Func<Task?>)WorkerLoopAsync);
	}

	public async Task<MediaPipeStereoFaceModel> ConfigureProfileAsync(string profileFolder, string subjectId, string subjectDisplayName, CancellationToken cancellationToken = default(CancellationToken))
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
			MediaPipeStereoFaceStore.Write(profileFolder2, _reconstructor.CreateState(), mediaPipeStereoFaceModel);
			_lastPublishUtc = DateTime.UtcNow;
			_lastSaveUtc = _lastPublishUtc;
		}
		finally
		{
			_ownerGate.Release();
		}
		Publish(mediaPipeStereoFaceModel, TimeSpan.Zero);
		return mediaPipeStereoFaceModel;
	}

	public bool Queue(MediaPipeStereoGeometryFrame frame)
	{
		ArgumentNullException.ThrowIfNull(frame, "frame");
		if (_disposed || !IsConfigured)
		{
			return false;
		}
		int generation;
		lock (_configurationLock)
		{
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

	public async Task<MediaPipeStereoFaceModel> FlushAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		ThrowIfDisposed();
		await _ownerGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			MediaPipeStereoFaceModel mediaPipeStereoFaceModel = _reconstructor.CreateModel();
			string profileFolder;
			lock (_configurationLock)
			{
				profileFolder = _profileFolder;
			}
			if (!string.IsNullOrWhiteSpace(profileFolder))
			{
				MediaPipeStereoFaceStore.Write(profileFolder, _reconstructor.CreateState(), mediaPipeStereoFaceModel);
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
					MediaPipeStereoFaceModel mediaPipeStereoFaceModel = null;
					try
					{
						if (!IsCurrentGeneration(queued.Generation))
						{
							continue;
						}
						_reconstructor.TryAddFrame(queued.Frame);
						DateTime utcNow = DateTime.UtcNow;
						bool flag = utcNow - _lastPublishUtc >= PublishInterval;
						bool flag2 = utcNow - _lastSaveUtc >= SaveInterval;
						if (flag || flag2)
						{
							mediaPipeStereoFaceModel = _reconstructor.CreateModel();
						}
						if (flag2 && mediaPipeStereoFaceModel != null)
						{
							string profileFolder;
							lock (_configurationLock)
							{
								profileFolder = _profileFolder;
							}
							try
							{
								MediaPipeStereoFaceStore.Write(profileFolder, _reconstructor.CreateState(), mediaPipeStereoFaceModel);
								_lastSaveUtc = utcNow;
							}
							catch (Exception ex) when (!(ex is OperationCanceledException))
							{
								this.ProcessingFailed?.Invoke(this, "Could not save the calibrated 3D face: " + ex.Message);
							}
						}
						if (flag && mediaPipeStereoFaceModel != null)
						{
							_lastPublishUtc = utcNow;
						}
						goto IL_0292;
					}
					finally
					{
						_ownerGate.Release();
					}
					IL_0292:
					stopwatch.Stop();
					if (mediaPipeStereoFaceModel != null)
					{
						Publish(mediaPipeStereoFaceModel, stopwatch.Elapsed);
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

	private void Publish(MediaPipeStereoFaceModel model, TimeSpan processingDuration)
	{
		this.ModelUpdated?.Invoke(this, new MediaPipeStereoModelUpdatedEventArgs(model, processingDuration, Interlocked.Read(in _submittedFrameCount), Interlocked.Read(in _replacedFrameCount)));
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
