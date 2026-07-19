using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Media;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class MediaPipeFaceLandmarkerSidecarTracker : IStatefulFaceLandmarkTracker, IFaceLandmarkCropRefiner
{
    private readonly DenseFaceLandmarkModelInfo _modelInfo = DenseFaceLandmarkModelInfo.Load();
    private readonly Lazy<MediaPipeSidecarPythonEnvironment> _environment;
    private MediaPipeFaceLandmarkerSidecarClient? _client;
    private string _lastStatus = "MediaPipe sidecar not checked";

    public MediaPipeFaceLandmarkerSidecarTracker()
    {
        _environment = new Lazy<MediaPipeSidecarPythonEnvironment>(() =>
        {
            var environment = MediaPipeSidecarPythonEnvironment.Detect(_modelInfo);
            _lastStatus = environment.Status;
            return environment;
        });
    }

    public string Name => "MediaPipe Face Landmarker sidecar";

    public bool IsAvailable => _environment.Value.IsReady;

    public int MaxDetectionDimension { get; set; } = 1920;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        if (!IsAvailable)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = _lastStatus
            };
        }

        _client ??= new MediaPipeFaceLandmarkerSidecarClient(_environment.Value);
        var response = _client.Analyze(bitmap, capturedAtUtc);
        _lastStatus = string.IsNullOrWhiteSpace(response.Status)
            ? _client.Status
            : response.Status;
        return MediaPipeFaceLandmarkerMapper.ToTrackingResult(response, capturedAtUtc, Name);
    }

    public FaceLandmarkTrackingResult DetectFaceCrop(
        BitmapSource bitmap,
        Rect normalizedFaceHint,
        DateTime capturedAtUtc)
    {
        if (!IsAvailable)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = _lastStatus
            };
        }

        if (!TryCreateFaceCrop(bitmap, normalizedFaceHint, out var cropBitmap, out var normalizedCrop))
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = "MediaPipe sidecar crop refinement skipped; face hint was outside the frame."
            };
        }

        _client ??= new MediaPipeFaceLandmarkerSidecarClient(_environment.Value);
        var response = _client.Analyze(cropBitmap, capturedAtUtc);
        _lastStatus = string.IsNullOrWhiteSpace(response.Status)
            ? _client.Status
            : response.Status;
        var cropResult = MediaPipeFaceLandmarkerMapper.ToTrackingResult(response, capturedAtUtc, Name);
        return FaceLandmarkCropMapper.MapToFrame(
            cropResult,
            normalizedCrop,
            $"crop refined from face hint {FormatCrop(normalizedCrop)}");
    }

    public void Reset()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    private static bool TryCreateFaceCrop(
        BitmapSource bitmap,
        Rect normalizedFaceHint,
        out BitmapSource cropBitmap,
        out Rect normalizedCrop)
    {
        cropBitmap = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
        normalizedCrop = Rect.Empty;
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || normalizedFaceHint.Width <= 0d || normalizedFaceHint.Height <= 0d)
        {
            return false;
        }

        var expanded = ExpandAndClamp(normalizedFaceHint, horizontalPadding: 0.45d, verticalPadding: 0.60d);
        var x = (int)Math.Floor(expanded.Left * bitmap.PixelWidth);
        var y = (int)Math.Floor(expanded.Top * bitmap.PixelHeight);
        var right = (int)Math.Ceiling(expanded.Right * bitmap.PixelWidth);
        var bottom = (int)Math.Ceiling(expanded.Bottom * bitmap.PixelHeight);
        x = Math.Clamp(x, 0, Math.Max(0, bitmap.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, bitmap.PixelHeight - 1));
        right = Math.Clamp(right, x + 1, bitmap.PixelWidth);
        bottom = Math.Clamp(bottom, y + 1, bitmap.PixelHeight);
        var pixelRect = new Int32Rect(x, y, right - x, bottom - y);
        if (pixelRect.Width < 24 || pixelRect.Height < 24)
        {
            return false;
        }

        normalizedCrop = new Rect(
            pixelRect.X / (double)bitmap.PixelWidth,
            pixelRect.Y / (double)bitmap.PixelHeight,
            pixelRect.Width / (double)bitmap.PixelWidth,
            pixelRect.Height / (double)bitmap.PixelHeight);
        BitmapSource crop = new CroppedBitmap(bitmap, pixelRect);
        var smallestSide = Math.Min(crop.PixelWidth, crop.PixelHeight);
        if (smallestSide > 0 && smallestSide < 320)
        {
            var scale = Math.Min(4d, 320d / smallestSide);
            crop = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
        }

        if (crop.CanFreeze)
        {
            crop.Freeze();
        }

        cropBitmap = crop;
        return true;
    }

    private static Rect ExpandAndClamp(Rect rect, double horizontalPadding, double verticalPadding)
    {
        var padX = rect.Width * horizontalPadding;
        var padY = rect.Height * verticalPadding;
        var left = Math.Clamp(rect.Left - padX, 0d, 1d);
        var top = Math.Clamp(rect.Top - padY, 0d, 1d);
        var right = Math.Clamp(rect.Right + padX, 0d, 1d);
        var bottom = Math.Clamp(rect.Bottom + padY, 0d, 1d);
        return new Rect(left, top, Math.Max(0d, right - left), Math.Max(0d, bottom - top));
    }

    private static string FormatCrop(Rect rect)
    {
        return $"{rect.Left:0.###},{rect.Top:0.###},{rect.Width:0.###}x{rect.Height:0.###}";
    }
}
