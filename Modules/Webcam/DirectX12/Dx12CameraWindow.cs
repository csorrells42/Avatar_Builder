using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

/// <summary>
/// A reusable, camera-first DX12 window. Showing the window starts the selected
/// camera, and closing it releases the camera session.
/// </summary>
public sealed class Dx12CameraWindow : Window, IDisposable
{
	private readonly Grid _previewSurface;

	private readonly Dx12CameraOptions _options;

	private Dx12Camera? _cameraSession;

	private bool _disposed;

	public Dx12Camera? CameraSession => _cameraSession;

	public Dx12CameraWindow(
		Dx12CameraOptions? options = null,
		string title = "Avatar Builder Camera Monitor")
	{
		_options = options ?? new Dx12CameraOptions();
		Title = string.IsNullOrWhiteSpace(title)
			? "Avatar Builder Camera Monitor"
			: title.Trim();
		Width = 720;
		Height = 460;
		MinWidth = 320;
		MinHeight = 240;
		Background = Brushes.Black;
		_previewSurface = new Grid
		{
			Background = Brushes.Black
		};
		Content = _previewSurface;
		Loaded += WindowLoaded;
		Closed += WindowClosed;
	}

	public Dx12Camera StartCamera()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_cameraSession != null)
		{
			return _cameraSession;
		}
		_cameraSession = Dx12Camera.Start(
			new Dx12Camera.PreviewTarget(
				_previewSurface,
				name: Title),
			_options);
		return _cameraSession;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Loaded -= WindowLoaded;
		Closed -= WindowClosed;
		Dx12Camera? camera = _cameraSession;
		_cameraSession = null;
		camera?.Dispose();
	}

	private void WindowLoaded(object sender, RoutedEventArgs e)
	{
		StartCamera();
	}

	private void WindowClosed(object? sender, EventArgs e)
	{
		Dispose();
	}
}
