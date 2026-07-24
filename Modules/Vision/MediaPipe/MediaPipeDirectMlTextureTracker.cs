using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Diagnostics;
using AvatarBuilder.Modules.Webcam.DirectX12;
using Microsoft.ML.OnnxRuntime;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

/// <summary>
/// Runs the MediaPipe detector and 478-point landmarker in process from a
/// camera-owned D3D12 NV12 texture. Pixel conversion, crop, resize, normalize,
/// and both model inputs remain GPU-resident.
/// </summary>
internal sealed class MediaPipeDirectMlTextureTracker : IDisposable
{
	private const float MinimumFaceScore = 0.30f;

	private const float MinimumPresenceScore = 0.30f;

	private const float FaceCropScale = 1.50f;

	private const int LandmarkCount = 478;

	private static readonly string[] DetectorInputNames = ["input"];

	private static readonly string[] DetectorOutputNames =
		["regressors", "classificators"];

	private static readonly string[] LandmarkerInputNames = ["input_12"];

	private static readonly string[] LandmarkerOutputNames =
		["Identity", "Identity_1", "Identity_2"];

	private static readonly DetectorAnchor[] DetectorAnchors =
		GenerateDetectorAnchors();

	private readonly MediaPipeGpuTensorPreprocessor _preprocessor;

	private readonly InferenceSession _detector;

	private readonly InferenceSession _landmarker;

	private readonly OrtValue[] _detectorInputValues;

	private readonly OrtValue[] _landmarkerInputValues;

	private readonly RunOptions _runOptions = new();

	private MediaPipeGpuRoi? _trackedRoi;

	private bool _disposed;

	public string Name => "MediaPipe Face Landmarker (GPU texture DirectML)";

	public MediaPipeDirectMlTextureTracker(
		TextureNativeFrameLease firstFrame,
		string detectorModelPath,
		string landmarkerModelPath)
	{
		if (string.IsNullOrWhiteSpace(detectorModelPath)
			|| !File.Exists(detectorModelPath))
		{
			throw new FileNotFoundException(
				"MediaPipe DirectML detector model is missing.",
				detectorModelPath);
		}
		if (string.IsNullOrWhiteSpace(landmarkerModelPath)
			|| !File.Exists(landmarkerModelPath))
		{
			throw new FileNotFoundException(
				"MediaPipe DirectML landmark model is missing.",
				landmarkerModelPath);
		}

		_preprocessor = new MediaPipeGpuTensorPreprocessor(firstFrame);
		_detectorInputValues = [_preprocessor.DetectorTensor];
		_landmarkerInputValues = [_preprocessor.LandmarkTensor];
		InferenceSession? detector = null;
		InferenceSession? landmarker = null;
		try
		{
			detector = CreateSession(
				detectorModelPath,
				_preprocessor.DevicePointer,
				_preprocessor.CommandQueuePointer);
			landmarker = CreateSession(
				landmarkerModelPath,
				_preprocessor.DevicePointer,
				_preprocessor.CommandQueuePointer);
			_detector = detector;
			_landmarker = landmarker;
			detector = null;
			landmarker = null;
		}
		catch
		{
			landmarker?.Dispose();
			detector?.Dispose();
			_preprocessor.Dispose();
			throw;
		}
	}

	public bool CanProcess(TextureNativeFrameLease frame)
	{
		return !_disposed && _preprocessor.CanProcess(frame);
	}

	public FaceLandmarkTrackingResult Detect(
		TextureNativeFrameLease frame,
		DateTime capturedAtUtc)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		long totalStarted = Stopwatch.GetTimestamp();
		var stages = new DirectMlStageTimings();

