using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX11;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class TextureNativeCameraStream : IDisposable
{
	private static readonly TimeSpan StreamStartTimeout = TimeSpan.FromSeconds(8L);

	private static readonly TimeSpan StreamStopTimeout = TimeSpan.FromSeconds(3L);

	private readonly object _stateLock = new object();

	private readonly object _processedDenoiseLock = new object();

	private readonly CameraDevice _camera;

	private readonly CameraVideoMode? _mode;

	private readonly ManualResetEventSlim _streamReady = new ManualResetEventSlim(initialState: false);

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private MediaFoundationCameraDeviceFactory.MediaFoundationScope? _mediaFoundationScope;

	private ITextureNativeDeviceManager? _deviceManager;

	private object? _mediaSource;

	private IMFSourceReader? _reader;

	private Task? _captureTask;

	private int _width;

	private int _height;

	private double _fps;

	private Guid _subtype;

	private long _sampleDuration;

	private MediaFoundationTextureVideoRecorder? _recorder;

	private MediaFoundationVideoRecorder? _processedRecorder;

	private Direct3D11SharedTextureBridge? _d3d11SharedTextureBridge;

	private bool _d3d11SharedTextureBridgeUnavailable;

	private byte[]? _previousProcessedDenoiseFrame;

	private string? _recordingPath;

	private string _recordingPipeline = "Media Foundation texture-native raw camera samples";

	private bool _recordingMatchesPreviewDenoise;

	private bool _recordingColorPolishApplied;

	private bool _recordingMatchesPreviewColor;

	private VideoFrameColorSettings _processedRecordingColorSettings = VideoFrameColorSettings.Off;

	private bool _processedRecordingDenoiseEnabled;

	private double _processedRecordingDenoiseStrength = 2.0;

	private bool _isPaused;

	private bool _isStopping;

	private long _nextSampleTime;

	private long _framesRead;

	private string _status = "Texture-native camera stream started.";

	private bool _captureStarted;

	private Exception? _startException;

	public int Width => _width;

	public int Height => _height;

	public double FramesPerSecond => _fps;

	public string DeviceMode => _deviceManager?.ModeName ?? "starting";

	public string MediaSubtype => MediaFoundationInterop.FormatSubtype(_subtype);

	public long FramesRead => Interlocked.Read(in _framesRead);

	public int SamplesWritten => _recorder?.SamplesWritten ?? _processedRecorder?.SamplesWritten ?? 0;

	public bool IsRecording
	{
		get
		{
			lock (_stateLock)
			{
				return _recorder != null || _processedRecorder != null;
			}
		}
	}

	public event EventHandler<TextureNativeFrameInfo>? FrameAvailable;

	public event EventHandler<TextureNativeFrameLease>? TextureFrameAvailable;

	public event EventHandler<string>? StatusChanged;

	public TextureNativeCameraStream(CameraDevice camera, CameraVideoMode? mode, bool startImmediately = true)
	{
		_camera = camera;
		_mode = mode;
		if (startImmediately)
		{
			Start();
		}
	}

	public nint DuplicateNativeD3D12Device()
	{
		return _deviceManager?.DuplicateNativeD3D12Device() ?? IntPtr.Zero;
	}

	public void Start()
	{
		lock (_stateLock)
		{
			if (_captureStarted || _isStopping)
			{
				return;
			}
			_captureStarted = true;
			_captureTask = Task.Run(delegate
			{
				CaptureLoop(_cancellation.Token);
			});
		}
		if (!_streamReady.Wait(StreamStartTimeout))
		{
			throw new TimeoutException($"Texture-native stream did not initialize within {StreamStartTimeout.TotalSeconds:0.#} seconds.");
		}
		if (_startException != null)
		{
			throw new InvalidOperationException("Texture-native stream failed to initialize: " + _startException.Message, _startException);
		}
	}

	public bool StartRecording(string path, TextureNativeRecordingOptions? options = null)
	{
		lock (_stateLock)
		{
			ITextureNativeDeviceManager textureNativeDeviceManager = _deviceManager ?? throw new InvalidOperationException("Texture-native recording requires an initialized device manager.");
			if (_recorder != null || _processedRecorder != null)
			{
				return true;
			}
			if ((object)options != null && options.ProcessedOutputEnabled)
			{
				_processedRecorder = new MediaFoundationVideoRecorder(path, _width, _height, _fps, null);
				_processedRecordingDenoiseEnabled = options.DenoiseEnabled;
				_processedRecordingDenoiseStrength = options.DenoiseStrength;
				_processedRecordingColorSettings = options.ColorSettings;
				ResetProcessedDenoiseHistory();
				_recordingPipeline = "Texture-native processed BGRA bridge";
				_recordingMatchesPreviewDenoise = true;
				_recordingColorPolishApplied = options.ColorSettings.HasVisibleAdjustments;
				_recordingMatchesPreviewColor = true;
			}
			else
			{
				_recorder = new MediaFoundationTextureVideoRecorder(path, _width, _height, _fps, _subtype, textureNativeDeviceManager.Manager);
				_processedRecordingDenoiseEnabled = false;
				_processedRecordingColorSettings = VideoFrameColorSettings.Off;
				ResetProcessedDenoiseHistory();
				_recordingPipeline = "Media Foundation texture-native raw camera samples";
				_recordingMatchesPreviewDenoise = (object)options == null || !options.DenoiseEnabled;
				_recordingColorPolishApplied = false;
				_recordingMatchesPreviewColor = (object)options == null || !options.ColorSettings.HasVisibleAdjustments;
			}
			_recordingPath = path;
			_nextSampleTime = 0L;
			_isPaused = false;
			_status = "Texture-native GPU recording started: " + Path.GetFileName(path);
			this.StatusChanged?.Invoke(this, _status);
			return true;
		}
	}

	public void PauseRecording()
	{
		lock (_stateLock)
		{
			_isPaused = true;
			_processedRecorder?.Pause();
			_status = "Texture-native GPU recording paused.";
			this.StatusChanged?.Invoke(this, _status);
		}
	}

	public void ResumeRecording()
	{
		lock (_stateLock)
		{
			_isPaused = false;
			_processedRecorder?.Resume();
			_status = "Texture-native GPU recording resumed.";
			this.StatusChanged?.Invoke(this, _status);
		}
	}

	public TextureNativeRecordingResult? StopRecording()
	{
		MediaFoundationTextureVideoRecorder recorder;
		MediaFoundationVideoRecorder processedRecorder;
		string recordingPath;
		string status;
		string recordingPipeline;
		bool processedRecordingDenoiseEnabled;
		bool recordingMatchesPreviewDenoise;
		bool recordingColorPolishApplied;
		bool recordingMatchesPreviewColor;
		lock (_stateLock)
		{
			recorder = _recorder;
			processedRecorder = _processedRecorder;
			recordingPath = _recordingPath;
			status = _status;
			recordingPipeline = _recordingPipeline;
			processedRecordingDenoiseEnabled = _processedRecordingDenoiseEnabled;
			recordingMatchesPreviewDenoise = _recordingMatchesPreviewDenoise;
			recordingColorPolishApplied = _recordingColorPolishApplied;
			recordingMatchesPreviewColor = _recordingMatchesPreviewColor;
			_recorder = null;
			_processedRecorder = null;
			_recordingPath = null;
			_isPaused = false;
			ResetProcessedDenoiseHistory();
			_processedRecordingDenoiseEnabled = false;
			_processedRecordingColorSettings = VideoFrameColorSettings.Off;
			_recordingMatchesPreviewDenoise = false;
			_recordingColorPolishApplied = false;
			_recordingMatchesPreviewColor = false;
			_recordingPipeline = "Media Foundation texture-native raw camera samples";
		}
		if ((recorder == null && processedRecorder == null) || string.IsNullOrWhiteSpace(recordingPath))
		{
			return null;
		}
		try
		{
			recorder?.Stop();
			processedRecorder?.Stop();
			long num = (File.Exists(recordingPath) ? new FileInfo(recordingPath).Length : 0);
			int num2 = recorder?.SamplesWritten ?? processedRecorder?.SamplesWritten ?? 0;
			int num3;
			object obj;
			if (num2 > 0)
			{
				num3 = ((num > 4096) ? 1 : 0);
				if (num3 != 0)
				{
					obj = ((processedRecorder != null) ? "Texture-native processed bridge recording completed." : "Texture-native shared stream recording completed.");
					goto IL_0137;
				}
			}
			else
			{
				num3 = 0;
			}
			obj = status;
			goto IL_0137;
			IL_0137:
			return new TextureNativeRecordingResult(Status: (string)obj, Success: (byte)num3 != 0, Path: recordingPath, SamplesWritten: num2, BytesWritten: num, DeviceMode: _deviceManager?.ModeName ?? "none", MediaSubtype: (processedRecorder != null) ? "rgb32" : MediaFoundationInterop.FormatSubtype(_subtype), Width: _width, Height: _height, FramesPerSecond: _fps, RecordingPipeline: recordingPipeline, RecordingDenoiseApplied: processedRecordingDenoiseEnabled, RecordingMatchesPreviewDenoise: recordingMatchesPreviewDenoise, RecordingColorPolishApplied: recordingColorPolishApplied, RecordingMatchesPreviewColor: recordingMatchesPreviewColor);
		}
		finally
		{
			recorder?.Dispose();
			processedRecorder?.Dispose();
		}
	}

	public void Stop()
	{
		lock (_stateLock)
		{
			if (_isStopping)
			{
				return;
			}
			_isStopping = true;
		}
		_cancellation.Cancel();
		TryFlushSourceReader();
		Task captureTask;
		lock (_stateLock)
		{
			captureTask = _captureTask;
		}
		bool flag = false;
		try
		{
			flag = captureTask?.Wait(StreamStopTimeout) ?? true;
		}
		catch
		{
			flag = true;
		}
		if (!flag)
		{
			TryShutdownMediaSource();
			TryFlushSourceReader();
			try
			{
				captureTask?.Wait(StreamStopTimeout);
			}
			catch
			{
			}
		}
		StopRecording();
	}

	public void Dispose()
	{
		Stop();
		_cancellation.Dispose();
		_streamReady.Dispose();
		TryShutdownMediaSource();
		MediaFoundationInterop.ReleaseComObject(_reader);
		_reader = null;
		MediaFoundationInterop.ReleaseComObject(_mediaSource);
		_mediaSource = null;
		_d3d11SharedTextureBridge?.Dispose();
		_d3d11SharedTextureBridge = null;
		_d3d11SharedTextureBridgeUnavailable = false;
		_deviceManager?.Dispose();
		_mediaFoundationScope?.Dispose();
	}

	private void TryShutdownMediaSource()
	{
		try
		{
			if (_mediaSource is IMFMediaSource iMFMediaSource)
			{
				iMFMediaSource.Shutdown();
			}
		}
		catch (COMException)
		{
		}
		catch (InvalidComObjectException)
		{
		}
	}

	private void CaptureLoop(CancellationToken cancellationToken)
	{
		try
		{
			_mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
			(ITextureNativeDeviceManager, IMFSourceReader, object) tuple = TextureNativeCameraRecorder.OpenTextureSourceReader(_camera, _mode);
			_deviceManager = tuple.Item1;
			_reader = tuple.Item2;
			_mediaSource = tuple.Item3;
			(int, int, double, Guid) tuple2 = ReadCurrentFormat(_reader, _mode);
			_width = tuple2.Item1;
			_height = tuple2.Item2;
			_fps = tuple2.Item3;
			_subtype = tuple2.Item4;
			_sampleDuration = Math.Max(1L, (long)Math.Round(10000000.0 / Math.Clamp(_fps, 1.0, 120.0)));
			_streamReady.Set();
			while (!cancellationToken.IsCancellationRequested)
			{
				IMFSourceReader reader = _reader;
				if (reader == null)
				{
					ReportStatus("Texture-native shared stream reader is not initialized.");
					break;
				}
				int actualStreamIndex;
				int streamFlags;
				long timestamp;
				object sample;
				int num = reader.ReadSample(-4, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample);
				if (MediaFoundationInterop.Failed(num))
				{
					ReportStatus($"Texture-native shared stream read failed: 0x{num:X8}");
					break;
				}
				if ((streamFlags & 2) != 0)
				{
					ReportStatus("Texture-native shared stream ended.");
					break;
				}
				if (!(sample is IMFSample sample2))
				{
					MediaFoundationInterop.ReleaseComObject(sample);
					continue;
				}
				try
				{
					long frameNumber = Interlocked.Increment(ref _framesRead);
					using TextureNativeFrameLease textureNativeFrameLease = TryCreateFrameLease(sample2, frameNumber);
					NotifyFrameAvailable(new TextureNativeFrameInfo(_width, _height, _fps, _deviceManager.ModeName, MediaFoundationInterop.FormatSubtype(_subtype), frameNumber));
					if (textureNativeFrameLease != null)
					{
						NotifyTextureFrameAvailable(textureNativeFrameLease);
					}
					WriteRecordingSample(sample2);
				}
				finally
				{
					MediaFoundationInterop.ReleaseComObject(sample);
				}
			}
		}
		catch (Exception ex)
		{
			if (_startException == null)
			{
				_startException = ex;
			}
			_streamReady.Set();
			ReportStatus(ex.Message);
		}
	}

	private void TryFlushSourceReader()
	{
		try
		{
			_reader?.Flush(-4);
		}
		catch
		{
		}
	}

	private void WriteRecordingSample(IMFSample sample)
	{
		MediaFoundationTextureVideoRecorder recorder;
		MediaFoundationVideoRecorder processedRecorder;
		bool isPaused;
		bool processedRecordingDenoiseEnabled;
		double processedRecordingDenoiseStrength;
		VideoFrameColorSettings processedRecordingColorSettings;
		lock (_stateLock)
		{
			recorder = _recorder;
			processedRecorder = _processedRecorder;
			isPaused = _isPaused;
			processedRecordingDenoiseEnabled = _processedRecordingDenoiseEnabled;
			processedRecordingDenoiseStrength = _processedRecordingDenoiseStrength;
			processedRecordingColorSettings = _processedRecordingColorSettings;
		}
		if (isPaused || (recorder == null && processedRecorder == null))
		{
			return;
		}
		if (processedRecorder != null)
		{
			if (!TryCreateBgraFrame(sample, out byte[] bgraBytes))
			{
				return;
			}
			if (processedRecordingDenoiseEnabled)
			{
				lock (_processedDenoiseLock)
				{
					VideoFrameDenoiser.ApplyTemporalDenoise(bgraBytes, processedRecordingDenoiseStrength, ref _previousProcessedDenoiseFrame);
				}
			}
			else
			{
				ResetProcessedDenoiseHistory();
			}
			if (processedRecordingColorSettings.HasVisibleAdjustments)
			{
				VideoFrameColorProcessor.Apply(bgraBytes, processedRecordingColorSettings);
			}
			processedRecorder.WriteFrame(bgraBytes);
		}
		else if (recorder != null)
		{
			MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
			MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(_sampleDuration));
			recorder.WriteSample(sample);
		}
		_nextSampleTime += _sampleDuration;
	}

	private void ReportStatus(string status)
	{
		lock (_stateLock)
		{
			_status = status;
		}
		NotifyStatusChanged(status);
	}

	private void NotifyFrameAvailable(TextureNativeFrameInfo frame)
	{
		EventHandler<TextureNativeFrameInfo> eventHandler = this.FrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<TextureNativeFrameInfo>)obj)(this, frame);
			}
			catch (Exception exception)
			{
				SetObserverFailureStatus("frame", exception);
			}
		}
	}

	private void NotifyTextureFrameAvailable(TextureNativeFrameLease frame)
	{
		EventHandler<TextureNativeFrameLease> eventHandler = this.TextureFrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<TextureNativeFrameLease>)obj)(this, frame);
			}
			catch (Exception exception)
			{
				SetObserverFailureStatus("texture", exception);
			}
		}
	}

	private void SetObserverFailureStatus(string observer, Exception exception)
	{
		lock (_stateLock)
		{
			_status = "Texture-native " + observer + " observer failed: " + exception.Message;
		}
		NotifyStatusChanged(_status);
	}

	private void NotifyStatusChanged(string status)
	{
		EventHandler<string> eventHandler = this.StatusChanged;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<string>)obj)(this, status);
			}
			catch
			{
			}
		}
	}

	private void ResetProcessedDenoiseHistory()
	{
		lock (_processedDenoiseLock)
		{
			_previousProcessedDenoiseFrame = null;
		}
	}

	private bool TryCreateBgraFrame(IMFSample sample, out byte[] bgraBytes)
	{
		bgraBytes = Array.Empty<byte>();
		IMFMediaBuffer buffer = null;
		try
		{
			if (MediaFoundationInterop.Failed(sample.GetBufferByIndex(0, out buffer)) || buffer == null)
			{
				return false;
			}
			if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
			{
				return false;
			}
			if (MediaFoundationInterop.Failed(buffer.Lock(out var buffer2, out var maxLength, out var currentLength)) || buffer2 == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				byte[] array = Nv12FrameConverter.ConvertToBgra(buffer2, currentLength, _width, _height, out maxLength);
				if (array == null)
				{
					return false;
				}
				bgraBytes = array;
				return true;
			}
			finally
			{
				buffer.Unlock();
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(buffer);
		}
	}

	private TextureNativeFrameLease? TryCreateFrameLease(IMFSample sample, long frameNumber)
	{
		IMFMediaBuffer buffer = null;
		PooledFrameBuffer pooledFrameBuffer = null;
		try
		{
			if (MediaFoundationInterop.Failed(sample.GetBufferByIndex(0, out buffer)) || buffer == null)
			{
				return null;
			}
			pooledFrameBuffer = TryCreateNv12Preview(buffer, out var nv12Stride);
			IMFDXGIBuffer iMFDXGIBuffer = QueryDxgiBuffer(buffer);
			if (iMFDXGIBuffer == null)
			{
				if (pooledFrameBuffer == null)
				{
					return null;
				}
				TextureNativeFrameLease result = new TextureNativeFrameLease(IntPtr.Zero, 0, _width, _height, _fps, (_deviceManager?.ModeName ?? "DX12") + " NV12 upload", MediaFoundationInterop.FormatSubtype(_subtype), frameNumber, IntPtr.Zero, pooledFrameBuffer, nv12Stride);
				pooledFrameBuffer = null;
				return result;
			}
			try
			{
				ITextureNativeDeviceManager deviceManager = _deviceManager;
				if (deviceManager == null)
				{
					return null;
				}
				int subresource = 0;
				iMFDXGIBuffer.GetSubresourceIndex(out subresource);
				if (MediaFoundationInterop.Failed(iMFDXGIBuffer.GetResource(deviceManager.TextureResourceId, out var resource)) || resource == IntPtr.Zero)
				{
					return null;
				}
				nint num = IntPtr.Zero;
				try
				{
					string mediaSubtype = MediaFoundationInterop.FormatSubtype(_subtype);
					if (!TextureNativePreviewPolicy.ShouldPreferNv12UploadFallback(mediaSubtype, _width, _height, pooledFrameBuffer?.Bytes, nv12Stride))
					{
						num = TryCreateD3D11SharedTextureHandle(resource, deviceManager);
					}
					TextureNativeFrameLease result2 = new TextureNativeFrameLease(resource, subresource, _width, _height, _fps, deviceManager.ModeName, mediaSubtype, frameNumber, num, pooledFrameBuffer, nv12Stride);
					pooledFrameBuffer = null;
					return result2;
				}
				catch
				{
					pooledFrameBuffer?.Dispose();
					Marshal.Release(resource);
					TextureNativeFrameLease.CloseOwnedSharedTextureHandle(num);
					throw;
				}
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(iMFDXGIBuffer);
			}
		}
		finally
		{
			pooledFrameBuffer?.Dispose();
			MediaFoundationInterop.ReleaseComObject(buffer);
		}
	}

	private nint TryCreateD3D11SharedTextureHandle(nint sourceTexture, ITextureNativeDeviceManager deviceManager)
	{
		if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12 || !(deviceManager is Direct3D11DeviceManager direct3D11DeviceManager) || _d3d11SharedTextureBridgeUnavailable)
		{
			return IntPtr.Zero;
		}
		try
		{
			if (_d3d11SharedTextureBridge == null)
			{
				_d3d11SharedTextureBridge = direct3D11DeviceManager.CreateSharedTextureBridge(_width, _height);
			}
			if (_d3d11SharedTextureBridge.TryCopyToSharedHandle(sourceTexture, out nint duplicatedSharedHandle, out string failureReason))
			{
				return duplicatedSharedHandle;
			}
			if (!string.IsNullOrWhiteSpace(failureReason))
			{
				DisableD3D11SharedTextureBridge(failureReason);
			}
		}
		catch (Exception ex)
		{
			DisableD3D11SharedTextureBridge(ex.Message);
		}
		return IntPtr.Zero;
	}

	private void DisableD3D11SharedTextureBridge(string? reason)
	{
		if (!_d3d11SharedTextureBridgeUnavailable)
		{
			_d3d11SharedTextureBridgeUnavailable = true;
			_d3d11SharedTextureBridge?.Dispose();
			_d3d11SharedTextureBridge = null;
			ReportStatus("D3D11 shared texture bridge unavailable: " + (reason ?? "unknown failure"));
		}
	}

	private PooledFrameBuffer? TryCreateNv12Preview(IMFMediaBuffer buffer, out int nv12Stride)
	{
		nv12Stride = 0;
		if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
		{
			return null;
		}
		if (MediaFoundationInterop.Failed(buffer.Lock(out var buffer2, out var _, out var currentLength)) || buffer2 == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			int num = Math.Max(_width, currentLength * 2 / Math.Max(1, _height * 3));
			int num2 = num * _height + num * ((_height + 1) / 2);
			if (currentLength < num2)
			{
				return null;
			}
			PooledFrameBuffer pooledFrameBuffer = PooledFrameBuffer.Rent(num2);
			try
			{
				Marshal.Copy(buffer2, pooledFrameBuffer.Bytes, 0, num2);
				nv12Stride = num;
				return pooledFrameBuffer;
			}
			catch
			{
				pooledFrameBuffer.Dispose();
				throw;
			}
		}
		finally
		{
			buffer.Unlock();
		}
	}

	private static IMFDXGIBuffer? QueryDxgiBuffer(IMFMediaBuffer buffer)
	{
		nint num = IntPtr.Zero;
		nint ppv = IntPtr.Zero;
		try
		{
			num = Marshal.GetIUnknownForObject(buffer);
			if (Marshal.QueryInterface(num, typeof(IMFDXGIBuffer).GUID, out ppv) < 0 || ppv == IntPtr.Zero)
			{
				return null;
			}
			return (IMFDXGIBuffer)Marshal.GetObjectForIUnknown(ppv);
		}
		finally
		{
			if (ppv != IntPtr.Zero)
			{
				Marshal.Release(ppv);
			}
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
		}
	}

	private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(IMFSourceReader reader, CameraVideoMode? requestedMode)
	{
		int item = requestedMode?.Width ?? 1280;
		int item2 = requestedMode?.Height ?? 720;
		double item3 = requestedMode?.FramesPerSecond ?? 30.0;
		Guid value = MediaFoundationGuids.MFVideoFormat_NV12;
		if (MediaFoundationInterop.Failed(reader.GetCurrentMediaType(-4, out IMFMediaType mediaType)))
		{
			return (Width: item, Height: item2, Fps: item3, Subtype: value);
		}
		try
		{
			if (MediaFoundationInterop.TryGetFrameSize(mediaType, out var width, out var height))
			{
				item = width;
				item2 = height;
			}
			if (MediaFoundationInterop.TryGetFrameRate(mediaType, out var framesPerSecond))
			{
				item3 = framesPerSecond;
			}
			mediaType.GetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, out value);
			return (Width: item, Height: item2, Fps: item3, Subtype: value);
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}
}
