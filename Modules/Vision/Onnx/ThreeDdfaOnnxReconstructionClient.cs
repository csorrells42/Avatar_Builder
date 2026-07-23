using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxReconstructionClient : IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly ThreeDdfaOnnxSidecarEnvironment _environment;

	private readonly string _clientSessionId = Guid.NewGuid().ToString("N").Substring(0, 12);

	private readonly object _sync = new object();

	private readonly TimeSpan _timeout;

	private Process? _process;

	private IReadOnlyList<ThreeDdfaOnnxSidecarEdge> _denseTopology = Array.Empty<ThreeDdfaOnnxSidecarEdge>();

	private string _lastStandardError = "";

	private bool _firstResponseAfterStart;

	private int _requestNumber;

	private int _disposed;

	public string Status { get; private set; } = "";

	public ThreeDdfaOnnxReconstructionClient(ThreeDdfaOnnxSidecarEnvironment environment)
	{
		_environment = environment;
		_timeout = TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds());
	}

	public ThreeDdfaOnnxSidecarResponse Reconstruct(BitmapSource bitmap, DateTime capturedAtUtc, ThreeDdfaOnnxSidecarFaceBox? faceBox, ThreeDdfaOnnxRequestMode mode = ThreeDdfaOnnxRequestMode.Tracking, int denseSampleStride = 24)
	{
		lock (_sync)
		{
			if (Volatile.Read(in _disposed) != 0)
			{
				return Error("3DDFA/ONNX sidecar client is stopped.");
			}
			if (!_environment.IsReady)
			{
				return Error(_environment.Status);
			}
			if (!EnsureProcess())
			{
				return Error(Status);
			}
			try
			{
				Stopwatch stopwatch = Stopwatch.StartNew();
				Stopwatch stopwatch2 = Stopwatch.StartNew();
				byte[] array = EncodeJpeg(bitmap);
				ThreeDdfaOnnxSidecarRequest threeDdfaOnnxSidecarRequest = new ThreeDdfaOnnxSidecarRequest
				{
					RequestId = $"{_clientSessionId}-{Interlocked.Increment(ref _requestNumber):D6}",
					CapturedAtUtc = capturedAtUtc.ToString("O"),
					FaceBox = faceBox,
					Mode = ToProtocolMode(mode),
					DenseSampleStride = Math.Clamp(denseSampleStride, 1, 200),
					IncludeTopology = (mode == ThreeDdfaOnnxRequestMode.Full && _denseTopology.Count == 0),
					ImageBase64 = Convert.ToBase64String(array)
				};
				string value = JsonSerializer.Serialize(threeDdfaOnnxSidecarRequest, JsonOptions);
				stopwatch2.Stop();
				Stopwatch stopwatch3 = Stopwatch.StartNew();
				_process.StandardInput.WriteLine(value);
				_process.StandardInput.Flush();
				Task<string> task = _process.StandardOutput.ReadLineAsync();
				TimeSpan timeout = ((mode != ThreeDdfaOnnxRequestMode.Full) ? (_firstResponseAfterStart ? TimeSpan.FromMilliseconds(ReadStartupTimeoutMilliseconds()) : _timeout) : (_firstResponseAfterStart ? TimeSpan.FromSeconds(60L) : TimeSpan.FromSeconds(30L)));
				if (!task.Wait(timeout))
				{
					RestartAfterFailure("3DDFA/ONNX sidecar timed out waiting for a reconstruction response.");
					return Error(Status);
				}
				_firstResponseAfterStart = false;
				string result = task.Result;
				stopwatch3.Stop();
				if (string.IsNullOrWhiteSpace(result))
				{
					RestartAfterFailure("3DDFA/ONNX sidecar closed its output stream.");
					return Error(Status);
				}
				Stopwatch stopwatch4 = Stopwatch.StartNew();
				ThreeDdfaOnnxSidecarResponse threeDdfaOnnxSidecarResponse = JsonSerializer.Deserialize<ThreeDdfaOnnxSidecarResponse>(result, JsonOptions);
				stopwatch4.Stop();
				if (threeDdfaOnnxSidecarResponse == null)
				{
					return Error("3DDFA/ONNX sidecar returned an empty response.");
				}
				threeDdfaOnnxSidecarResponse.ExpandCompactMeshData();
				if (threeDdfaOnnxSidecarResponse.DenseEdges.Count > 0)
				{
					_denseTopology = threeDdfaOnnxSidecarResponse.DenseEdges;
				}
				else if (mode == ThreeDdfaOnnxRequestMode.Full && _denseTopology.Count > 0)
				{
					threeDdfaOnnxSidecarResponse.DenseEdges = _denseTopology;
				}
				if (!string.Equals(threeDdfaOnnxSidecarResponse.RequestId, threeDdfaOnnxSidecarRequest.RequestId, StringComparison.Ordinal))
				{
					return Error($"3DDFA/ONNX sidecar response id mismatch: expected {threeDdfaOnnxSidecarRequest.RequestId}, got {threeDdfaOnnxSidecarResponse.RequestId}.");
				}
				Status = threeDdfaOnnxSidecarResponse.Status;
				stopwatch.Stop();
				threeDdfaOnnxSidecarResponse.Diagnostics = new VisionPipelineDiagnostics
				{
					CapturedAtUtc = capturedAtUtc,
					Backend = "3DDFA-V2 ONNX",
					Mode = threeDdfaOnnxSidecarRequest.Mode,
					SourceWidth = bitmap.PixelWidth,
					SourceHeight = bitmap.PixelHeight,
					InputWidth = bitmap.PixelWidth,
					InputHeight = bitmap.PixelHeight,
					EncodedPayloadBytes = array.Length,
					HasFace = threeDdfaOnnxSidecarResponse.HasFace,
					ClientPrepareMilliseconds = stopwatch2.Elapsed.TotalMilliseconds,
					SidecarRoundTripMilliseconds = stopwatch3.Elapsed.TotalMilliseconds,
					ClientParseMilliseconds = stopwatch4.Elapsed.TotalMilliseconds,
					EndToEndMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
					SidecarStagesMilliseconds = threeDdfaOnnxSidecarResponse.TimingsMilliseconds,
					Status = threeDdfaOnnxSidecarResponse.Status
				};
				return threeDdfaOnnxSidecarResponse;
			}
			catch (Exception ex)
			{
				string text = Volatile.Read(in _lastStandardError);
				string text2 = (string.IsNullOrWhiteSpace(text) ? ex.Message : (ex.Message + ". Sidecar: " + text));
				RestartAfterFailure("3DDFA/ONNX sidecar call failed: " + text2);
				return Error(Status);
			}
		}
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
		if (Volatile.Read(in _disposed) != 0)
		{
			Status = "3DDFA/ONNX sidecar client is stopped.";
			return false;
		}
		Process process = _process;
		if (process != null && !process.HasExited)
		{
			return true;
		}
		StopProcess();
		Process process2 = null;
		try
		{
			process2 = new Process
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
			process2.StartInfo.ArgumentList.Add("--config");
			process2.StartInfo.ArgumentList.Add(_environment.ConfigPath);
			if (!process2.Start())
			{
				Status = "3DDFA/ONNX sidecar process did not start.";
				return false;
			}
			_process = process2;
			Process runningProcess = process2;
			process2 = null;
			_firstResponseAfterStart = true;
			Volatile.Write(ref _lastStandardError, "");
			Task.Run(delegate
			{
				ReadErrors(runningProcess);
			});
			Status = "3DDFA/ONNX sidecar process started.";
			return true;
		}
		catch (Exception ex)
		{
			Status = "3DDFA/ONNX sidecar process failed to start: " + ex.Message;
			return false;
		}
		finally
		{
			process2?.Dispose();
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
						string text2 = text.Trim();
						Volatile.Write(ref _lastStandardError, text2);
						Status = text2;
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

	private static ThreeDdfaOnnxSidecarResponse Error(string status)
	{
		return new ThreeDdfaOnnxSidecarResponse
		{
			Ok = false,
			Status = status,
			TrustDecision = "3DDFA/ONNX reconstruction unavailable for this frame."
		};
	}

	private static string ToProtocolMode(ThreeDdfaOnnxRequestMode mode)
	{
		return mode switch
		{
			ThreeDdfaOnnxRequestMode.FaceBoxOnly => "faceBoxOnly", 
			ThreeDdfaOnnxRequestMode.Preview => "preview", 
			ThreeDdfaOnnxRequestMode.Full => "full", 
			_ => "tracking", 
		};
	}

	private static int ReadTimeoutMilliseconds()
	{
		if (!int.TryParse(Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_TIMEOUT_MS"), out var result))
		{
			return 4500;
		}
		return Math.Clamp(result, 500, 30000);
	}

	private static int ReadStartupTimeoutMilliseconds()
	{
		if (!int.TryParse(Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_STARTUP_TIMEOUT_MS"), out var result))
		{
			return 25000;
		}
		return Math.Clamp(result, 1000, 60000);
	}
}
