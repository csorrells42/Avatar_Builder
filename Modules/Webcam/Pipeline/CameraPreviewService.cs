using AvatarBuilder.Modules.Webcam.MediaFoundation;
using AvatarBuilder.Modules.Webcam.Ffmpeg;
using AvatarBuilder.Modules.Webcam.Common;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Webcam.Pipeline;

public sealed class CameraPreviewService : ICameraPreviewService
{
    private readonly MediaFoundationBitmapCameraPreviewService _mediaFoundation = new();
    private readonly FfmpegCameraPreviewService _ffmpeg = new();
    private ICameraPreviewService? _activeService;
    private int _maxOutputWidth = 960;
    private double _maxOutputFramesPerSecond = 15d;

    public CameraPreviewService()
    {
        _mediaFoundation.FrameAvailable += ForwardFrameAvailable;
        _mediaFoundation.CameraFrameAvailable += ForwardCameraFrameAvailable;
        _mediaFoundation.StatusChanged += ForwardStatusChanged;
        _ffmpeg.FrameAvailable += ForwardFrameAvailable;
        _ffmpeg.CameraFrameAvailable += ForwardCameraFrameAvailable;
        _ffmpeg.StatusChanged += ForwardStatusChanged;
    }

    public event EventHandler<BitmapSource>? FrameAvailable;

    public event EventHandler<CameraFrame>? CameraFrameAvailable;

    public event EventHandler<string>? StatusChanged;

    public bool IsAvailable => _mediaFoundation.IsAvailable || _ffmpeg.IsAvailable;

    public int MaxOutputWidth
    {
        get => _maxOutputWidth;
        set
        {
            _maxOutputWidth = value;
            _mediaFoundation.MaxOutputWidth = value;
            _ffmpeg.MaxOutputWidth = value;
        }
    }

    public double MaxOutputFramesPerSecond
    {
        get => _maxOutputFramesPerSecond;
        set
        {
            _maxOutputFramesPerSecond = value;
            _mediaFoundation.MaxOutputFramesPerSecond = value;
            _ffmpeg.MaxOutputFramesPerSecond = value;
        }
    }

    public async Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default)
    {
        Stop();
        ApplySettings();

        if (_mediaFoundation.IsAvailable)
        {
            StatusChanged?.Invoke(this, $"Trying Windows Media Foundation camera path for {camera.DisplayName}...");
            if (await _mediaFoundation.StartAsync(camera, mode, cancellationToken))
            {
                _activeService = _mediaFoundation;
                StatusChanged?.Invoke(this, "Camera active through Windows Media Foundation.");
                return true;
            }

            _mediaFoundation.Stop();
            StatusChanged?.Invoke(this, "Media Foundation camera path failed; trying bundled FFmpeg fallback.");
        }

        var directShowCamera = camera.DirectShowDeviceOrSelf();
        if (_ffmpeg.IsAvailable && await _ffmpeg.StartAsync(directShowCamera, mode, cancellationToken))
        {
            _activeService = _ffmpeg;
            StatusChanged?.Invoke(this, "Camera active through bundled FFmpeg fallback.");
            return true;
        }

        _activeService = null;
        return false;
    }

    public void Stop()
    {
        if (_activeService is not null)
        {
            _activeService.Stop();
            _activeService = null;
            return;
        }

        _mediaFoundation.Stop();
        _ffmpeg.Stop();
    }

    public void Dispose()
    {
        Stop();
        _mediaFoundation.FrameAvailable -= ForwardFrameAvailable;
        _mediaFoundation.CameraFrameAvailable -= ForwardCameraFrameAvailable;
        _mediaFoundation.StatusChanged -= ForwardStatusChanged;
        _ffmpeg.FrameAvailable -= ForwardFrameAvailable;
        _ffmpeg.CameraFrameAvailable -= ForwardCameraFrameAvailable;
        _ffmpeg.StatusChanged -= ForwardStatusChanged;
        _mediaFoundation.Dispose();
        _ffmpeg.Dispose();
    }

    private void ApplySettings()
    {
        _mediaFoundation.MaxOutputWidth = _maxOutputWidth;
        _mediaFoundation.MaxOutputFramesPerSecond = _maxOutputFramesPerSecond;
        _ffmpeg.MaxOutputWidth = _maxOutputWidth;
        _ffmpeg.MaxOutputFramesPerSecond = _maxOutputFramesPerSecond;
    }

    private void ForwardFrameAvailable(object? sender, BitmapSource frame)
    {
        FrameAvailable?.Invoke(this, frame);
    }

    private void ForwardCameraFrameAvailable(object? sender, CameraFrame frame)
    {
        CameraFrameAvailable?.Invoke(this, frame);
    }

    private void ForwardStatusChanged(object? sender, string status)
    {
        StatusChanged?.Invoke(this, status);
    }
}
