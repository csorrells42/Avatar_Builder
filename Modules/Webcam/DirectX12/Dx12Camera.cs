using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class Dx12Camera : IDisposable
{
	public sealed class PreviewTarget
	{
		public Panel PreviewWindow { get; }

		public UIElement? PreviewImage { get; }

		public UIElement? Placeholder { get; }

		public TextBlock? StatusText { get; }

		public int HostInsertIndex { get; }

		public string Name { get; }

		public PreviewTarget(Panel previewWindow, UIElement? previewImage = null, UIElement? placeholder = null, TextBlock? statusText = null, int hostInsertIndex = 0, string name = "Camera")
		{
			PreviewWindow = previewWindow;
			PreviewImage = previewImage;
			Placeholder = placeholder;
			StatusText = statusText;
			HostInsertIndex = Math.Max(0, hostInsertIndex);
			Name = name;
		}
	}

	private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5L);

	private readonly object _stateLock = new object();

	private readonly CameraDevice _camera;

	private readonly CameraVideoMode _mode;

	private readonly PreviewTarget _target;

	private readonly Dispatcher _dispatcher;

	private readonly LatestTextureFrameWorker _textureObserverWorker;

	private TextureNativeCameraStream? _stream;

	private Direct3D12PreviewHost? _previewHost;

	private bool _textureFrameLeaseActive;

	private bool _denoiseEnabled;

	private double _denoiseStrength = 2.0;

	private double _maxPreviewRenderFramesPerSecond;

	private DateTime _lastPreviewRenderFrameUtc = DateTime.MinValue;

	private VideoFrameColorSettings _colorSettings = VideoFrameColorSettings.Off;

	private string _recordingMode = "not recording";

	private bool _disposed;

	public bool IsRecording => _stream?.IsRecording ?? false;

	public string RecordingMode => _recordingMode;

	public long FramesRead => _stream?.FramesRead ?? 0L;

	public long FramesDroppedWhileProcessingBusy => _stream?.FramesDroppedWhileProcessingBusy ?? 0L;

	public long LastSourceFrameTimestamp => _stream?.LastSourceFrameTimestamp ?? 0L;

	public long LastPresentedFrameTimestamp =>
		_previewHost?.LastRenderedFrameTimestamp ?? 0L;

	public event EventHandler<TextureNativeFrameInfo>? FrameAvailable;

	public event EventHandler<TextureNativeFrameLease>? TextureFrameAvailable;

	public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

	public event EventHandler<string>? StatusChanged;

	private Dx12Camera(CameraDevice camera, CameraVideoMode? mode, PreviewTarget target)
	{
		if (!target.PreviewWindow.Dispatcher.CheckAccess())
		{
			throw new InvalidOperationException("Dx12Camera must be constructed on the preview window's UI thread.");
		}
		_camera = camera;
		_mode = mode ?? CameraVideoMode.Auto;
		_target = target;
		_dispatcher = target.PreviewWindow.Dispatcher;
		_textureObserverWorker = new LatestTextureFrameWorker(
			"Avatar Builder camera observers",
			acceptedFrame =>
			{
				if (!_disposed)
				{
					Direct3D12PreviewHost? previewHost = _previewHost;
					if (previewHost == null
						|| previewHost.WaitForTextureFrameRead(
							acceptedFrame.FrameNumber))
					{
						NotifyTextureFrameAvailable(acceptedFrame);
					}
					else
					{
						NotifyStatusChanged(
							"DX12 preview GPU stopped completing frames; " +
							"the analysis frame was discarded for safe recovery.");
					}
				}
			},
			failureHandler: ex => NotifyStatusChanged(
				"DX12 camera observer recovered after an isolated failure: " +
				ex.Message));
		try
		{
			Initialize();
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	~Dx12Camera()
	{
		Dispose(disposing: false);
	}

	public static Dx12Camera Start(PreviewTarget target, Dx12CameraOptions? options)
	{
		Dx12Camera dx12Camera = OpenTextureNative(options?.Camera ?? CameraSourceSelection.RequireDefaultCamera(), options?.Mode ?? CameraVideoMode.Auto, target, options?.DenoiseEnabled ?? false, options?.DenoiseStrength ?? 2.0);
		dx12Camera.ColorPolish(options?.ColorSettings ?? VideoFrameColorSettings.Off);
		dx12Camera.LimitPreviewRenderRate(options?.MaxPreviewRenderFramesPerSecond ?? 0.0);
		dx12Camera.AttachStartupHandlers(options);
		return dx12Camera;
	}

	internal static Dx12Camera OpenTextureNative(CameraDevice camera, CameraVideoMode mode, PreviewTarget target, bool denoiseEnabled, double denoiseStrength)
	{
		if (!target.PreviewWindow.Dispatcher.CheckAccess())
		{
			return target.PreviewWindow.Dispatcher.Invoke(() => OpenTextureNative(camera, mode, target, denoiseEnabled, denoiseStrength));
		}
		Dx12Camera dx12Camera = new Dx12Camera(camera, mode, target);
		dx12Camera.Denoise(denoiseEnabled, denoiseStrength);
		return dx12Camera;
	}

	private void AttachStartupHandlers(Dx12CameraOptions? options)
	{
		if (options?.FrameAvailable != null)
		{
			FrameAvailable += options.FrameAvailable;
		}
		if (options?.TextureFrameAvailable != null)
		{
			TextureFrameAvailable += options.TextureFrameAvailable;
		}
		if (options?.DiagnosticsChanged != null)
		{
			DiagnosticsChanged += options.DiagnosticsChanged;
		}
		if (options?.StatusChanged != null)
		{
			StatusChanged += options.StatusChanged;
		}
	}

	public void Denoise(bool enabled, double strength)
	{
		_denoiseEnabled = enabled;
		_denoiseStrength = Math.Clamp(strength, 0.5, 5.0);
	}

	public void LimitPreviewRenderRate(double maxFramesPerSecond)
	{
		_maxPreviewRenderFramesPerSecond = ((maxFramesPerSecond <= 0.0) ? 0.0 : Math.Clamp(maxFramesPerSecond, 1.0, 120.0));
		_lastPreviewRenderFrameUtc = DateTime.MinValue;
	}

	public void UpdateTrackingOverlay(PreviewTrackingOverlay? overlay)
	{
		_previewHost?.UpdateTrackingOverlay(overlay);
	}

	public void ResumePreview()
	{
		if (!_disposed)
		{
			_lastPreviewRenderFrameUtc = DateTime.MinValue;
			_previewHost?.ResumeRendering();
		}
	}

	public void SuspendPreview()
	{
		if (!_disposed)
		{
			_previewHost?.SuspendRendering();
		}
	}

	public void ColorPolish(VideoFrameColorSettings settings)
	{
		_colorSettings = settings;
	}

	public bool WriteMP4(string path)
	{
		return WriteMP4(path, _denoiseEnabled, _denoiseEnabled, _denoiseStrength);
	}

	public bool WriteMP4(string path, bool processedOutputEnabled, bool denoiseEnabled, double denoiseStrength)
	{
		return WriteMP4(path, new TextureNativeRecordingOptions(processedOutputEnabled, denoiseEnabled, denoiseStrength, _colorSettings));
	}

	public bool WriteMP4(string path, TextureNativeRecordingOptions options)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("An MP4 file path is required.", "path");
		}
		return StartRecording(path, options);
	}

	public bool StartRecording(string path, bool processedOutputEnabled = false, bool denoiseEnabled = false, double denoiseStrength = 2.0)
	{
		return StartRecording(path, new TextureNativeRecordingOptions(processedOutputEnabled, denoiseEnabled, denoiseStrength, _colorSettings));
	}

	public bool StartRecording(string path, TextureNativeRecordingOptions options)
	{
		bool num = (_stream ?? throw new InvalidOperationException("DX12 camera stream is not initialized.")).StartRecording(path, options);
		if (num)
		{
			SetPreviewRecordingMode(FormatTextureRecordingMode(options));
		}
		return num;
	}

	public void PauseRecording()
	{
		_stream?.PauseRecording();
		if (IsRecording)
		{
			_previewHost?.SetRecordingMode("recording paused");
		}
	}

	public void ResumeRecording()
	{
		_stream?.ResumeRecording();
		if (IsRecording)
		{
			_previewHost?.SetRecordingMode(_recordingMode);
		}
	}

	public TextureNativeRecordingResult? StopRecording()
	{
		TextureNativeRecordingResult? result = _stream?.StopRecording();
		SetPreviewRecordingMode("not recording");
		return result;
	}

	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	public void DetachPreviewHost()
	{
		if (_dispatcher.CheckAccess())
		{
			HidePreviewHost();
			return;
		}
		_dispatcher.Invoke(HidePreviewHost);
	}

	private void Initialize()
	{
		_target.PreviewImage?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
		_target.Placeholder?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
		_target.StatusText?.SetCurrentValue(TextBlock.TextProperty, "DX12 camera starting for " + _target.Name + ".");
		TextureNativeCameraStream textureNativeCameraStream = (_stream = new TextureNativeCameraStream(_camera, _mode, startImmediately: false));
		textureNativeCameraStream.FrameAvailable += StreamFrameAvailable;
		textureNativeCameraStream.TextureFrameAvailable += StreamTextureFrameAvailable;
		textureNativeCameraStream.StatusChanged += StreamStatusChanged;
		textureNativeCameraStream.Start();
		ShowPreviewHost(textureNativeCameraStream.DuplicateNativeD3D12Device());
		if (!WaitForFirstFrame(textureNativeCameraStream))
		{
			throw new TimeoutException($"No DX12 texture frames arrived within {FirstFrameTimeout.TotalSeconds:0.#} seconds ({textureNativeCameraStream.DeviceMode}, {textureNativeCameraStream.Width}x{textureNativeCameraStream.Height}@{textureNativeCameraStream.FramesPerSecond:0.###}, {textureNativeCameraStream.MediaSubtype}).");
		}
	}

	private static bool WaitForFirstFrame(TextureNativeCameraStream stream)
	{
		DateTimeOffset dateTimeOffset = DateTimeOffset.UtcNow + FirstFrameTimeout;
		while (DateTimeOffset.UtcNow < dateTimeOffset)
		{
			if (stream.FramesRead > 0)
			{
				return true;
			}
			Thread.Sleep(25);
		}
		return stream.FramesRead > 0;
	}

	private void ShowPreviewHost(nint nativeD3D12Device)
	{
		try
		{
			Direct3D12PreviewHost direct3D12PreviewHost = new Direct3D12PreviewHost(nativeD3D12Device)
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch
			};
			nativeD3D12Device = IntPtr.Zero;
			direct3D12PreviewHost.StatusChanged += PreviewHostStatusChanged;
			direct3D12PreviewHost.DiagnosticsChanged += PreviewHostDiagnosticsChanged;
			direct3D12PreviewHost.SetRecordingMode(_recordingMode);
			_previewHost = direct3D12PreviewHost;
			int index = Math.Min(_target.HostInsertIndex, _target.PreviewWindow.Children.Count);
			_target.PreviewWindow.Children.Insert(index, direct3D12PreviewHost);
			direct3D12PreviewHost.Visibility = Visibility.Visible;
		}
		finally
		{
			if (nativeD3D12Device != IntPtr.Zero)
			{
				Marshal.Release(nativeD3D12Device);
			}
		}
	}

	private void HidePreviewHost()
	{
		Direct3D12PreviewHost previewHost = _previewHost;
		if (previewHost != null)
		{
			_previewHost = null;
			previewHost.StatusChanged -= PreviewHostStatusChanged;
			previewHost.DiagnosticsChanged -= PreviewHostDiagnosticsChanged;
			_target.PreviewWindow.Children.Remove(previewHost);
			previewHost.Dispose();
		}
	}

	private void StreamFrameAvailable(object? sender, TextureNativeFrameInfo frame)
	{
		NotifyFrameAvailable(frame);
	}

	private void StreamTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
	{
		_textureFrameLeaseActive = frame.IsValid;
		if (_disposed)
		{
			return;
		}

		try
		{
			// Preview submission is the only priority work on this lane. The host
			// performs an O(1), reference-counted handoff to its dedicated render
			// thread and drops immediately whenever that thread is already busy.
			if (ShouldAcceptPreviewRenderFrame())
			{
				_previewHost?.RenderTextureFrame(frame, _denoiseEnabled, _denoiseStrength, _colorSettings);
			}

			// Optional observers run on their own dedicated, no-backlog lane.
			// The display lane performs only this O(1) reference handoff.
			if (!_disposed)
			{
				if (!_textureObserverWorker.TryAcceptTexture(frame))
				{
					_previewHost?.DiscardTextureFrameRead(
						frame.FrameNumber);
				}
			}
		}
		catch (Exception ex)
		{
			NotifyStatusChanged("DX12 preview submission failed: " + ex.Message);
		}
	}

	private bool ShouldAcceptPreviewRenderFrame()
	{
		double maxPreviewRenderFramesPerSecond = _maxPreviewRenderFramesPerSecond;
		if (maxPreviewRenderFramesPerSecond <= 0.0)
		{
			return true;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow - _lastPreviewRenderFrameUtc < TimeSpan.FromSeconds(1.0 / maxPreviewRenderFramesPerSecond))
		{
			return false;
		}
		_lastPreviewRenderFrameUtc = utcNow;
		return true;
	}

	private void StreamStatusChanged(object? sender, string status)
	{
		NotifyStatusChanged(status);
	}

	private void PreviewHostStatusChanged(object? sender, string status)
	{
		NotifyStatusChanged(status);
	}

	private void PreviewHostDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
	{
		EventHandler<Direct3D12PreviewDiagnostics> eventHandler = this.DiagnosticsChanged;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<Direct3D12PreviewDiagnostics>)obj)(this, diagnostics);
			}
			catch (Exception ex)
			{
				NotifyStatusChanged("DX12 diagnostics observer failed: " + ex.Message);
			}
		}
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
			catch (Exception ex)
			{
				NotifyStatusChanged("DX12 frame observer failed: " + ex.Message);
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
			catch (Exception ex)
			{
				NotifyStatusChanged("DX12 texture observer failed: " + ex.Message);
			}
		}
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

	private void SetPreviewRecordingMode(string recordingMode)
	{
		_recordingMode = (string.IsNullOrWhiteSpace(recordingMode) ? "not recording" : recordingMode);
		_previewHost?.SetRecordingMode(_recordingMode);
	}

	private static string FormatTextureRecordingMode(TextureNativeRecordingOptions options)
	{
		if (options.ProcessedOutputEnabled)
		{
			if (!options.ColorSettings.HasVisibleAdjustments && !options.DenoiseEnabled)
			{
				return "recording processed texture bridge";
			}
			return "recording processed texture output";
		}
		return "recording raw texture-native samples";
	}

	private void Dispose(bool disposing)
	{
		lock (_stateLock)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
		}
		TextureNativeCameraStream stream = _stream;
		_stream = null;
		if (stream != null)
		{
			stream.FrameAvailable -= StreamFrameAvailable;
			stream.TextureFrameAvailable -= StreamTextureFrameAvailable;
			stream.StatusChanged -= StreamStatusChanged;
		}
		try
		{
			stream?.Dispose();
		}
		catch
		{
		}
		_textureObserverWorker.Dispose();
		_textureFrameLeaseActive = false;
		if (disposing
			&& _previewHost != null
			&& _dispatcher.CheckAccess())
		{
			HidePreviewHost();
		}
		else if (disposing && _previewHost != null)
		{
			try
			{
				_dispatcher.Invoke(HidePreviewHost);
			}
			catch
			{
			}
		}
	}
}