		MediaPipeGpuRoi roi;
		float detectorScore = 0f;
		if (_trackedRoi is MediaPipeGpuRoi tracked)
		{
			roi = tracked;
			stages.DetectorMilliseconds = 0d;
		}
		else
		{
			long preprocessStarted = Stopwatch.GetTimestamp();
			MediaPipeDetectorTransform transform =
				_preprocessor.PreprocessDetector(frame);
			stages.DetectorGpuPreprocessMilliseconds =
				Stopwatch.GetElapsedTime(preprocessStarted).TotalMilliseconds;

			long detectorStarted = Stopwatch.GetTimestamp();
			using IDisposableReadOnlyCollection<OrtValue> detectorOutputs =
				RunDetector();
			stages.DetectorMilliseconds =
				Stopwatch.GetElapsedTime(detectorStarted).TotalMilliseconds;

			ReadOnlySpan<float> regressors =
				detectorOutputs[0].GetTensorDataAsSpan<float>();
			ReadOnlySpan<float> scores =
				detectorOutputs[1].GetTensorDataAsSpan<float>();
			if (!TryDecodeDetector(
				regressors,
				scores,
				transform,
				out DetectorCandidate candidate))
			{
				return CreateNoFaceResult(
					capturedAtUtc,
					frame,
					totalStarted,
					stages,
					"MediaPipe GPU texture DirectML searching");
			}

			detectorScore = candidate.Score;
			roi = RoiFromDetection(candidate);
		}

		long cropStarted = Stopwatch.GetTimestamp();
		_preprocessor.PreprocessLandmarks(frame, roi);
		stages.LandmarkGpuPreprocessMilliseconds =
			Stopwatch.GetElapsedTime(cropStarted).TotalMilliseconds;

		long landmarkStarted = Stopwatch.GetTimestamp();
		using IDisposableReadOnlyCollection<OrtValue> landmarkOutputs =
			RunLandmarker();
		stages.LandmarkerMilliseconds =
			Stopwatch.GetElapsedTime(landmarkStarted).TotalMilliseconds;

		ReadOnlySpan<float> rawLandmarks =
			landmarkOutputs[0].GetTensorDataAsSpan<float>();
		ReadOnlySpan<float> rawPresence =
			landmarkOutputs[1].GetTensorDataAsSpan<float>();
		float presence = rawPresence.IsEmpty ? 0f : Sigmoid(rawPresence[0]);
		if (presence < MinimumPresenceScore)
		{
			_trackedRoi = null;
			return CreateNoFaceResult(
				capturedAtUtc,
				frame,
				totalStarted,
				stages,
				"MediaPipe GPU texture DirectML searching");
		}

		long projectionStarted = Stopwatch.GetTimestamp();
		MediaPipeSidecarLandmark[] landmarks =
			ProjectLandmarks(rawLandmarks, roi, frame.Width, frame.Height);
		_trackedRoi = RoiFromLandmarks(landmarks, frame.Width, frame.Height);
		stages.ProjectMilliseconds =
			Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds;

