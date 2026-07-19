using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxReconstructionClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ThreeDdfaOnnxSidecarEnvironment _environment;
    private readonly string _clientSessionId = Guid.NewGuid().ToString("N")[..12];
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;
    private Process? _process;
    private string _lastStandardError = "";
    private bool _firstResponseAfterStart;
    private int _requestNumber;
    private int _disposed;

    public ThreeDdfaOnnxReconstructionClient(ThreeDdfaOnnxSidecarEnvironment environment)
    {
        _environment = environment;
        _timeout = TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds());
    }

    public string Status { get; private set; } = "";

    public ThreeDdfaOnnxSidecarResponse Reconstruct(
        BitmapSource bitmap,
        DateTime capturedAtUtc,
        ThreeDdfaOnnxSidecarFaceBox? faceBox,
        ThreeDdfaOnnxRequestMode mode = ThreeDdfaOnnxRequestMode.Tracking,
        int denseSampleStride = 24)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
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
                var totalStopwatch = Stopwatch.StartNew();
                var prepareStopwatch = Stopwatch.StartNew();
                var jpeg = EncodeJpeg(bitmap);
                var request = new ThreeDdfaOnnxSidecarRequest
                {
                    RequestId = $"{_clientSessionId}-{Interlocked.Increment(ref _requestNumber):D6}",
                    CapturedAtUtc = capturedAtUtc.ToString("O"),
                    FaceBox = faceBox,
                    Mode = ToProtocolMode(mode),
                    DenseSampleStride = Math.Clamp(denseSampleStride, 1, 200),
                    ImageBase64 = Convert.ToBase64String(jpeg)
                };
                var line = JsonSerializer.Serialize(request, JsonOptions);
                prepareStopwatch.Stop();
                var roundTripStopwatch = Stopwatch.StartNew();
                _process!.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();

                var responseTask = _process.StandardOutput.ReadLineAsync();
                var responseTimeout = mode == ThreeDdfaOnnxRequestMode.Full
                    ? _firstResponseAfterStart
                        ? TimeSpan.FromSeconds(60)
                        : TimeSpan.FromSeconds(30)
                    : _firstResponseAfterStart
                        ? TimeSpan.FromMilliseconds(ReadStartupTimeoutMilliseconds())
                        : _timeout;
                if (!responseTask.Wait(responseTimeout))
                {
                    RestartAfterFailure("3DDFA/ONNX sidecar timed out waiting for a reconstruction response.");
                    return Error(Status);
                }
                _firstResponseAfterStart = false;

                var responseLine = responseTask.Result;
                roundTripStopwatch.Stop();
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    RestartAfterFailure("3DDFA/ONNX sidecar closed its output stream.");
                    return Error(Status);
                }

                var parseStopwatch = Stopwatch.StartNew();
                var response = JsonSerializer.Deserialize<ThreeDdfaOnnxSidecarResponse>(responseLine, JsonOptions);
                parseStopwatch.Stop();
                if (response is null)
                {
                    return Error("3DDFA/ONNX sidecar returned an empty response.");
                }

                response.ExpandCompactMeshData();

                if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    return Error($"3DDFA/ONNX sidecar response id mismatch: expected {request.RequestId}, got {response.RequestId}.");
                }

                Status = response.Status;
                totalStopwatch.Stop();
                response.Diagnostics = new VisionPipelineDiagnostics
                {
                    CapturedAtUtc = capturedAtUtc,
                    Backend = "3DDFA-V2 ONNX",
                    Mode = request.Mode,
                    SourceWidth = bitmap.PixelWidth,
                    SourceHeight = bitmap.PixelHeight,
                    InputWidth = bitmap.PixelWidth,
                    InputHeight = bitmap.PixelHeight,
                    EncodedPayloadBytes = jpeg.Length,
                    HasFace = response.HasFace,
                    ClientPrepareMilliseconds = prepareStopwatch.Elapsed.TotalMilliseconds,
                    SidecarRoundTripMilliseconds = roundTripStopwatch.Elapsed.TotalMilliseconds,
                    ClientParseMilliseconds = parseStopwatch.Elapsed.TotalMilliseconds,
                    EndToEndMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds,
                    SidecarStagesMilliseconds = response.TimingsMilliseconds,
                    Status = response.Status
                };
                return response;
            }
            catch (Exception ex)
            {
                var sidecarError = Volatile.Read(ref _lastStandardError);
                var detail = string.IsNullOrWhiteSpace(sidecarError)
                    ? ex.Message
                    : $"{ex.Message}. Sidecar: {sidecarError}";
                RestartAfterFailure($"3DDFA/ONNX sidecar call failed: {detail}");
                return Error(Status);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopProcess();
    }

    private bool EnsureProcess()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            Status = "3DDFA/ONNX sidecar client is stopped.";
            return false;
        }

        if (_process is { HasExited: false })
        {
            return true;
        }

        StopProcess();
        try
        {
            var process = new Process
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
            process.StartInfo.ArgumentList.Add(_environment.ScriptPath);
            process.StartInfo.ArgumentList.Add("--repo");
            process.StartInfo.ArgumentList.Add(_environment.RepositoryPath);
            process.StartInfo.ArgumentList.Add("--config");
            process.StartInfo.ArgumentList.Add(_environment.ConfigPath);

            if (!process.Start())
            {
                Status = "3DDFA/ONNX sidecar process did not start.";
                return false;
            }

            _process = process;
            _firstResponseAfterStart = true;
            Volatile.Write(ref _lastStandardError, "");
            _ = Task.Run(() => ReadErrors(process));
            Status = "3DDFA/ONNX sidecar process started.";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"3DDFA/ONNX sidecar process failed to start: {ex.Message}";
            return false;
        }
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(converted));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private void RestartAfterFailure(string status)
    {
        Status = status;
        StopProcess();
    }

    private void StopProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
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
                var line = process.StandardError.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var detail = line.Trim();
                    Volatile.Write(ref _lastStandardError, detail);
                    Status = detail;
                }
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
            _ => "tracking"
        };
    }

    private static int ReadTimeoutMilliseconds()
    {
        var configured = Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_TIMEOUT_MS");
        return int.TryParse(configured, out var milliseconds)
            ? Math.Clamp(milliseconds, 500, 30000)
            : 4500;
    }

    private static int ReadStartupTimeoutMilliseconds()
    {
        var configured = Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_STARTUP_TIMEOUT_MS");
        return int.TryParse(configured, out var milliseconds)
            ? Math.Clamp(milliseconds, 1000, 60000)
            : 25000;
    }
}
