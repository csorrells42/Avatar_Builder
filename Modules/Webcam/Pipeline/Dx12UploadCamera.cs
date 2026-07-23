using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam.Pipeline;

public sealed class Dx12UploadCamera : IAsyncDisposable
{
	private readonly Dx12Camera.PreviewTarget _target;

	private readonly CameraPreviewService _capture = new CameraPreviewService();

	private Direct3D12PreviewHost? _previewHost;

	private long _frameNumber;

	private int _running;

	private int _disposed;

	public bool IsRunning => Volatile.Read(in _running) != 0;

	public event EventHandler<CameraFrame>? FrameAvailable;

	public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

	public event EventHandler<string>? StatusChanged;

	public Dx12UploadCamera(Dx12Camera.PreviewTarget target)
	{
		_target = target;
		_capture.BitmapFramesEnabled = false;
	}

	public async Task StartAsync(CameraDevice camera, CameraVideoMode mode, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(in _disposed) != 0, this);
		if (!_target.PreviewWindow.Dispatcher.CheckAccess())
		{
			throw new InvalidOperationException("DX12 upload camera must be started on the preview UI thread.");
		}
		CreatePreviewHost();
		_capture.MaxOutputWidth = Math.Clamp(mode.Width ?? 3840, 320, 3840);
		_capture.MaxOutputFramesPerSecond = Math.Clamp(mode.FramesPerSecond ?? 30.0, 1.0, 60.0);
		_capture.CameraFrameAvailable += CaptureFrameAvailable;
		_capture.StatusChanged += CaptureStatusChanged;
		try
		{
			if (!(await _capture.StartAsync(camera, mode, cancellationToken)))
			{
				throw new InvalidOperationException("Compatible camera capture could not open " + camera.Name + ".");
			}
			Volatile.Write(ref _running, 1);
			_target.PreviewImage?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
			_target.Placeholder?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
			NotifyStatusChanged("DirectShow capture is feeding the DX12 upload presenter for " + camera.Name + ".");
		}
		catch
		{
			await StopAsync();
			throw;
		}
	}

	public void UpdateTrackingOverlay(PreviewTrackingOverlay? overlay)
	{
		_previewHost?.UpdateTrackingOverlay(overlay);
	}

	public void ResumePreview()
	{
		_previewHost?.ResumeRendering();
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			await StopAsync();
			_capture.Dispose();
		}
	}

	private async Task StopAsync()
	{
		Volatile.Write(ref _running, 0);
		_capture.CameraFrameAvailable -= CaptureFrameAvailable;
		_capture.StatusChanged -= CaptureStatusChanged;
		await Task.Run((Action)_capture.Stop);
		Direct3D12PreviewHost host = Interlocked.Exchange(ref _previewHost, null);
		if (host == null)
		{
			return;
		}
		host.DiagnosticsChanged -= PreviewDiagnosticsChanged;
		host.StatusChanged -= PreviewStatusChanged;
		if (_target.PreviewWindow.Dispatcher.CheckAccess())
		{
			RemovePreviewHost(host);
			return;
		}
		await _target.PreviewWindow.Dispatcher.InvokeAsync(delegate
		{
			RemovePreviewHost(host);
		});
	}

	private void CreatePreviewHost()
	{
		Direct3D12PreviewHost direct3D12PreviewHost = new Direct3D12PreviewHost
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch
		};
		direct3D12PreviewHost.LimitRenderRate(0.0);
		direct3D12PreviewHost.DiagnosticsChanged += PreviewDiagnosticsChanged;
		direct3D12PreviewHost.StatusChanged += PreviewStatusChanged;
		_previewHost = direct3D12PreviewHost;
		int index = Math.Min(_target.HostInsertIndex, _target.PreviewWindow.Children.Count);
		_target.PreviewWindow.Children.Insert(index, direct3D12PreviewHost);
		direct3D12PreviewHost.Visibility = Visibility.Visible;
	}

	private void RemovePreviewHost(Direct3D12PreviewHost host)
	{
		_target.PreviewWindow.Children.Remove(host);
		host.Dispose();
	}

	private void CaptureFrameAvailable(object? sender, CameraFrame frame)
	{
		if (IsRunning && Volatile.Read(in _disposed) == 0)
		{
			long frameNumber = Interlocked.Increment(ref _frameNumber);
			_previewHost?.RenderBgraFrame(frame, frameNumber);
			NotifyFrameAvailable(frame);
		}
	}

	private void CaptureStatusChanged(object? sender, string status)
	{
		NotifyStatusChanged(status);
	}

	private void PreviewStatusChanged(object? sender, string status)
	{
		NotifyStatusChanged(status);
	}

	private void PreviewDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
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
				NotifyStatusChanged("DX12 upload diagnostics observer failed: " + ex.Message);
			}
		}
	}

	private void NotifyFrameAvailable(CameraFrame frame)
	{
		EventHandler<CameraFrame> eventHandler = this.FrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<CameraFrame>)obj)(this, frame);
			}
			catch (Exception ex)
			{
				NotifyStatusChanged("DX12 upload frame observer failed: " + ex.Message);
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
}