		double totalMilliseconds =
			Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
		stages.DetectorScore = detectorScore;
		stages.Presence = presence;
		MediaPipeSidecarResponse response = new()
		{
			Ok = true,
			HasFace = true,
			Status = "MediaPipe GPU texture DirectML dense landmark lock",
			Landmarks = landmarks,
			LandmarkCount = landmarks.Length,
			TimingsMilliseconds = stages,
			Diagnostics = CreateDiagnostics(
				capturedAtUtc,
				frame,
				hasFace: true,
				totalMilliseconds,
				stages,
				"GPU texture direct")
		};
		return MediaPipeFaceLandmarkerMapper.ToTrackingResult(
			response,
			capturedAtUtc,
			Name,
			frame.Width,
			frame.Height);
	}

	public void Reset()
	{
		_trackedRoi = null;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_trackedRoi = null;
		_runOptions.Dispose();
		_landmarker.Dispose();
		_detector.Dispose();
		_preprocessor.Dispose();
	}

	private FaceLandmarkTrackingResult CreateNoFaceResult(
		DateTime capturedAtUtc,
		TextureNativeFrameLease frame,
		long totalStarted,
		IReadOnlyDictionary<string, double> stages,
		string status)
	{
		double totalMilliseconds =
			Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
		MediaPipeSidecarResponse response = new()
		{
			Ok = true,
			HasFace = false,
			Status = status,
			TimingsMilliseconds = stages,
			Diagnostics = CreateDiagnostics(
				capturedAtUtc,
				frame,
				hasFace: false,
				totalMilliseconds,
				stages,
				status)
		};
		return MediaPipeFaceLandmarkerMapper.ToTrackingResult(
			response,
			capturedAtUtc,
			Name,
			frame.Width,
			frame.Height);
	}

	private VisionPipelineDiagnostics CreateDiagnostics(
		DateTime capturedAtUtc,
		TextureNativeFrameLease frame,
		bool hasFace,
		double totalMilliseconds,
		IReadOnlyDictionary<string, double> stages,
		string status)
	{
		return new VisionPipelineDiagnostics
		{
			CapturedAtUtc = capturedAtUtc,
			Backend = Name,
			Mode = "DirectML D3D12 GPU tensor",
			SourceWidth = frame.Width,
			SourceHeight = frame.Height,
			InputWidth = MediaPipeGpuTensorPreprocessor.LandmarkSize,
			InputHeight = MediaPipeGpuTensorPreprocessor.LandmarkSize,
			EncodedPayloadBytes = 0,
			HasFace = hasFace,
			EndToEndMilliseconds = totalMilliseconds,
			SidecarStagesMilliseconds = stages,
			Status = status
		};
	}

	private IDisposableReadOnlyCollection<OrtValue> RunDetector()
	{
		try
		{
			return _detector.Run(
				_runOptions,
				DetectorInputNames,
				_detectorInputValues,
				DetectorOutputNames);
		}
		finally
		{
			_preprocessor.SignalDetectorInferenceSubmitted();
		}
	}

	private IDisposableReadOnlyCollection<OrtValue> RunLandmarker()
	{
		try
		{
			return _landmarker.Run(
				_runOptions,
				LandmarkerInputNames,
				_landmarkerInputValues,
				LandmarkerOutputNames);
		}
		finally
		{
			_preprocessor.SignalLandmarkInferenceSubmitted();
		}
	}

	private static InferenceSession CreateSession(
		string modelPath,
		nint d3d12Device,
		nint commandQueue)
	{
		using SessionOptions options = new()
		{
			EnableMemoryPattern = false,
			ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
			GraphOptimizationLevel =
				GraphOptimizationLevel.ORT_ENABLE_ALL
		};
		DmlGpuAllocationBridge.AppendExecutionProvider(
			options,
			d3d12Device,
			commandQueue);
		return new InferenceSession(modelPath, options);
	}

	private static bool TryDecodeDetector(
		ReadOnlySpan<float> regressors,
		ReadOnlySpan<float> rawScores,
		MediaPipeDetectorTransform transform,
		out DetectorCandidate best)
	{
		best = default;
		bool found = false;
		float bestScore = MinimumFaceScore;
		int candidateCount = Math.Min(
			DetectorAnchors.Length,
			Math.Min(rawScores.Length, regressors.Length / 16));
		for (int index = 0; index < candidateCount; index++)
		{
			float score = Sigmoid(Math.Clamp(rawScores[index], -80f, 80f));
			if (score < bestScore)
			{
				continue;
			}

			int rawOffset = index * 16;
			DetectorAnchor anchor = DetectorAnchors[index];
			float centerX =
				regressors[rawOffset] /
				MediaPipeGpuTensorPreprocessor.DetectorSize +
				anchor.X;
			float centerY =
				regressors[rawOffset + 1] /
				MediaPipeGpuTensorPreprocessor.DetectorSize +
				anchor.Y;
			float width =
				regressors[rawOffset + 2] /
				MediaPipeGpuTensorPreprocessor.DetectorSize;
			float height =
				regressors[rawOffset + 3] /
				MediaPipeGpuTensorPreprocessor.DetectorSize;
			(float minimumX, float minimumY) = DetectorPointToFrame(
				centerX - width * 0.5f,
				centerY - height * 0.5f,
				transform);
			(float maximumX, float maximumY) = DetectorPointToFrame(
				centerX + width * 0.5f,
				centerY + height * 0.5f,
				transform);
			(float firstEyeX, float firstEyeY) = DetectorPointToFrame(
				regressors[rawOffset + 4] /
					MediaPipeGpuTensorPreprocessor.DetectorSize +
					anchor.X,
				regressors[rawOffset + 5] /
					MediaPipeGpuTensorPreprocessor.DetectorSize +
					anchor.Y,
				transform);
			(float secondEyeX, float secondEyeY) = DetectorPointToFrame(
				regressors[rawOffset + 6] /
					MediaPipeGpuTensorPreprocessor.DetectorSize +
					anchor.X,
				regressors[rawOffset + 7] /
					MediaPipeGpuTensorPreprocessor.DetectorSize +
					anchor.Y,
				transform);

			bestScore = score;
			best = new DetectorCandidate(
				score,
				minimumX,
				minimumY,
				maximumX,
				maximumY,
				new DetectorPoint(firstEyeX, firstEyeY),
				new DetectorPoint(secondEyeX, secondEyeY));
			found = true;
		}
		return found;
	}

	private static (float X, float Y) DetectorPointToFrame(
		float normalizedX,
		float normalizedY,
		MediaPipeDetectorTransform transform)
	{
		float detectorX =
			normalizedX * MediaPipeGpuTensorPreprocessor.DetectorSize;
		float detectorY =
			normalizedY * MediaPipeGpuTensorPreprocessor.DetectorSize;
		return (
			(detectorX - transform.Left) / transform.Scale,
			(detectorY - transform.Top) / transform.Scale);
	}

	private static MediaPipeGpuRoi RoiFromDetection(
		DetectorCandidate candidate)
	{
		DetectorPoint firstEye = candidate.FirstEye;
		DetectorPoint secondEye = candidate.SecondEye;
		DetectorPoint leftEye =
			firstEye.X <= secondEye.X ? firstEye : secondEye;
		DetectorPoint rightEye =
			firstEye.X <= secondEye.X ? secondEye : firstEye;
		float angle = MathF.Atan2(
			rightEye.Y - leftEye.Y,
			rightEye.X - leftEye.X);
		return new MediaPipeGpuRoi(
			(candidate.MinimumX + candidate.MaximumX) * 0.5f,
			(candidate.MinimumY + candidate.MaximumY) * 0.5f,
			Math.Max(
				candidate.MaximumX - candidate.MinimumX,
				candidate.MaximumY - candidate.MinimumY) * FaceCropScale,
			angle);
	}

	private static MediaPipeSidecarLandmark[] ProjectLandmarks(
		ReadOnlySpan<float> rawLandmarks,
		MediaPipeGpuRoi roi,
		int frameWidth,
		int frameHeight)
	{
		if (rawLandmarks.Length < LandmarkCount * 3)
		{
			throw new InvalidOperationException(
				$"Landmark model returned {rawLandmarks.Length / 3} points; " +
				$"{LandmarkCount} are required.");
		}

		float cosine = MathF.Cos(roi.Angle);
		float sine = MathF.Sin(roi.Angle);
		float depthScale =
			roi.Size /
			(MediaPipeGpuTensorPreprocessor.LandmarkSize * frameWidth);
		MediaPipeSidecarLandmark[] landmarks =
			new MediaPipeSidecarLandmark[LandmarkCount];
		for (int index = 0; index < landmarks.Length; index++)
		{
			int offset = index * 3;
			float localX =
				(rawLandmarks[offset] /
					(MediaPipeGpuTensorPreprocessor.LandmarkSize - 1f) -
					0.5f) * roi.Size;
			float localY =
				(rawLandmarks[offset + 1] /
					(MediaPipeGpuTensorPreprocessor.LandmarkSize - 1f) -
					0.5f) * roi.Size;
			float frameX =
				roi.CenterX + cosine * localX - sine * localY;
			float frameY =
				roi.CenterY + sine * localX + cosine * localY;
			landmarks[index] = new MediaPipeSidecarLandmark(
				frameX / frameWidth,
				frameY / frameHeight,
				rawLandmarks[offset + 2] * depthScale);
		}
		return landmarks;
	}

	private static MediaPipeGpuRoi RoiFromLandmarks(
		IReadOnlyList<MediaPipeSidecarLandmark> landmarks,
		int frameWidth,
		int frameHeight)
	{
		float minimumX = float.MaxValue;
		float maximumX = float.MinValue;
		float minimumY = float.MaxValue;
		float maximumY = float.MinValue;
		for (int index = 0; index < landmarks.Count; index++)
		{
			float x = (float)landmarks[index].X * frameWidth;
			float y = (float)landmarks[index].Y * frameHeight;
			minimumX = Math.Min(minimumX, x);
			maximumX = Math.Max(maximumX, x);
			minimumY = Math.Min(minimumY, y);
			maximumY = Math.Max(maximumY, y);
		}

		MediaPipeSidecarLandmark leftEye = landmarks[33];
		MediaPipeSidecarLandmark rightEye = landmarks[263];
		float angle = MathF.Atan2(
			(float)(rightEye.Y - leftEye.Y) * frameHeight,
			(float)(rightEye.X - leftEye.X) * frameWidth);
		return new MediaPipeGpuRoi(
			(minimumX + maximumX) * 0.5f,
			(minimumY + maximumY) * 0.5f,
			Math.Max(maximumX - minimumX, maximumY - minimumY) *
				FaceCropScale,
			angle);
	}

	private static DetectorAnchor[] GenerateDetectorAnchors()
	{
		int[] strides = [8, 16, 16, 16];
		const float minimumScale = 0.1484375f;
		const float maximumScale = 0.75f;
		var anchors = new List<DetectorAnchor>(896);
		int layer = 0;
		while (layer < strides.Length)
		{
			int sameStrideEnd = layer;
			while (sameStrideEnd < strides.Length
				&& strides[sameStrideEnd] == strides[layer])
			{
				sameStrideEnd++;
			}

			var scales = new List<float>();
			for (int currentLayer = layer;
				currentLayer < sameStrideEnd;
				currentLayer++)
			{
				float scale = CalculateScale(
					currentLayer,
					strides.Length,
					minimumScale,
					maximumScale);
				scales.Add(scale);
				float nextScale = currentLayer < strides.Length - 1
					? CalculateScale(
						currentLayer + 1,
						strides.Length,
						minimumScale,
						maximumScale)
					: 1f;
				scales.Add(MathF.Sqrt(scale * nextScale));
			}

			int stride = strides[layer];
			int featureMapSize = (int)Math.Ceiling(
				(double)MediaPipeGpuTensorPreprocessor.DetectorSize /
				stride);
			for (int y = 0; y < featureMapSize; y++)
			{
				for (int x = 0; x < featureMapSize; x++)
				{
					for (int scaleIndex = 0;
						scaleIndex < scales.Count;
						scaleIndex++)
					{
						anchors.Add(new DetectorAnchor(
							(x + 0.5f) / featureMapSize,
							(y + 0.5f) / featureMapSize));
					}
				}
			}
			layer = sameStrideEnd;
		}

		if (anchors.Count != 896)
		{
			throw new InvalidOperationException(
				$"Detector generated {anchors.Count} anchors; 896 are required.");
		}
		return anchors.ToArray();
	}

	private static float CalculateScale(
		int layerIndex,
		int layerCount,
		float minimum,
		float maximum)
	{
		return layerCount == 1
			? (minimum + maximum) * 0.5f
			: minimum +
				(maximum - minimum) * layerIndex / (layerCount - 1);
	}

	private static float Sigmoid(float value)
	{
		if (value >= 0f)
		{
			float exponential = MathF.Exp(-Math.Min(value, 100f));
			return 1f / (1f + exponential);
		}
		float negativeExponential = MathF.Exp(Math.Max(value, -100f));
		return negativeExponential / (1f + negativeExponential);
	}

	private readonly record struct DetectorAnchor(float X, float Y);

	private readonly record struct DetectorPoint(float X, float Y);

	private readonly record struct DetectorCandidate(
		float Score,
		float MinimumX,
		float MinimumY,
		float MaximumX,
		float MaximumY,
		DetectorPoint FirstEye,
		DetectorPoint SecondEye);

	private sealed class DirectMlStageTimings
		: IReadOnlyDictionary<string, double>
	{
		public double? DetectorGpuPreprocessMilliseconds { get; set; }

		public double? DetectorMilliseconds { get; set; }

		public double? LandmarkGpuPreprocessMilliseconds { get; set; }

		public double? LandmarkerMilliseconds { get; set; }

		public double? ProjectMilliseconds { get; set; }

		public double? DetectorScore { get; set; }

		public double? Presence { get; set; }

		public int Count =>
			(DetectorGpuPreprocessMilliseconds.HasValue ? 1 : 0) +
			(DetectorMilliseconds.HasValue ? 1 : 0) +
			(LandmarkGpuPreprocessMilliseconds.HasValue ? 1 : 0) +
			(LandmarkerMilliseconds.HasValue ? 1 : 0) +
			(ProjectMilliseconds.HasValue ? 1 : 0) +
			(DetectorScore.HasValue ? 1 : 0) +
			(Presence.HasValue ? 1 : 0);

		public IEnumerable<string> Keys
		{
			get
			{
				foreach (KeyValuePair<string, double> pair in this)
				{
					yield return pair.Key;
				}
			}
		}

		public IEnumerable<double> Values
		{
			get
			{
				foreach (KeyValuePair<string, double> pair in this)
				{
					yield return pair.Value;
				}
			}
		}

		public double this[string key] =>
			TryGetValue(key, out double value)
				? value
				: throw new KeyNotFoundException(key);

		public bool ContainsKey(string key)
		{
			return TryGetValue(key, out _);
		}

		public bool TryGetValue(string key, out double value)
		{
			double? candidate = key switch
			{
				"detectorGpuPreprocess" =>
					DetectorGpuPreprocessMilliseconds,
				"detector" => DetectorMilliseconds,
				"landmarkGpuPreprocess" =>
					LandmarkGpuPreprocessMilliseconds,
				"landmarker" => LandmarkerMilliseconds,
				"project" => ProjectMilliseconds,
				"detectorScore" => DetectorScore,
				"presence" => Presence,
				_ => null
			};
			value = candidate.GetValueOrDefault();
			return candidate.HasValue;
		}

		public IEnumerator<KeyValuePair<string, double>> GetEnumerator()
		{
			if (DetectorGpuPreprocessMilliseconds is double detectorPreprocess)
			{
				yield return new(
					"detectorGpuPreprocess",
					detectorPreprocess);
			}
			if (DetectorMilliseconds is double detector)
			{
				yield return new("detector", detector);
			}
			if (LandmarkGpuPreprocessMilliseconds is double landmarkPreprocess)
			{
				yield return new(
					"landmarkGpuPreprocess",
					landmarkPreprocess);
			}
			if (LandmarkerMilliseconds is double landmarker)
			{
				yield return new("landmarker", landmarker);
			}
			if (ProjectMilliseconds is double project)
			{
				yield return new("project", project);
			}
			if (DetectorScore is double detectorScore)
			{
				yield return new("detectorScore", detectorScore);
			}
			if (Presence is double presence)
			{
				yield return new("presence", presence);
			}
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}
	}
}
