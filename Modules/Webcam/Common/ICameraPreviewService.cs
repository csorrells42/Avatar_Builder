using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Webcam.Common;

public interface ICameraPreviewService : IDisposable
{
    event EventHandler<BitmapSource>? FrameAvailable;

    event EventHandler<CameraFrame>? CameraFrameAvailable;

    event EventHandler<string>? StatusChanged;

    bool IsAvailable { get; }

    int MaxOutputWidth { get; set; }

    double MaxOutputFramesPerSecond { get; set; }

    Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default);

    void Stop();
}
