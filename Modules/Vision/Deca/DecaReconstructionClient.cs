using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed class DecaReconstructionClient : IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly DecaSidecarEnvironment _environment;

	private readonly string _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);

	private readonly object _sync = new object();

	private Process? _process;

	private IReadOnlyList<MeshTopologyEdge> _topology = Array.Empty<MeshTopologyEdge>();

	private string _lastStandardError = "";

	private int _requestNumber;

	private IReadOnlyList<double> _currentModelShapeCoefficients = Array.Empty<double>();

	private IReadOnlyList<double> _identityAnchorShapeCoefficients = Array.Empty<double>();

	private long _currentModelSequenceNumber;

	private int _disposed;

	public string Status { get; private set; } = "DECA waiting";

	public DecaReconstructionClient(DecaSidecarEnvironment environment)
	{
		_environment = environment;
	}

	public AvatarReconstructionSnapshot? Reconstruct(BitmapSource bitmap, DateTime capturedAtUtc, DecaSidecarFaceBox faceBox, FaceLandmarkFrame observedLandmarks, DecaPinnedStillConvergenceOptions? pinnedStill = null, DecaIdentityFitProfile identityFitProfile = DecaIdentityFitProfile.Flame68)
	{
		lock (_sync)
		{
			if (Volatile.Read(in _disposed) != 0)
			{
				Status = "DECA sidecar client is stopped.";
				return null;
			}
			if (!_environment.IsReady)
			{
				Status = _environment.Status;
				return null;
			}
			if (!EnsureProcess())
			{
				return null;
			}
			try
			{
				byte[] inArray = EncodeJpeg(bitmap);
				DecaSidecarRequest decaSidecarRequest = new DecaSidecarRequest
				{
					RequestId = $"{_sessionId}-{Interlocked.Increment(ref _requestNumber):D6}",
					CapturedAtUtc = capturedAtUtc.ToString("O"),
					ImageBase64 = Convert.ToBase64String(inArray),
					FaceBox = faceBox,
					IncludeTopology = (_topology.Count == 0),
					PreviousModelShapeCoefficients = _currentModelShapeCoefficients,
					IdentityAnchorShapeCoefficients = _identityAnchorShapeCoefficients,
					PreviousModelSequenceNumber = _currentModelSequenceNumber,
					IdentityFrames = new global::_003C_003Ez__ReadOnlySingleElementList<DecaIdentityFitFrame>(new DecaIdentityFitFrame
					{
						FrameWidthPixels = bitmap.PixelWidth,
						FrameHeightPixels = bitmap.PixelHeight,
						FaceBox = faceBox,
						ObservedLandmarkCoordinates = observedLandmarks.DenseMeshPoints.SelectMany((FaceMeshLandmarkPoint point) => new double[3] { point.X, point.Y, point.Z }).ToList()
					}),
					IdentityFitProfile = DecaIdentityFitProfileNames.ToProtocolValue(identityFitProfile),
					MaximumIterations = (pinnedStill?.OptimizationIterationsPerPass ?? 12),
					PinnedStillMaximumPasses = (pinnedStill?.MaximumPasses ?? 1),
					PinnedStillStablePassesRequired = (pinnedStill?.StablePassesRequired ?? 1),
					PinnedStillCoefficientDeltaThreshold = (pinnedStill?.CoefficientDeltaThreshold ?? 0.0)
				};
				_process.StandardInput.WriteLine(JsonSerializer.Serialize(decaSidecarRequest, JsonOptions));
				_process.StandardInput.Flush();
				DecaSidecarResponse decaSidecarResponse = ReadResponse(_process, decaSidecarRequest.RequestId, pinnedStill?.RequestTimeout ?? TimeSpan.FromSeconds(60L));
				if (decaSidecarResponse == null || !decaSidecarResponse.Ok || !decaSidecarResponse.HasFace)
				{
					Status = decaSidecarResponse?.Status ?? "DECA sidecar returned no reconstruction.";
					return null;
				}
				if (!string.Equals(decaSidecarRequest.RequestId, decaSidecarResponse.RequestId, StringComparison.Ordinal))
				{
					RestartAfterFailure($"DECA response mismatch: expected {decaSidecarRequest.RequestId}, got {decaSidecarResponse.RequestId}.");
					return null;
				}
				if (decaSidecarResponse.DenseEdgeIndices.Count > 0)
				{
					_topology = ExpandEdges(decaSidecarResponse.DenseEdgeIndices);
				}
				List<FaceMeshLandmarkPoint> list = ExpandPoints(decaSidecarResponse.ProjectedVertexCoordinates);
				List<FaceMeshLandmarkPoint> list2 = ExpandPoints(decaSidecarResponse.CanonicalIdentityCoordinates);
				List<FaceMeshLandmarkPoint> list3 = ExpandPoints(decaSidecarResponse.AlignedIdentityProjectedCoordinates);
				if (list.Count < 1000 || list2.Count != list.Count || _topology.Count == 0)
				{
					Status = "DECA returned incomplete FLAME geometry.";
					return null;
				}
				List<FaceMeshLandmarkPoint> list4 = ExpandPoints(decaSidecarResponse.SparseLandmarkCoordinates);
				if (list3.Count != list.Count || decaSidecarResponse.CurrentModelShapeCoefficients.Count == 0)
				{
					Status = "DECA returned incomplete recurrent FLAME identity geometry.";
					return null;
				}
				_currentModelShapeCoefficients = decaSidecarResponse.CurrentModelShapeCoefficients.ToList();
				_currentModelSequenceNumber = decaSidecarResponse.CurrentModelSequenceNumber;
				DecaProjectionFitEvaluation decaProjectionFitEvaluation = DecaProjectionFitEvaluator.Evaluate(list4, observedLandmarks, bitmap.PixelWidth, bitmap.PixelHeight);
				Status = decaSidecarResponse.Status + " " + decaProjectionFitEvaluation.Summary;
				return new AvatarReconstructionSnapshot
				{
					BackendId = (((object)pinnedStill == null) ? "deca-flame-recurrent-v4" : "deca-flame-standard-model-checkpoint-v1"),
					RequestId = decaSidecarResponse.RequestId,
					CapturedAtUtc = ParseCapturedAtUtc(decaSidecarResponse.CapturedAtUtc, capturedAtUtc),
					Source = decaSidecarResponse.Backend,
					CoordinateSpace = ((identityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted) ? "DECA official bbox crop v2 with MediaPipe surface assistance: 105 embedded MediaPipe-to-FLAME surface targets plus the 17-point jaw contour guide recurrent identity fitting. Projected FLAME vertices are paired to the source image; canonical identity vertices are expression-free FLAME model coordinates. A/B/C is global head rotation around X/Y/Z." : "DECA official bbox crop v2: projected FLAME vertices are paired to the source image; canonical identity vertices are expression-free FLAME model coordinates. A/B/C is global head rotation around X/Y/Z."),
					DenseVertexCount = list.Count,
					DenseSampleStride = 1,
					ReconstructionConfidencePercent = decaProjectionFitEvaluation.FitConfidencePercent,
					ARotationAroundXDegrees = decaSidecarResponse.Pose.ARotationAroundXDegrees,
					BRotationAroundYDegrees = decaSidecarResponse.Pose.BRotationAroundYDegrees,
					CRotationAroundZDegrees = decaSidecarResponse.Pose.CRotationAroundZDegrees,
					PoseSource = decaSidecarResponse.Backend,
					TrustDecision = ((identityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted) ? "Experimental MediaPipe-assisted recurrent DECA/FLAME mode used every bundled surface correspondence plus the jaw contour. The previous complete model remained the exact next seed, while the last human-accepted Standard Model remained the fixed identity anchor." : (((object)pinnedStill == null) ? "Recurrent DECA/FLAME mode retains every structurally complete update as the next local seed and keeps the human-accepted Standard Model as its identity anchor; projection fit is diagnostic only." : "This checkpoint held one source still fixed until recurrent coefficient movement stabilized or reached the bounded pass limit; no pose meshes were averaged.")),
					Vertices = list,
					CanonicalIdentityVertices = list2,
					AlignedIdentityVertices = list3,
					CurrentModelShapeCoefficients = decaSidecarResponse.CurrentModelShapeCoefficients,
					CurrentModelSequenceNumber = decaSidecarResponse.CurrentModelSequenceNumber,
					CurrentModelCoefficientDeltaRms = decaSidecarResponse.CurrentModelCoefficientDeltaRms,
					PinnedStillConverged = decaSidecarResponse.PinnedStillConverged,
					PinnedStillPassCount = decaSidecarResponse.PinnedStillPassCount,
					PinnedStillStablePassCount = decaSidecarResponse.PinnedStillStablePassCount,
					TopologyEdges = _topology.ToList(),
					SparseLandmarks = list4,
					CameraMatrixCoefficients = decaSidecarResponse.CameraMatrixCoefficients,
					ShapeCoefficients = decaSidecarResponse.CurrentModelShapeCoefficients,
					ExpressionCoefficients = decaSidecarResponse.ExpressionCoefficients,
					PoseCoefficients = decaSidecarResponse.PoseCoefficients,
					SourceFrameWidthPixels = bitmap.PixelWidth,
					SourceFrameHeightPixels = bitmap.PixelHeight,
					InputFaceBox = new ReconstructionInputFaceBox
					{
						Left = faceBox.Left,
						Top = faceBox.Top,
						Right = faceBox.Right,
						Bottom = faceBox.Bottom,
						Normalized = faceBox.Normalized,
						Confidence = faceBox.Confidence
					},
					ObservedLandmarks = observedLandmarks.DenseMeshPoints.Select((FaceMeshLandmarkPoint point) => new FaceMeshLandmarkPoint
					{
						Index = point.Index,
						X = point.X,
						Y = point.Y,
						Z = point.Z
					}).ToList(),
					Warnings = decaSidecarResponse.Warnings.Concat(decaProjectionFitEvaluation.Warnings).ToList()
				};
			}
			catch (Exception ex)
			{
				string text = (string.IsNullOrWhiteSpace(_lastStandardError) ? ex.Message : (ex.Message + ". Sidecar: " + _lastStandardError));
				RestartAfterFailure("DECA sidecar failed: " + text);
				return null;
			}
		}
	}

	public void SetCurrentModelShapeCoefficients(IReadOnlyList<double>? shapeCoefficients)
	{
		lock (_sync)
		{
			_currentModelShapeCoefficients = ((shapeCoefficients != null && shapeCoefficients.Count > 0) ? shapeCoefficients.ToList() : new List<double>());
			_identityAnchorShapeCoefficients = _currentModelShapeCoefficients.ToList();
			_currentModelSequenceNumber = 0L;
		}
	}

	public bool ResetCurrentModelToIdentityAnchor()
	{
		lock (_sync)
		{
			if (_identityAnchorShapeCoefficients.Count == 0)
			{
				return false;
			}
			_currentModelShapeCoefficients = _identityAnchorShapeCoefficients.ToList();
			_currentModelSequenceNumber = 0L;
			return true;
		}
	}

	private DecaSidecarResponse? ReadResponse(Process process, string requestId, TimeSpan timeout)
	{
		DateTime dateTime = DateTime.UtcNow + timeout;
		DecaSidecarResponse decaSidecarResponse;
		while (true)
		{
			TimeSpan timeSpan = dateTime - DateTime.UtcNow;
			if (timeSpan <= TimeSpan.Zero)
			{
				throw new TimeoutException("DECA sidecar timed out waiting for reconstruction.");
			}
			Task<string?> task = process.StandardOutput.ReadLineAsync();
			if (!task.Wait(timeSpan))
			{
				throw new TimeoutException("DECA sidecar timed out waiting for reconstruction.");
			}
			string text = (task.Result ?? throw new EndOfStreamException("DECA sidecar closed its response stream.")).Trim();
			if (text.Length == 0)
			{
				continue;
			}
			if (text[0] != '{')
			{
				Volatile.Write(ref _lastStandardError, "stdout diagnostic: " + text);
				continue;
			}
			decaSidecarResponse = JsonSerializer.Deserialize<DecaSidecarResponse>(text, JsonOptions);
			if (decaSidecarResponse != null)
			{
				break;
			}
		}
		if (!string.Equals(requestId, decaSidecarResponse.RequestId, StringComparison.Ordinal))
		{
			throw new InvalidDataException($"DECA response mismatch: expected {requestId}, got {decaSidecarResponse.RequestId}.");
		}
		return decaSidecarResponse;
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			StopProcess();
		}
	}

	private bool EnsureProcess()
	{
		Process process = _process;
		if (process != null && !process.HasExited)
		{
			return true;
		}
		StopProcess();
		try
		{
			Process process2 = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = _environment.PythonPath,
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			process2.StartInfo.ArgumentList.Add(_environment.ScriptPath);
			process2.StartInfo.ArgumentList.Add("--repo");
			process2.StartInfo.ArgumentList.Add(_environment.RepositoryPath);
			process2.StartInfo.ArgumentList.Add("--model");
			process2.StartInfo.ArgumentList.Add(_environment.ModelPath);
			if (!process2.Start())
			{
				Status = "DECA sidecar process did not start.";
				process2.Dispose();
				return false;
			}
			_process = process2;
			_lastStandardError = "";
			Task.Run(delegate
			{
				ReadErrors(process2);
			});
			Status = "DECA sidecar process started.";
			return true;
		}
		catch (Exception ex)
		{
			Status = "DECA sidecar process failed to start: " + ex.Message;
			return false;
		}
	}

	private void RestartAfterFailure(string status)
	{
		Status = status;
		StopProcess();
	}

	private void StopProcess()
	{
		Process process = Interlocked.Exchange(ref _process, null);
		if (process == null)
		{
			return;
		}
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
		finally
		{
			process.Dispose();
		}
	}

	private void ReadErrors(Process process)
	{
		try
		{
			while (!process.HasExited)
			{
				string text = process.StandardError.ReadLine();
				if (text != null)
				{
					if (!string.IsNullOrWhiteSpace(text))
					{
						Volatile.Write(ref _lastStandardError, text.Trim());
					}
					continue;
				}
				break;
			}
		}
		catch
		{
		}
	}

	private static byte[] EncodeJpeg(BitmapSource bitmap)
	{
		FormatConvertedBitmap source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
		JpegBitmapEncoder jpegBitmapEncoder = new JpegBitmapEncoder
		{
			QualityLevel = 90
		};
		jpegBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
		using MemoryStream memoryStream = new MemoryStream();
		jpegBitmapEncoder.Save(memoryStream);
		return memoryStream.ToArray();
	}

	private static List<FaceMeshLandmarkPoint> ExpandPoints(IReadOnlyList<double> coordinates)
	{
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(coordinates.Count / 3);
		for (int i = 0; i + 2 < coordinates.Count; i += 3)
		{
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = i / 3,
				X = coordinates[i],
				Y = coordinates[i + 1],
				Z = coordinates[i + 2]
			});
		}
		return list;
	}

	private static List<MeshTopologyEdge> ExpandEdges(IReadOnlyList<int> indices)
	{
		List<MeshTopologyEdge> list = new List<MeshTopologyEdge>(indices.Count / 2);
		for (int i = 0; i + 1 < indices.Count; i += 2)
		{
			list.Add(new MeshTopologyEdge
			{
				FromIndex = indices[i],
				ToIndex = indices[i + 1],
				Source = "DECA FLAME topology"
			});
		}
		return list;
	}

	private static DateTime ParseCapturedAtUtc(string value, DateTime fallback)
	{
		if (!DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var result))
		{
			return fallback;
		}
		return result.ToUniversalTime();
	}
}
