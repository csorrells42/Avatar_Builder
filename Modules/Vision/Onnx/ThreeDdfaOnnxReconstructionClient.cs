using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxReconstructionClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ThreeDdfaOnnxSidecarEnvironment _environment;
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;
    private Process? _process;
    private bool _firstResponseAfterStart;
    private int _requestNumber;

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
        bool returnDenseVertices = false,
        int denseSampleStride = 24)
    {
        lock (_sync)
        {
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
                var request = new ThreeDdfaOnnxSidecarRequest
                {
                    RequestId = Interlocked.Increment(ref _requestNumber).ToString("D6"),
                    CapturedAtUtc = capturedAtUtc.ToString("O"),
                    FaceBox = faceBox,
                    ReturnDenseVertices = returnDenseVertices,
                    DenseSampleStride = Math.Clamp(denseSampleStride, 1, 200),
                    ImageBase64 = Convert.ToBase64String(EncodeJpeg(bitmap))
                };
                var line = JsonSerializer.Serialize(request, JsonOptions);
                _process!.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();

                var responseTask = _process.StandardOutput.ReadLineAsync();
                var responseTimeout = _firstResponseAfterStart
                    ? TimeSpan.FromMilliseconds(ReadStartupTimeoutMilliseconds())
                    : _timeout;
                if (!responseTask.Wait(responseTimeout))
                {
                    RestartAfterFailure("3DDFA/ONNX sidecar timed out waiting for a reconstruction response.");
                    return Error(Status);
                }
                _firstResponseAfterStart = false;

                var responseLine = responseTask.Result;
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    RestartAfterFailure("3DDFA/ONNX sidecar closed its output stream.");
                    return Error(Status);
                }

                var response = JsonSerializer.Deserialize<ThreeDdfaOnnxSidecarResponse>(responseLine, JsonOptions);
                if (response is null)
                {
                    return Error("3DDFA/ONNX sidecar returned an empty response.");
                }

                if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    return Error($"3DDFA/ONNX sidecar response id mismatch: expected {request.RequestId}, got {response.RequestId}.");
                }

                Status = response.Status;
                return response;
            }
            catch (Exception ex)
            {
                RestartAfterFailure($"3DDFA/ONNX sidecar call failed: {ex.Message}");
                return Error(Status);
            }
        }
    }

    public void Dispose()
    {
        StopProcess();
    }

    private bool EnsureProcess()
    {
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
        var process = _process;
        _process = null;
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
                    Status = line.Trim();
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
