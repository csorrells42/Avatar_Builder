using System;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvApertureLandmarkTracker : IStatefulFaceLandmarkTracker, IFaceLandmarkTracker, IDisposable
{
	private readonly OpenCvFaceFeatureTracker _featureTracker = new OpenCvFaceFeatureTracker();

	public string Name => "OpenCV aperture fallback";

	public bool IsAvailable => _featureTracker.IsAvailable;

	public int MaxDetectionDimension
	{
		get
		{
			return _featureTracker.MaxDetectionDimension;
		}
		set
		{
			_featureTracker.MaxDetectionDimension = value;
		}
	}

	public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
	{
		if (!IsAvailable)
		{
			return FaceLandmarkTrackingResult.None;
		}
		FaceFeatureDetection faceFeatureDetection = _featureTracker.Detect(bitmap);
		FaceLandmarkFrame landmarkFrame = faceFeatureDetection.ToLandmarkFrame(capturedAtUtc);
		return new FaceLandmarkTrackingResult
		{
			BackendName = Name,
			BackendStatus = (faceFeatureDetection.HasFace ? "fallback aperture lock" : "fallback searching"),
			FeatureDetection = faceFeatureDetection,
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
