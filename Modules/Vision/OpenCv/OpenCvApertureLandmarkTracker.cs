using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvApertureLandmarkTracker : IStatefulFaceLandmarkTracker
{
    private readonly OpenCvFaceFeatureTracker _featureTracker = new();

    public string Name => "OpenCV aperture fallback";

    public bool IsAvailable => _featureTracker.IsAvailable;

    public int MaxDetectionDimension
    {
        get => _featureTracker.MaxDetectionDimension;
        set => _featureTracker.MaxDetectionDimension = value;
    }

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        if (!IsAvailable)
        {
            return FaceLandmarkTrackingResult.None;
        }

        var detection = _featureTracker.Detect(bitmap);
        var landmarkFrame = detection.ToLandmarkFrame(capturedAtUtc);
        return new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = detection.HasFace ? "fallback aperture lock" : "fallback searching",
            FeatureDetection = detection,
            LandmarkFrame = landmarkFrame
        };
    }

    public void Dispose()
    {
        _featureTracker.Dispose();
    }

    public void Reset()
    {
        _featureTracker.Reset();
    }
}
