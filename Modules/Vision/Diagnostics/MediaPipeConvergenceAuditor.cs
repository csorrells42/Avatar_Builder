using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class MediaPipeConvergenceAuditor : IDisposable
{
	private sealed record AuditFrameInput(int SessionGeneration, DateTime CapturedAtUtc, int FrameWidth, int FrameHeight, FaceLandmarkFrame RawFrame, FaceLandmarkFrame ReconstructedFrame);

	private sealed class LandmarkRunningStatistics(int index)
	{
		private double _meanX;

		private double _meanY;

		private double _meanZ;

		private double _m2;

		public int Index { get; } = index;

		public long Count { get; private set; }

		public void Add(MediaPipeCanonicalPoint point)
		{
			Count++;
			double num = point.X - _meanX;
			double num2 = point.Y - _meanY;
			double num3 = point.Z - _meanZ;
			_meanX += num / (double)Count;
			_meanY += num2 / (double)Count;
			_meanZ += num3 / (double)Count;
			_m2 += num * (point.X - _meanX) + num2 * (point.Y - _meanY) + num3 * (point.Z - _meanZ);
		}

		public MediaPipeLandmarkVariance ToSummary()
		{
			return new MediaPipeLandmarkVariance
			{
				Index = Index,
				SampleCount = Count,
				RmsDeviationPercent = ((Count < 2) ? 0.0 : (Math.Sqrt(Math.Max(0.0, _m2 / (double)(Count - 1))) * 100.0))
			};
		}

		public void Reset()
		{
			Count = 0L;
			_meanX = 0.0;
			_meanY = 0.0;
			_meanZ = 0.0;
			_m2 = 0.0;
		}
	}

	public const string HtmlFileName = "mediapipe_convergence_audit.html";

	public const string JsonFileName = "mediapipe_convergence_summary.json";

	public const string CsvFileName = "mediapipe_convergence_samples.csv";

	public const string RawLandmarkFileName = "mediapipe_raw_landmarks.mpaudit";

	public const string MarkerFileName = "mediapipe_convergence_markers.csv";

	private const int MaximumRetainedSamples = 7200;

	private const int MaximumRetainedMarkers = 200;

	private const double SampleIntervalSeconds = 1.0;

	private static readonly TimeSpan ReportWriteInterval = TimeSpan.FromSeconds(5L);

	private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
		WriteIndented = true
	};

	private static readonly int[] StableShellLandmarkIndices = new int[52]
	{
		10, 338, 297, 332, 284, 251, 389, 356, 454, 323,
		361, 288, 397, 365, 379, 378, 400, 377, 152, 148,
		176, 149, 150, 136, 172, 58, 132, 93, 234, 127,
		162, 21, 54, 103, 67, 109, 168, 6, 197, 195,
		5, 4, 1, 19, 94, 2, 50, 101, 205, 280,
		330, 425
	};

	private readonly object _sync = new object();

	private readonly List<MediaPipeConvergenceSample> _samples = new List<MediaPipeConvergenceSample>();

	private readonly List<MediaPipeConvergenceMarker> _markers = new List<MediaPipeConvergenceMarker>();

	private readonly LandmarkRunningStatistics[] _landmarkStatistics = CreateLandmarkStatistics(478);

	private Task? _workerTask;

	private MediaPipeCanonicalFace? _previousCanonicalFace;

	private IReadOnlyList<FaceMeshLandmarkPoint>? _previousRawLandmarks;

	private DateTime _sessionStartedAtUtc = DateTime.UtcNow;

	private DateTime _lastSampleAtUtc = DateTime.MinValue;

	private DateTime _lastReportWriteAtUtc = DateTime.MinValue;

	private string _sessionId = CreateSessionId();

	private string _sessionFolder = "";

	private string _configuredRoot = "";

	private string _sessionReason = "application startup";

	private long _totalFrames;

	private long _faceFrames;

	private long _missingFaceFrames;

	private long _auditFramesReplaced;

	private int _persistedSampleCount;

	private int _persistedMarkerCount;

	private int _sessionGeneration;

	private int _workerRunning;

	private int _disposed;

	private double? _previousHeadA;

	private double? _previousHeadB;

	private double? _previousHeadC;

	private double _intervalScreenMotionSum;

	private double _intervalCanonicalAllSum;

	private double _intervalCanonicalStableSum;

	private double _intervalPoseMotionSum;

	private double _intervalAppCorrectionSum;

	private int _intervalScreenMotionCount;

	private int _intervalCanonicalAllCount;

	private int _intervalCanonicalStableCount;

	private int _intervalPoseMotionCount;

	private int _intervalAppCorrectionCount;

	public MediaPipeConvergenceSummary CurrentSummary
	{
		get
		{
			lock (_sync)
			{
				return BuildSummaryLocked();
			}
		}
	}

	public void SetOutputRoot(string outputRoot)
	{
		if (string.IsNullOrWhiteSpace(outputRoot))
		{
			return;
		}
		lock (_sync)
		{
			_configuredRoot = Path.Combine(Path.GetFullPath(outputRoot), "Benchmarks", "MediaPipeConvergence");
			StartNewSessionLocked("data folder or avatar profile selected");
		}
	}

	public void StartNewSession(string reason)
	{
		lock (_sync)
		{
			StartNewSessionLocked(string.IsNullOrWhiteSpace(reason) ? "human-started controlled run" : reason.Trim());
		}
	}

	public void MarkEvent(string eventName, string detail = "")
	{
		if (Volatile.Read(in _disposed) != 0)
		{
			return;
		}
		lock (_sync)
		{
			_markers.Add(new MediaPipeConvergenceMarker
			{
				CapturedAtUtc = DateTime.UtcNow,
				Event = eventName,
				Detail = detail
			});
			if (_markers.Count > 200)
			{
				_markers.RemoveRange(0, _markers.Count - 200);
				_persistedMarkerCount = Math.Min(_persistedMarkerCount, _markers.Count);
			}
		}
	}

	public void Record(FaceLandmarkFrame rawFrame, FaceLandmarkFrame reconstructedFrame, int frameWidth, int frameHeight)
	{
		if (Volatile.Read(in _disposed) == 0)
		{
			QueueFrame(new AuditFrameInput(Volatile.Read(in _sessionGeneration), (rawFrame.CapturedAtUtc == default(DateTime)) ? DateTime.UtcNow : rawFrame.CapturedAtUtc, frameWidth, frameHeight, rawFrame, reconstructedFrame));
		}
	}

	public void RecordMissingFace(DateTime capturedAtUtc)
	{
		if (Volatile.Read(in _disposed) == 0)
		{
			QueueFrame(new AuditFrameInput(Volatile.Read(in _sessionGeneration), (capturedAtUtc == default(DateTime)) ? DateTime.UtcNow : capturedAtUtc, 0, 0, FaceLandmarkFrame.None, FaceLandmarkFrame.None));
		}
	}

	public string GetHtmlPath()
	{
		lock (_sync)
		{
			return string.IsNullOrWhiteSpace(_sessionFolder) ? "" : Path.Combine(_sessionFolder, "mediapipe_convergence_audit.html");
		}
	}

	public void EnsureReport()
	{
		lock (_sync)
		{
			WriteArtifactsLocked(force: true);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		try
		{
			_workerTask?.Wait(TimeSpan.FromSeconds(2L));
		}
		catch
		{
		}
		lock (_sync)
		{
			WriteArtifactsLocked(force: true);
		}
	}

	private void QueueFrame(AuditFrameInput input)
	{
		if (Volatile.Read(in _disposed) != 0 || Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0)
		{
			Interlocked.Increment(ref _auditFramesReplaced);
			return;
		}
		_workerTask = Task.Run(delegate
		{
			ProcessFrameSingleFlight(input);
		});
	}

	private void ProcessFrameSingleFlight(AuditFrameInput input)
	{
		try
		{
			if (Volatile.Read(in _disposed) == 0)
			{
				ProcessFrame(input);
			}
		}
		finally
		{
			Interlocked.Exchange(ref _workerRunning, 0);
		}
	}

	private void ProcessFrame(AuditFrameInput input)
	{
		if (!input.RawFrame.HasFace || !MediaPipeFaceCanonicalizer.TryCanonicalize(input.RawFrame.DenseMeshPoints, input.FrameWidth, input.FrameHeight, out MediaPipeCanonicalFace canonicalFace))
		{
			lock (_sync)
			{
				if (input.SessionGeneration == _sessionGeneration)
				{
					_totalFrames++;
					_missingFaceFrames++;
					WriteArtifactsLocked(force: false);
				}
				return;
			}
		}
		MediaPipeConvergenceSample mediaPipeConvergenceSample = null;
		IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList = null;
		lock (_sync)
		{
			if (input.SessionGeneration != _sessionGeneration)
			{
				return;
			}
			_totalFrames++;
			_faceFrames++;
			double screenMotion = CalculateRawScreenMotionPercent(_previousRawLandmarks, input.RawFrame.DenseMeshPoints, canonicalFace.EyeSpan, input.FrameWidth, input.FrameHeight);
			double canonicalAll = (((object)_previousCanonicalFace == null) ? double.NaN : (MediaPipeFaceCanonicalizer.CalculateRmsDifference(_previousCanonicalFace, canonicalFace) * 100.0));
			double canonicalStable = (((object)_previousCanonicalFace == null) ? double.NaN : (MediaPipeFaceCanonicalizer.CalculateRmsDifference(_previousCanonicalFace, canonicalFace, StableShellLandmarkIndices) * 100.0));
			double poseMotion = CalculatePoseMotionDegrees(input.RawFrame, _previousHeadA, _previousHeadB, _previousHeadC);
			double appCorrection = CalculateAppCorrectionPercent(input.RawFrame, input.ReconstructedFrame);
			AccumulateInterval(screenMotion, canonicalAll, canonicalStable, poseMotion, appCorrection);
			UpdateLandmarkStatistics(canonicalFace);
			_previousCanonicalFace = canonicalFace;
			_previousRawLandmarks = input.RawFrame.DenseMeshPoints;
			_previousHeadA = input.RawFrame.HeadPitchDegrees;
			_previousHeadB = input.RawFrame.HeadYawDegrees;
			_previousHeadC = input.RawFrame.HeadRollDegrees;
			if (_lastSampleAtUtc == DateTime.MinValue || (input.CapturedAtUtc - _lastSampleAtUtc).TotalSeconds >= 1.0)
			{
				mediaPipeConvergenceSample = CreateIntervalSample(input, canonicalFace);
				_samples.Add(mediaPipeConvergenceSample);
				if (_samples.Count > 7200)
				{
					int num = _samples.Count - 7200;
					_samples.RemoveRange(0, num);
					_persistedSampleCount = Math.Max(0, _persistedSampleCount - num);
				}
				_lastSampleAtUtc = input.CapturedAtUtc;
				readOnlyList = input.RawFrame.DenseMeshPoints;
				ResetInterval();
			}
			WriteArtifactsLocked(force: false);
		}
		if (mediaPipeConvergenceSample != null && readOnlyList != null)
		{
			AppendRawLandmarkRecord(input, readOnlyList);
		}
	}

	private MediaPipeConvergenceSample CreateIntervalSample(AuditFrameInput input, MediaPipeCanonicalFace canonicalFace)
	{
		return new MediaPipeConvergenceSample
		{
			CapturedAtUtc = input.CapturedAtUtc,
			ElapsedSeconds = Math.Max(0.0, (input.CapturedAtUtc - _sessionStartedAtUtc).TotalSeconds),
			FaceFrameCount = _faceFrames,
			ScreenMotionPercent = AverageInterval(_intervalScreenMotionSum, _intervalScreenMotionCount),
			CanonicalAllRmsPercent = AverageInterval(_intervalCanonicalAllSum, _intervalCanonicalAllCount),
			CanonicalStableRmsPercent = AverageInterval(_intervalCanonicalStableSum, _intervalCanonicalStableCount),
			PoseMotionDegrees = AverageInterval(_intervalPoseMotionSum, _intervalPoseMotionCount),
			AppCorrectionRmsPercent = AverageInterval(_intervalAppCorrectionSum, _intervalAppCorrectionCount),
			TrackingConfidencePercent = input.RawFrame.TrackingConfidence * 100.0,
			EyeSpanNormalized = canonicalFace.EyeSpan,
			HeadA = input.RawFrame.HeadPitchDegrees,
			HeadB = input.RawFrame.HeadYawDegrees,
			HeadC = input.RawFrame.HeadRollDegrees
		};
	}

	private void AccumulateInterval(double screenMotion, double canonicalAll, double canonicalStable, double poseMotion, double appCorrection)
	{
		if (double.IsFinite(screenMotion))
		{
			_intervalScreenMotionSum += screenMotion;
			_intervalScreenMotionCount++;
		}
		if (double.IsFinite(canonicalAll))
		{
			_intervalCanonicalAllSum += canonicalAll;
			_intervalCanonicalAllCount++;
		}
		if (double.IsFinite(canonicalStable))
		{
			_intervalCanonicalStableSum += canonicalStable;
			_intervalCanonicalStableCount++;
		}
		if (double.IsFinite(poseMotion))
		{
			_intervalPoseMotionSum += poseMotion;
			_intervalPoseMotionCount++;
		}
		if (double.IsFinite(appCorrection))
		{
			_intervalAppCorrectionSum += appCorrection;
			_intervalAppCorrectionCount++;
		}
	}

	private void ResetInterval()
	{
		_intervalScreenMotionSum = 0.0;
		_intervalCanonicalAllSum = 0.0;
		_intervalCanonicalStableSum = 0.0;
		_intervalPoseMotionSum = 0.0;
		_intervalAppCorrectionSum = 0.0;
		_intervalScreenMotionCount = 0;
		_intervalCanonicalAllCount = 0;
		_intervalCanonicalStableCount = 0;
		_intervalPoseMotionCount = 0;
		_intervalAppCorrectionCount = 0;
	}

	private static double AverageInterval(double sum, int count)
	{
		if (count != 0)
		{
			return sum / (double)count;
		}
		return double.NaN;
	}

	private void StartNewSessionLocked(string reason)
	{
		_sessionGeneration++;
		_sessionId = CreateSessionId();
		_sessionStartedAtUtc = DateTime.UtcNow;
		_sessionReason = reason;
		_sessionFolder = (string.IsNullOrWhiteSpace(_configuredRoot) ? "" : Path.Combine(_configuredRoot, _sessionId));
		_samples.Clear();
		_markers.Clear();
		ResetStatistics();
		_previousCanonicalFace = null;
		_previousRawLandmarks = null;
		_previousHeadA = null;
		_previousHeadB = null;
		_previousHeadC = null;
		_totalFrames = 0L;
		_faceFrames = 0L;
		_missingFaceFrames = 0L;
		Interlocked.Exchange(ref _auditFramesReplaced, 0L);
		_persistedSampleCount = 0;
		_persistedMarkerCount = 0;
		_lastSampleAtUtc = DateTime.MinValue;
		_lastReportWriteAtUtc = DateTime.MinValue;
		ResetInterval();
		_markers.Add(new MediaPipeConvergenceMarker
		{
			CapturedAtUtc = _sessionStartedAtUtc,
			Event = "Audit session started",
			Detail = reason
		});
		WriteArtifactsLocked(force: true);
	}

	private MediaPipeConvergenceSummary BuildSummaryLocked()
	{
		DateTime utcNow = DateTime.UtcNow;
		MediaPipeConvergenceSample[] array = _samples.Where(delegate(MediaPipeConvergenceSample sample)
		{
			double elapsedSeconds = sample.ElapsedSeconds;
			return elapsedSeconds >= 5.0 && elapsedSeconds <= 65.0;
		}).ToArray();
		DateTime dateTime;
		if (_samples.Count != 0)
		{
			List<MediaPipeConvergenceSample> samples = _samples;
			dateTime = samples[samples.Count - 1].CapturedAtUtc - TimeSpan.FromSeconds(60L);
		}
		else
		{
			dateTime = utcNow;
		}
		DateTime recentCutoff = dateTime;
		MediaPipeConvergenceSample[] array2 = _samples.Where((MediaPipeConvergenceSample sample) => sample.CapturedAtUtc >= recentCutoff).ToArray();
		double num = Average(array, (MediaPipeConvergenceSample sample) => sample.CanonicalStableRmsPercent);
		double num2 = Average(array2, (MediaPipeConvergenceSample sample) => sample.CanonicalStableRmsPercent);
		double num3 = ((double.IsFinite(num) && num > 1E-06 && double.IsFinite(num2)) ? ((num - num2) / num * 100.0) : double.NaN);
		(string, string) tuple = BuildState(utcNow - _sessionStartedAtUtc, array.Length, array2.Length, num3);
		return new MediaPipeConvergenceSummary
		{
			SessionId = _sessionId,
			SessionReason = _sessionReason,
			SessionStartedAtUtc = _sessionStartedAtUtc,
			UpdatedAtUtc = utcNow,
			ElapsedSeconds = Math.Max(0.0, (utcNow - _sessionStartedAtUtc).TotalSeconds),
			TotalFrames = _totalFrames,
			FaceFrames = _faceFrames,
			MissingFaceFrames = _missingFaceFrames,
			AuditFramesReplaced = Interlocked.Read(in _auditFramesReplaced),
			FaceLockPercent = ((_totalFrames == 0L) ? 0.0 : ((double)_faceFrames * 100.0 / (double)_totalFrames)),
			RetainedSampleCount = _samples.Count,
			FirstMinuteStableRmsPercent = num,
			RecentMinuteStableRmsPercent = num2,
			RecentMinuteAllRmsPercent = Average(array2, (MediaPipeConvergenceSample sample) => sample.CanonicalAllRmsPercent),
			RecentMinuteScreenMotionPercent = Average(array2, (MediaPipeConvergenceSample sample) => sample.ScreenMotionPercent),
			RecentMinutePoseMotionDegrees = Average(array2, (MediaPipeConvergenceSample sample) => sample.PoseMotionDegrees),
			RecentMinuteAppCorrectionPercent = Average(array2, (MediaPipeConvergenceSample sample) => sample.AppCorrectionRmsPercent),
			ConvergenceImprovementPercent = num3,
			State = tuple.Item1,
			Interpretation = tuple.Item2,
			MostVariableLandmarks = GetMostVariableLandmarks(12)
		};
	}

	private static (string State, string Interpretation) BuildState(TimeSpan elapsed, int firstCount, int recentCount, double improvement)
	{
		if (elapsed < TimeSpan.FromMinutes(1L) || firstCount < 20)
		{
			return (State: "Collecting baseline", Interpretation: "Keep the face visible while the first 60-second canonical stability baseline is measured.");
		}
		if (elapsed < TimeSpan.FromMinutes(2L) || recentCount < 20 || !double.IsFinite(improvement))
		{
			return (State: "Collecting comparison", Interpretation: "The first-minute baseline exists. Continue the same session so a later 60-second window can be compared fairly.");
		}
		if (improvement >= 15.0)
		{
			return (State: "Measured convergence", Interpretation: $"Canonical stable-shell jitter is {improvement:0.#}% lower than the first-minute baseline.");
		}
		if (improvement <= -15.0)
		{
			return (State: "Measured regression", Interpretation: $"Canonical stable-shell jitter is {Math.Abs(improvement):0.#}% higher than the first-minute baseline. Check pose, occlusion, focus, or a reset marker.");
		}
		return (State: "Stable range", Interpretation: $"Canonical stable-shell jitter changed {improvement:+0.#;-0.#;0}% from the first-minute baseline; no large convergence or regression is measured yet.");
	}

	private IReadOnlyList<MediaPipeLandmarkVariance> GetMostVariableLandmarks(int count)
	{
		return (from item in _landmarkStatistics
			where item.Count >= 20
			select item.ToSummary() into item
			orderby item.RmsDeviationPercent descending
			select item).Take(count).ToArray();
	}

	private void UpdateLandmarkStatistics(MediaPipeCanonicalFace face)
	{
		int num = Math.Min(face.Points.Count, _landmarkStatistics.Length);
		for (int i = 0; i < num; i++)
		{
			MediaPipeCanonicalPoint point = face.Points[i];
			if (point.IsValid)
			{
				_landmarkStatistics[i].Add(point);
			}
		}
	}

	private void ResetStatistics()
	{
		for (int i = 0; i < _landmarkStatistics.Length; i++)
		{
			_landmarkStatistics[i].Reset();
		}
	}

	private void WriteArtifactsLocked(bool force)
	{
		if (string.IsNullOrWhiteSpace(_sessionFolder) || (!force && DateTime.UtcNow - _lastReportWriteAtUtc < ReportWriteInterval))
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(_sessionFolder);
			AppendSamplesLocked();
			AppendMarkersLocked();
			MediaPipeConvergenceSummary mediaPipeConvergenceSummary = BuildSummaryLocked();
			AtomicTextFileWriter.WriteAllText(Path.Combine(_sessionFolder, "mediapipe_convergence_summary.json"), JsonSerializer.Serialize(mediaPipeConvergenceSummary, JsonOptions), Utf8WithoutBom);
			AtomicTextFileWriter.WriteAllText(Path.Combine(_sessionFolder, "mediapipe_convergence_audit.html"), BuildHtml(mediaPipeConvergenceSummary, _samples.TakeLast(300).ToArray(), _markers.TakeLast(30).ToArray()), Utf8WithoutBom);
			_lastReportWriteAtUtc = DateTime.UtcNow;
		}
		catch
		{
		}
	}

	private void AppendSamplesLocked()
	{
		if (_persistedSampleCount >= _samples.Count)
		{
			return;
		}
		string text = Path.Combine(_sessionFolder, "mediapipe_convergence_samples.csv");
		bool flag = !File.Exists(text) || new FileInfo(text).Length == 0;
		using StreamWriter streamWriter = new StreamWriter(text, append: true, Utf8WithoutBom);
		if (flag)
		{
			streamWriter.WriteLine("capturedAtUtc,elapsedSeconds,faceFrames,screenMotionPercent,canonicalAllRmsPercent,canonicalStableRmsPercent,poseMotionDegrees,appCorrectionRmsPercent,trackingConfidencePercent,eyeSpanNormalized,a,b,c");
		}
		for (int i = _persistedSampleCount; i < _samples.Count; i++)
		{
			MediaPipeConvergenceSample mediaPipeConvergenceSample = _samples[i];
			InlineArray13<string> buffer = default(InlineArray13<string>);
			buffer[0] = mediaPipeConvergenceSample.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture);
			buffer[1] = Number(mediaPipeConvergenceSample.ElapsedSeconds);
			buffer[2] = mediaPipeConvergenceSample.FaceFrameCount.ToString(CultureInfo.InvariantCulture);
			buffer[3] = Number(mediaPipeConvergenceSample.ScreenMotionPercent);
			buffer[4] = Number(mediaPipeConvergenceSample.CanonicalAllRmsPercent);
			buffer[5] = Number(mediaPipeConvergenceSample.CanonicalStableRmsPercent);
			buffer[6] = Number(mediaPipeConvergenceSample.PoseMotionDegrees);
			buffer[7] = Number(mediaPipeConvergenceSample.AppCorrectionRmsPercent);
			buffer[8] = Number(mediaPipeConvergenceSample.TrackingConfidencePercent);
			buffer[9] = Number(mediaPipeConvergenceSample.EyeSpanNormalized);
			buffer[10] = Number(mediaPipeConvergenceSample.HeadA);
			buffer[11] = Number(mediaPipeConvergenceSample.HeadB);
			buffer[12] = Number(mediaPipeConvergenceSample.HeadC);
			streamWriter.WriteLine(string.Join(",", (ReadOnlySpan<string?>)buffer));
		}
		_persistedSampleCount = _samples.Count;
	}

	private void AppendMarkersLocked()
	{
		if (_persistedMarkerCount >= _markers.Count)
		{
			return;
		}
		string text = Path.Combine(_sessionFolder, "mediapipe_convergence_markers.csv");
		bool flag = !File.Exists(text) || new FileInfo(text).Length == 0;
		using StreamWriter streamWriter = new StreamWriter(text, append: true, Utf8WithoutBom);
		if (flag)
		{
			streamWriter.WriteLine("capturedAtUtc,event,detail");
		}
		for (int i = _persistedMarkerCount; i < _markers.Count; i++)
		{
			MediaPipeConvergenceMarker mediaPipeConvergenceMarker = _markers[i];
			streamWriter.WriteLine($"{mediaPipeConvergenceMarker.CapturedAtUtc:O},{Csv(mediaPipeConvergenceMarker.Event)},{Csv(mediaPipeConvergenceMarker.Detail)}");
		}
		_persistedMarkerCount = _markers.Count;
	}

	private void AppendRawLandmarkRecord(AuditFrameInput input, IReadOnlyList<FaceMeshLandmarkPoint> landmarks)
	{
		string text;
		lock (_sync)
		{
			if (input.SessionGeneration != _sessionGeneration || string.IsNullOrWhiteSpace(_sessionFolder))
			{
				return;
			}
			text = Path.Combine(_sessionFolder, "mediapipe_raw_landmarks.mpaudit");
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(text));
			bool flag = !File.Exists(text) || new FileInfo(text).Length == 0;
			using FileStream output = new FileStream(text, FileMode.Append, FileAccess.Write, FileShare.Read);
			using BinaryWriter binaryWriter = new BinaryWriter(output, Utf8WithoutBom, leaveOpen: false);
			if (flag)
			{
				binaryWriter.Write(Encoding.ASCII.GetBytes("MPAUDIT1"));
				binaryWriter.Write(1);
			}
			binaryWriter.Write(input.CapturedAtUtc.Ticks);
			binaryWriter.Write(input.FrameWidth);
			binaryWriter.Write(input.FrameHeight);
			binaryWriter.Write(landmarks.Count);
			for (int i = 0; i < landmarks.Count; i++)
			{
				FaceMeshLandmarkPoint faceMeshLandmarkPoint = landmarks[i];
				binaryWriter.Write(faceMeshLandmarkPoint.Index);
				binaryWriter.Write((float)faceMeshLandmarkPoint.X);
				binaryWriter.Write((float)faceMeshLandmarkPoint.Y);
				binaryWriter.Write((float)faceMeshLandmarkPoint.Z);
			}
		}
		catch
		{
		}
	}

	private static string BuildHtml(MediaPipeConvergenceSummary summary, IReadOnlyList<MediaPipeConvergenceSample> samples, IReadOnlyList<MediaPipeConvergenceMarker> markers)
	{
		string value = string.Join("", from sample in samples.TakeLast(30).Reverse()
			select $"<tr><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{sample.ScreenMotionPercent:0.###}%</td><td>{sample.CanonicalStableRmsPercent:0.###}%</td><td>{sample.CanonicalAllRmsPercent:0.###}%</td><td>{sample.PoseMotionDegrees:0.###} deg</td><td>{sample.AppCorrectionRmsPercent:0.###}%</td><td>{sample.HeadA:0.#}/{sample.HeadB:0.#}/{sample.HeadC:0.#}</td></tr>");
		string value2 = string.Join("", from marker in markers.Reverse()
			select $"<tr><td>{H(marker.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{H(marker.Event)}</td><td>{H(marker.Detail)}</td></tr>");
		string value3 = string.Join("", summary.MostVariableLandmarks.Select((MediaPipeLandmarkVariance item) => $"<tr><td>{item.Index}</td><td>{item.RmsDeviationPercent:0.###}%</td><td>{item.SampleCount}</td></tr>"));
		string value4 = BuildChart(samples);
		string value5 = ((summary.State == "Measured convergence") ? "good" : ((summary.State == "Measured regression") ? "bad" : "warn"));
		return $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"refresh\" content=\"5\"><title>MediaPipe Convergence Audit</title><style>\n:root{{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--good:#80e0a4;--warn:#ffd27a;--bad:#ff8b8b;--cyan:#5ed5ff;--pink:#ff8fbe}}*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}}main{{max-width:1500px;margin:auto;padding:18px}}.panel{{border:1px solid var(--line);background:var(--panel);padding:14px;margin:0 0 14px;border-radius:6px}}.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:10px}}.metric{{border:1px solid #1c3042;padding:10px}}.metric strong{{display:block;font-size:21px;color:var(--cyan)}}.good{{color:var(--good)}}.warn{{color:var(--warn)}}.bad{{color:var(--bad)}}.muted{{color:var(--muted)}}h1,h2{{margin:0 0 8px}}table{{width:100%;border-collapse:collapse}}td,th{{border-bottom:1px solid #1c3042;padding:6px 5px;text-align:left}}th{{color:var(--muted)}}.two{{display:grid;grid-template-columns:2fr 1fr;gap:14px}}@media(max-width:900px){{.two{{grid-template-columns:1fr}}}}svg{{width:100%;height:260px;background:#071019;border:1px solid #1c3042}}.legend span{{margin-right:18px}}.cyan{{color:var(--cyan)}}.pink{{color:var(--pink)}}code{{color:#b9d7ef}}\n</style></head><body><main><section class=\"panel\"><h1>MediaPipe Convergence Audit</h1><p class=\"{value5}\"><strong>{H(summary.State)}</strong>: {H(summary.Interpretation)}</p><p class=\"muted\">Session {H(summary.SessionId)} started {H(summary.SessionStartedAtUtc.ToLocalTime().ToString("G", CultureInfo.InvariantCulture))} because {H(summary.SessionReason)}. This report refreshes every five seconds. Raw landmarks are measured before Avatar Builder's temporal reconstruction.</p></section><section class=\"panel grid\">\n<div class=\"metric\"><span>First-minute stable RMS</span><strong>{Metric(summary.FirstMinuteStableRmsPercent, "%")}</strong></div>\n<div class=\"metric\"><span>Recent stable RMS</span><strong>{Metric(summary.RecentMinuteStableRmsPercent, "%")}</strong></div>\n<div class=\"metric\"><span>Change from baseline</span><strong>{SignedMetric(summary.ConvergenceImprovementPercent, "% better")}</strong></div>\n<div class=\"metric\"><span>Screen motion</span><strong>{Metric(summary.RecentMinuteScreenMotionPercent, "%")}</strong></div>\n<div class=\"metric\"><span>Canonical all-point RMS</span><strong>{Metric(summary.RecentMinuteAllRmsPercent, "%")}</strong></div>\n<div class=\"metric\"><span>App contour correction</span><strong>{Metric(summary.RecentMinuteAppCorrectionPercent, "%")}</strong></div>\n<div class=\"metric\"><span>Face lock</span><strong>{summary.FaceLockPercent:0.0}%</strong></div>\n<div class=\"metric\"><span>Audit inputs dropped while busy</span><strong>{summary.AuditFramesReplaced}</strong></div>\n</section><section class=\"panel\"><h2>Canonical Stability Timeline</h2><p class=\"legend\"><span class=\"cyan\">Stable-shell canonical RMS</span><span class=\"pink\">Avatar Builder contour correction</span></p>{value4}<p class=\"muted\">Values are percentages of the measured inter-eye span. Head translation, rotation, and scale are removed before canonical RMS is calculated.</p></section><section class=\"two\"><section class=\"panel\"><h2>Recent Samples</h2><table><tr><th>Time</th><th>Screen motion</th><th>Stable canonical</th><th>All canonical</th><th>Pose motion</th><th>App correction</th><th>A/B/C</th></tr>{value}</table></section><section class=\"panel\"><h2>Most Variable Landmarks</h2><table><tr><th>Index</th><th>RMS deviation</th><th>Frames</th></tr>{value3}</table></section></section><section class=\"panel\"><h2>Experiment Markers</h2><table><tr><th>Time</th><th>Event</th><th>Detail</th></tr>{value2}</table></section><section class=\"panel muted\"><p>Frames {summary.FaceFrames} face / {summary.MissingFaceFrames} missing; {summary.RetainedSampleCount} one-second samples. Exact raw 478-point snapshots are stored in <code>{"mediapipe_raw_landmarks.mpaudit"}</code>; scalar measurements are stored in <code>{"mediapipe_convergence_samples.csv"}</code>.</p></section></main></body></html>";
	}

	private static string BuildChart(IReadOnlyList<MediaPipeConvergenceSample> samples)
	{
		if (samples.Count < 2)
		{
			return "<svg viewBox=\"0 0 1200 250\"><text x=\"30\" y=\"125\" fill=\"#9db7c9\">Collecting samples...</text></svg>";
		}
		double num = samples.Max((MediaPipeConvergenceSample sample) => Math.Max(sample.CanonicalStableRmsPercent, sample.AppCorrectionRmsPercent));
		num = Math.Max(0.01, num * 1.1);
		StringBuilder stringBuilder = new StringBuilder();
		StringBuilder stringBuilder2 = new StringBuilder();
		for (int num2 = 0; num2 < samples.Count; num2++)
		{
			double x = 18.0 + (double)num2 * 1164.0 / (double)Math.Max(1, samples.Count - 1);
			AppendChartPoint(stringBuilder, x, ChartY(samples[num2].CanonicalStableRmsPercent, num, 250.0, 18.0));
			AppendChartPoint(stringBuilder2, x, ChartY(samples[num2].AppCorrectionRmsPercent, num, 250.0, 18.0));
		}
		return $"<svg viewBox=\"0 0 {1200.0:0} {250.0:0}\"><line x1=\"{18.0}\" y1=\"{232.0}\" x2=\"{1182.0}\" y2=\"{232.0}\" stroke=\"#28435b\"/><polyline points=\"{stringBuilder}\" fill=\"none\" stroke=\"#5ed5ff\" stroke-width=\"2\"/><polyline points=\"{stringBuilder2}\" fill=\"none\" stroke=\"#ff8fbe\" stroke-width=\"2\"/></svg>";
	}

	private static double ChartY(double value, double maximum, double height, double padding)
	{
		double num = ((!double.IsFinite(value)) ? 0.0 : Math.Clamp(value / maximum, 0.0, 1.0));
		return height - padding - num * (height - padding * 2.0);
	}

	private static void AppendChartPoint(StringBuilder builder, double x, double y)
	{
		if (builder.Length > 0)
		{
			builder.Append(' ');
		}
		builder.Append(x.ToString("0.##", CultureInfo.InvariantCulture));
		builder.Append(',');
		builder.Append(y.ToString("0.##", CultureInfo.InvariantCulture));
	}

	private static double CalculateRawScreenMotionPercent(IReadOnlyList<FaceMeshLandmarkPoint>? previous, IReadOnlyList<FaceMeshLandmarkPoint> current, double eyeSpan, int frameWidth, int frameHeight)
	{
		if (previous == null || previous.Count == 0 || eyeSpan <= 0.0 || frameWidth <= 0 || frameHeight <= 0)
		{
			return double.NaN;
		}
		double num = (double)frameHeight / (double)frameWidth;
		int num2 = Math.Min(previous.Count, current.Count);
		double num3 = 0.0;
		int num4 = 0;
		for (int i = 0; i < num2; i++)
		{
			if (previous[i].Index == current[i].Index)
			{
				double num5 = current[i].X - previous[i].X;
				double num6 = (current[i].Y - previous[i].Y) * num;
				double num7 = current[i].Z - previous[i].Z;
				num3 += num5 * num5 + num6 * num6 + num7 * num7;
				num4++;
			}
		}
		if (num4 != 0)
		{
			return Math.Sqrt(num3 / (double)num4) / eyeSpan * 100.0;
		}
		return double.NaN;
	}

	private static double CalculatePoseMotionDegrees(FaceLandmarkFrame current, double? previousA, double? previousB, double? previousC)
	{
		if (previousA.HasValue)
		{
			double valueOrDefault = previousA.GetValueOrDefault();
			if (previousB.HasValue)
			{
				double valueOrDefault2 = previousB.GetValueOrDefault();
				if (previousC.HasValue)
				{
					double valueOrDefault3 = previousC.GetValueOrDefault();
					double num = current.HeadPitchDegrees - valueOrDefault;
					double num2 = current.HeadYawDegrees - valueOrDefault2;
					double num3 = current.HeadRollDegrees - valueOrDefault3;
					return Math.Sqrt(num * num + num2 * num2 + num3 * num3);
				}
			}
		}
		return double.NaN;
	}

	private static double CalculateAppCorrectionPercent(FaceLandmarkFrame raw, FaceLandmarkFrame reconstructed)
	{
		double num = BoundsWidth(raw.FaceContour);
		if (num <= 0.0)
		{
			return 0.0;
		}
		double sum = 0.0;
		int count = 0;
		AccumulateContourCorrection(raw.LeftEyeContour, reconstructed.LeftEyeContour, ref sum, ref count);
		AccumulateContourCorrection(raw.RightEyeContour, reconstructed.RightEyeContour, ref sum, ref count);
		AccumulateContourCorrection(raw.InnerLipContour, reconstructed.InnerLipContour, ref sum, ref count);
		if (count != 0)
		{
			return Math.Sqrt(sum / (double)count) / num * 100.0;
		}
		return 0.0;
	}

	private static void AccumulateContourCorrection(IReadOnlyList<Point> raw, IReadOnlyList<Point> reconstructed, ref double sum, ref int count)
	{
		int num = Math.Min(raw.Count, reconstructed.Count);
		for (int i = 0; i < num; i++)
		{
			double num2 = raw[i].X - reconstructed[i].X;
			double num3 = raw[i].Y - reconstructed[i].Y;
			sum += num2 * num2 + num3 * num3;
			count++;
		}
	}

	private static double BoundsWidth(IReadOnlyList<Point> points)
	{
		if (points.Count == 0)
		{
			return 0.0;
		}
		double num = double.PositiveInfinity;
		double num2 = double.NegativeInfinity;
		for (int i = 0; i < points.Count; i++)
		{
			num = Math.Min(num, points[i].X);
			num2 = Math.Max(num2, points[i].X);
		}
		return Math.Max(0.0, num2 - num);
	}

	private static double Average<T>(IReadOnlyList<T> values, Func<T, double> selector)
	{
		double num = 0.0;
		int num2 = 0;
		for (int i = 0; i < values.Count; i++)
		{
			double num3 = selector(values[i]);
			if (double.IsFinite(num3))
			{
				num += num3;
				num2++;
			}
		}
		if (num2 != 0)
		{
			return num / (double)num2;
		}
		return double.NaN;
	}

	private static LandmarkRunningStatistics[] CreateLandmarkStatistics(int count)
	{
		LandmarkRunningStatistics[] array = new LandmarkRunningStatistics[count];
		for (int i = 0; i < count; i++)
		{
			array[i] = new LandmarkRunningStatistics(i);
		}
		return array;
	}

	private static string CreateSessionId()
	{
		return $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}".Substring(0, 24);
	}

	private static string Number(double value)
	{
		if (!double.IsFinite(value))
		{
			return "";
		}
		return value.ToString("0.######", CultureInfo.InvariantCulture);
	}

	private static string Csv(string value)
	{
		return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
	}

	private static string H(string value)
	{
		return WebUtility.HtmlEncode(value ?? "");
	}

	private static string Metric(double value, string suffix)
	{
		if (!double.IsFinite(value))
		{
			return "collecting";
		}
		return $"{value:0.###}{suffix}";
	}

	private static string SignedMetric(double value, string suffix)
	{
		if (!double.IsFinite(value))
		{
			return "collecting";
		}
		return $"{value:+0.#;-0.#;0} {suffix}";
	}
}
