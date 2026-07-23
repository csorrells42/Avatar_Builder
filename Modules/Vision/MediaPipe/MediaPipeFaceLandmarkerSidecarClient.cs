using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeFaceLandmarkerSidecarClient : IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly MediaPipeSidecarPythonEnvironment _environment;

	private readonly MediaPipeSharedMemoryFrame _sharedMemoryFrame = new MediaPipeSharedMemoryFrame();

	private readonly object _sync = new object();

	private readonly TimeSpan _timeout;

	private Process? _process;

	private bool _firstResponseAfterStart;

	private int _requestNumber;

	private long _lastTimestampMilliseconds;

	private int _disposed;

	public string Status { get; private set; } = "";

	public MediaPipeFaceLandmarkerSidecarClient(MediaPipeSidecarPythonEnvironment environment)
	{
		_environment = environment;
		_timeout = TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds());
	}

	public MediaPipeSidecarResponse Analyze(BitmapSource bitmap, DateTime capturedAtUtc, int sourceWidth = 0, int sourceHeight = 0)
	{
		lock (_sync)
		{
			if (Volatile.Read(in _disposed) != 0)
			{
				return Error("MediaPipe sidecar client is stopped.");
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
				MediaPipeSharedMemoryFrameDescriptor mediaPipeSharedMemoryFrameDescriptor = _sharedMemoryFrame.Write(bitmap);
				long timestampMilliseconds = (_lastTimestampMilliseconds = Math.Max(new DateTimeOffset(capturedAtUtc).ToUnixTimeMilliseconds(), _lastTimestampMilliseconds + 1));
				MediaPipeSidecarRequest mediaPipeSidecarRequest = new MediaPipeSidecarRequest
				{
					RequestId = Interlocked.Increment(ref _requestNumber).ToString("D6"),
					CapturedAtUtc = capturedAtUtc.ToString("O"),
					TimestampMilliseconds = timestampMilliseconds,
					SharedMemoryName = mediaPipeSharedMemoryFrameDescriptor.Name,
					SharedMemoryCapacityBytes = mediaPipeSharedMemoryFrameDescriptor.CapacityBytes,
					ImageByteLength = mediaPipeSharedMemoryFrameDescriptor.ImageByteLength,
					ImageWidth = mediaPipeSharedMemoryFrameDescriptor.Width,
					ImageHeight = mediaPipeSharedMemoryFrameDescriptor.Height,
					ImageStride = mediaPipeSharedMemoryFrameDescriptor.Stride,
					ImagePixelFormat = mediaPipeSharedMemoryFrameDescriptor.PixelFormat
				};
				string value = JsonSerializer.Serialize(mediaPipeSidecarRequest, JsonOptions);
				stopwatch2.Stop();
				Stopwatch stopwatch3 = Stopwatch.StartNew();
				_process.StandardInput.WriteLine(value);
				_process.StandardInput.Flush();
				Task<string> task = _process.StandardOutput.ReadLineAsync();
				TimeSpan timeout = (_firstResponseAfterStart ? TimeSpan.FromMilliseconds(ReadStartupTimeoutMilliseconds()) : _timeout);
				if (!task.Wait(timeout))
				{
					RestartAfterFailure("MediaPipe sidecar timed out waiting for a frame response.");
					return Error(Status);
				}
				_firstResponseAfterStart = false;
				string result = task.Result;
				stopwatch3.Stop();
				if (string.IsNullOrWhiteSpace(result))
				{
					RestartAfterFailure("MediaPipe sidecar closed its output stream.");
					return Error(Status);
				}
				Stopwatch stopwatch4 = Stopwatch.StartNew();
				MediaPipeSidecarResponse mediaPipeSidecarResponse = JsonSerializer.Deserialize<MediaPipeSidecarResponse>(result, JsonOptions);
				stopwatch4.Stop();
				if (mediaPipeSidecarResponse == null)
				{
					return Error("MediaPipe sidecar returned an empty response.");
				}
				if (!string.Equals(mediaPipeSidecarResponse.RequestId, mediaPipeSidecarRequest.RequestId, StringComparison.Ordinal))
				{
					return Error($"MediaPipe sidecar response id mismatch: expected {mediaPipeSidecarRequest.RequestId}, got {mediaPipeSidecarResponse.RequestId}.");
				}
				Status = mediaPipeSidecarResponse.Status;
				stopwatch.Stop();
				mediaPipeSidecarResponse.Diagnostics = new VisionPipelineDiagnostics
				{
					CapturedAtUtc = capturedAtUtc,
					Backend = "MediaPipe Face Landmarker",
					Mode = "video-tracking-shared-memory",
					SourceWidth = ((sourceWidth > 0) ? sourceWidth : bitmap.PixelWidth),
					SourceHeight = ((sourceHeight > 0) ? sourceHeight : bitmap.PixelHeight),
					InputWidth = bitmap.PixelWidth,
					InputHeight = bitmap.PixelHeight,
					EncodedPayloadBytes = mediaPipeSharedMemoryFrameDescriptor.ImageByteLength,
					HasFace = mediaPipeSidecarResponse.HasFace,
					ClientPrepareMilliseconds = stopwatch2.Elapsed.TotalMilliseconds,
					SidecarRoundTripMilliseconds = stopwatch3.Elapsed.TotalMilliseconds,
					ClientParseMilliseconds = stopwatch4.Elapsed.TotalMilliseconds,
					EndToEndMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
					SidecarStagesMilliseconds = mediaPipeSidecarResponse.TimingsMilliseconds,
					Status = mediaPipeSidecarResponse.Status
				};
				return mediaPipeSidecarResponse;
			}
			catch (Exception ex)
			{
				RestartAfterFailure("MediaPipe sidecar call failed: " + ex.Message);
				return Error(Status);
			}
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			StopProcess();
			_sharedMemoryFrame.Dispose();
		}
	}

	private bool EnsureProcess()
	{
		if (Volatile.Read(in _disposed) != 0)
		{
			Status = "MediaPipe sidecar client is stopped.";
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
			process2.StartInfo.ArgumentList.Add("--model");
			process2.StartInfo.ArgumentList.Add(_environment.ModelPath);
			if (!process2.Start())
			{
				Status = "MediaPipe sidecar process did not start.";
				return false;
			}
			_process = process2;
			Process runningProcess = process2;
			process2 = null;
			_firstResponseAfterStart = true;
			_lastTimestampMilliseconds = 0L;
			Task.Run(delegate
			{
				ReadErrors(runningProcess);
			});
			Status = "MediaPipe sidecar process started.";
			return true;
		}
		catch (Exception ex)
		{
			Status = "MediaPipe sidecar process failed to start: " + ex.Message;
			return false;
		}
		finally
		{
			process2?.Dispose();
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
						Status = text.Trim();
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

	private static MediaPipeSidecarResponse Error(string status)
	{
		return new MediaPipeSidecarResponse
		{
			Ok = false,
			Status = status
		};
	}

	private static int ReadTimeoutMilliseconds()
	{
		if (!int.TryParse(Environment.GetEnvironmentVariable("AVATAR_BUILDER_MEDIAPIPE_TIMEOUT_MS"), out var result))
		{
			return 1800;
		}
		return Math.Clamp(result, 250, 10000);
	}

	private static int ReadStartupTimeoutMilliseconds()
	{
		if (!int.TryParse(Environment.GetEnvironmentVariable("AVATAR_BUILDER_MEDIAPIPE_STARTUP_TIMEOUT_MS"), out var result))
		{
			return 10000;
		}
		return Math.Clamp(result, 1000, 30000);
	}
}
