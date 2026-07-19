using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class VisionBenchmarkRecorder : IDisposable
{
    private const int MaximumPendingSampleCount = 5000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly ConcurrentQueue<VisionPipelineDiagnostics> _pending = new();
    private readonly object _folderLock = new();
    private readonly object _flushLock = new();
    private readonly Timer _timer;
    private string _benchmarkFolder = "";
    private int _flushRunning;
    private bool _disposed;

    public VisionBenchmarkRecorder()
    {
        _timer = new Timer(_ => QueueFlush(), null, FlushInterval, FlushInterval);
    }

    public void SetOutputRoot(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return;
        }

        FlushPending();
        lock (_folderLock)
        {
            _benchmarkFolder = Path.Combine(Path.GetFullPath(outputRoot), "Benchmarks");
        }
    }

    public void Record(VisionPipelineDiagnostics diagnostics)
    {
        if (_disposed || diagnostics == VisionPipelineDiagnostics.None || diagnostics.EndToEndMilliseconds <= 0d)
        {
            return;
        }

        _pending.Enqueue(diagnostics);
        while (_pending.Count > MaximumPendingSampleCount && _pending.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        FlushPending();
    }

    private void QueueFlush()
    {
        if (_disposed || _pending.IsEmpty || Interlocked.Exchange(ref _flushRunning, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                FlushPending();
            }
            finally
            {
                Interlocked.Exchange(ref _flushRunning, 0);
            }
        });
    }

    private void FlushPending()
    {
        lock (_flushLock)
        {
            FlushPendingLocked();
        }
    }

    private void FlushPendingLocked()
    {
        string folder;
        lock (_folderLock)
        {
            folder = _benchmarkFolder;
        }

        if (string.IsNullOrWhiteSpace(folder) || _pending.IsEmpty)
        {
            return;
        }

        var samples = new List<VisionPipelineDiagnostics>();
        while (_pending.TryDequeue(out var sample))
        {
            samples.Add(sample);
        }

        if (samples.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            foreach (var group in samples.GroupBy(static sample => sample.CapturedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
            {
                var path = Path.Combine(folder, $"vision-pipeline-{group.Key}.csv");
                var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0L;
                using var writer = new StreamWriter(path, append: true, Utf8WithoutBom);
                if (writeHeader)
                {
                    writer.WriteLine("capturedAtUtc,backend,mode,sourceWidth,sourceHeight,inputWidth,inputHeight,payloadBytes,hasFace,clientPrepareMs,decodeMs,faceBoxMs,inferenceMs,parametersMs,sparseMs,denseMs,poseMs,serializeMs,sidecarTotalMs,roundTripMs,parseMs,endToEndMs,status");
                }

                foreach (var sample in group)
                {
                    writer.WriteLine(ToCsv(sample));
                }
            }
        }
        catch
        {
            foreach (var sample in samples)
            {
                _pending.Enqueue(sample);
            }
        }
    }

    private static string ToCsv(VisionPipelineDiagnostics sample)
    {
        return string.Join(",",
            Csv(sample.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            Csv(sample.Backend),
            Csv(sample.Mode),
            sample.SourceWidth.ToString(CultureInfo.InvariantCulture),
            sample.SourceHeight.ToString(CultureInfo.InvariantCulture),
            sample.InputWidth.ToString(CultureInfo.InvariantCulture),
            sample.InputHeight.ToString(CultureInfo.InvariantCulture),
            sample.EncodedPayloadBytes.ToString(CultureInfo.InvariantCulture),
            sample.HasFace ? "true" : "false",
            Number(sample.ClientPrepareMilliseconds),
            Stage(sample, "decode"),
            Stage(sample, "face_box", "faceBox"),
            Stage(sample, "inference"),
            Stage(sample, "parameters"),
            Stage(sample, "sparse"),
            Stage(sample, "dense"),
            Stage(sample, "pose"),
            Stage(sample, "serialize"),
            Stage(sample, "total"),
            Number(sample.SidecarRoundTripMilliseconds),
            Number(sample.ClientParseMilliseconds),
            Number(sample.EndToEndMilliseconds),
            Csv(sample.Status));
    }

    private static string Stage(VisionPipelineDiagnostics sample, params string[] names)
    {
        foreach (var name in names)
        {
            if (sample.SidecarStagesMilliseconds.TryGetValue(name, out var value))
            {
                return Number(value);
            }
        }

        return "";
    }

    private static string Number(double value)
    {
        return double.IsFinite(value) ? value.ToString("0.####", CultureInfo.InvariantCulture) : "";
    }

    private static string Csv(string value)
    {
        return $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
    }
}
