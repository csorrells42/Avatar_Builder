using AvatarBuilder.Modules.Vision.Common;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class DenseFaceMeshLandmarkTracker : IFaceLandmarkTracker
{
    private const bool InferenceImplementationCompiled = false;
    private readonly DenseFaceLandmarkModelInfo _modelInfo = DenseFaceLandmarkModelInfo.Load();

    public string Name => "Dense face mesh backend";

    public bool IsAvailable => InferenceImplementationCompiled && _modelInfo.CanRunInference;

    public int MaxDetectionDimension { get; set; } = 1280;

    public string Status => _modelInfo.Status;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        if (!IsAvailable)
        {
            return new FaceLandmarkTrackingResult
            {
                BackendName = Name,
                BackendStatus = CreateUnavailableStatus()
            };
        }

        return new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = "dense face mesh inference implementation pending"
        };
    }

    public void Dispose()
    {
    }

    private string CreateUnavailableStatus()
    {
        if (_modelInfo.CanRunInference && !InferenceImplementationCompiled)
        {
            return "dense landmark model/runtime marked ready, but C# inference bridge is not compiled";
        }

        return _modelInfo.Status;
    }
}
