using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Onnx;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class PoseAlignmentAuditor
{
    public const string HtmlFileName = "pose_alignment_audit.html";
    public const string JsonFileName = "pose_alignment_summary.json";
    public const string CsvFileName = "pose_alignment_samples.csv";
    private const int MinimumSampleCount = 30;
    private const int MaximumSampleCount = 600;
    private const double MinimumAxisRangeDegrees = 12d;
    private const double MinimumAbsoluteCorrelation = 0.80d;
    private const double MaximumP95ErrorDegrees = 8d;
    private const double MinimumNovelPoseDeltaDegrees = 1.25d;
    private static readonly TimeSpan MaximumDuplicatePoseInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportWriteInterval = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly List<PoseAlignmentSample> _samples = [];
    private string _folder = "";
    private PoseAlignmentSummary _summary = PoseAlignmentSummary.Waiting;
    private DateTime _lastReportWriteAtUtc = DateTime.MinValue;

    public PoseAlignmentSummary CurrentSummary
    {
        get
        {
            lock (_sync)
            {
                return _summary;
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
            _folder = Path.Combine(Path.GetFullPath(outputRoot), "Benchmarks");
            Directory.CreateDirectory(_folder);
            _samples.Clear();
            _samples.AddRange(ReadSamples(Path.Combine(_folder, CsvFileName)).TakeLast(MaximumSampleCount));
            _summary = BuildSummary(_samples);
            _lastReportWriteAtUtc = DateTime.MinValue;
            WriteReportLocked();
        }
    }

    public PoseAlignmentSummary Record(
        DateTime capturedAtUtc,
        PoseAngles mediaPipe,
        ThreeDdfaOnnxSidecarPose threeDdfa)
    {
        if (!mediaPipe.IsFinite
            || !double.IsFinite(threeDdfa.ARotationAroundXDegrees)
            || !double.IsFinite(threeDdfa.BRotationAroundYDegrees)
            || !double.IsFinite(threeDdfa.CRotationAroundZDegrees))
        {
            return CurrentSummary;
        }

        lock (_sync)
        {
            var sample = new PoseAlignmentSample
            {
                CapturedAtUtc = capturedAtUtc,
                MediaPipeA = mediaPipe.A,
                MediaPipeB = mediaPipe.B,
                MediaPipeC = mediaPipe.C,
                ThreeDdfaA = threeDdfa.ARotationAroundXDegrees,
                ThreeDdfaB = threeDdfa.BRotationAroundYDegrees,
                ThreeDdfaC = threeDdfa.CRotationAroundZDegrees
            };
            if (!ShouldRetainSample(sample))
            {
                return _summary;
            }

            _samples.Add(sample);
            if (_samples.Count > MaximumSampleCount)
            {
                _samples.RemoveRange(0, _samples.Count - MaximumSampleCount);
            }

            _summary = BuildSummary(_samples);
            if (DateTime.UtcNow - _lastReportWriteAtUtc >= ReportWriteInterval)
            {
                WriteReportLocked();
            }

            return _summary;
        }
    }

    private bool ShouldRetainSample(PoseAlignmentSample candidate)
    {
        if (_samples.Count < MinimumSampleCount)
        {
            return true;
        }

        var previous = _samples[^1];
        if (candidate.CapturedAtUtc - previous.CapturedAtUtc >= MaximumDuplicatePoseInterval)
        {
            return true;
        }

        return MaximumPoseDelta(candidate, previous) >= MinimumNovelPoseDeltaDegrees;
    }

    private static double MaximumPoseDelta(PoseAlignmentSample current, PoseAlignmentSample previous)
    {
        return new[]
        {
            Math.Abs(current.MediaPipeA - previous.MediaPipeA),
            Math.Abs(current.MediaPipeB - previous.MediaPipeB),
            Math.Abs(current.MediaPipeC - previous.MediaPipeC),
            Math.Abs(current.ThreeDdfaA - previous.ThreeDdfaA),
            Math.Abs(current.ThreeDdfaB - previous.ThreeDdfaB),
            Math.Abs(current.ThreeDdfaC - previous.ThreeDdfaC)
        }.Max();
    }

    public string GetHtmlPath()
    {
        lock (_sync)
        {
            return string.IsNullOrWhiteSpace(_folder) ? "" : Path.Combine(_folder, HtmlFileName);
        }
    }

    public void EnsureReport()
    {
        lock (_sync)
        {
            WriteReportLocked();
        }
    }

    private static PoseAlignmentSummary BuildSummary(IReadOnlyList<PoseAlignmentSample> samples)
    {
        var a = FitAxis("A around X", samples.Select(static sample => (sample.MediaPipeA, sample.ThreeDdfaA)).ToList());
        var b = FitAxis("B around Y", samples.Select(static sample => (sample.MediaPipeB, sample.ThreeDdfaB)).ToList());
        var c = FitAxis("C around Z", samples.Select(static sample => (sample.MediaPipeC, sample.ThreeDdfaC)).ToList());
        var axes = new[] { a, b, c };
        var ready = axes.All(static axis => axis.Ready);
        var guidance = ready
            ? "A/B/C agreement is measured and within tolerance. The comparison is ready for diagnostic review."
            : BuildGuidance(axes, samples.Count);
        return new PoseAlignmentSummary
        {
            UpdatedAtUtc = DateTime.UtcNow,
            SampleCount = samples.Count,
            ReadyForComparison = ready,
            Status = ready ? "aligned" : "collecting alignment evidence",
            Guidance = guidance,
            A = a,
            B = b,
            C = c
        };
    }

    private static PoseAxisAlignment FitAxis(string name, IReadOnlyList<(double X, double Y)> pairs)
    {
        if (pairs.Count == 0)
        {
            return new PoseAxisAlignment { Name = name, Status = "waiting for paired samples" };
        }

        var meanX = pairs.Average(static pair => pair.X);
        var meanY = pairs.Average(static pair => pair.Y);
        var varianceX = pairs.Sum(pair => Math.Pow(pair.X - meanX, 2d));
        var varianceY = pairs.Sum(pair => Math.Pow(pair.Y - meanY, 2d));
        var covariance = pairs.Sum(pair => (pair.X - meanX) * (pair.Y - meanY));
        var scale = varianceX <= 0.000001d ? 0d : covariance / varianceX;
        var offset = meanY - scale * meanX;
        var correlation = varianceX <= 0.000001d || varianceY <= 0.000001d
            ? 0d
            : covariance / Math.Sqrt(varianceX * varianceY);
        var errors = pairs
            .Select(pair => Math.Abs(scale * pair.X + offset - pair.Y))
            .Order()
            .ToList();
        var rangeX = pairs.Max(static pair => pair.X) - pairs.Min(static pair => pair.X);
        var rangeY = pairs.Max(static pair => pair.Y) - pairs.Min(static pair => pair.Y);
        var p95 = Percentile(errors, 0.95d);
        var meanError = errors.Average();
        var enoughSamples = pairs.Count >= MinimumSampleCount;
        var enoughMotion = rangeX >= MinimumAxisRangeDegrees && rangeY >= MinimumAxisRangeDegrees;
        var correlated = Math.Abs(correlation) >= MinimumAbsoluteCorrelation;
        var errorAccepted = p95 <= MaximumP95ErrorDegrees;
        var scaleAccepted = Math.Abs(scale) is >= 0.35d and <= 2.75d;
        var ready = enoughSamples && enoughMotion && correlated && errorAccepted && scaleAccepted;
        var status = !enoughSamples
            ? $"need {MinimumSampleCount - pairs.Count} more paired samples"
            : !enoughMotion
                ? "need more movement around this axis"
                : !correlated || !scaleAccepted
                    ? "the packages do not yet track this axis consistently"
                    : !errorAccepted
                        ? "residual disagreement is above tolerance"
                        : "aligned within tolerance";
        return new PoseAxisAlignment
        {
            Name = name,
            SampleCount = pairs.Count,
            Scale = Round(scale),
            OffsetDegrees = Round(offset),
            Correlation = Round(correlation),
            MediaPipeRangeDegrees = Round(rangeX),
            ThreeDdfaRangeDegrees = Round(rangeY),
            MeanAbsoluteErrorDegrees = Round(meanError),
            P95AbsoluteErrorDegrees = Round(p95),
            Ready = ready,
            Status = status
        };
    }

    private static string BuildGuidance(IReadOnlyList<PoseAxisAlignment> axes, int sampleCount)
    {
        if (sampleCount < MinimumSampleCount)
        {
            return $"Keep your face visible and slowly move through A, B, and C. {MinimumSampleCount - sampleCount} more same-frame pairs are needed before alignment can be judged.";
        }

        var waiting = axes.Where(static axis => !axis.Ready).Select(static axis => $"{axis.Name}: {axis.Status}");
        return "Alignment is not ready: " + string.Join("; ", waiting) + ".";
    }

    private void WriteSamplesLocked()
    {
        if (string.IsNullOrWhiteSpace(_folder))
        {
            return;
        }

        var path = Path.Combine(_folder, CsvFileName);
        var csv = new StringBuilder("capturedAtUtc,mediaPipeA,mediaPipeB,mediaPipeC,threeDdfaA,threeDdfaB,threeDdfaC\n");
        foreach (var sample in _samples)
        {
            csv.AppendLine(string.Join(",",
                sample.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                Number(sample.MediaPipeA),
                Number(sample.MediaPipeB),
                Number(sample.MediaPipeC),
                Number(sample.ThreeDdfaA),
                Number(sample.ThreeDdfaB),
                Number(sample.ThreeDdfaC)));
        }

        AtomicTextFileWriter.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private void WriteReportLocked()
    {
        if (string.IsNullOrWhiteSpace(_folder))
        {
            return;
        }

        Directory.CreateDirectory(_folder);
        var json = JsonSerializer.Serialize(_summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        WriteSamplesLocked();
        AtomicTextFileWriter.WriteAllText(Path.Combine(_folder, JsonFileName), json, Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(Path.Combine(_folder, HtmlFileName), BuildHtml(_summary, _samples.TakeLast(60).ToList()), Encoding.UTF8);
        _lastReportWriteAtUtc = DateTime.UtcNow;
    }

    private static IReadOnlyList<PoseAlignmentSample> ReadSamples(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var samples = new List<PoseAlignmentSample>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length != 7
                || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var capturedAtUtc)
                || !TryNumbers(parts.Skip(1), out var values))
            {
                continue;
            }

            samples.Add(new PoseAlignmentSample
            {
                CapturedAtUtc = capturedAtUtc,
                MediaPipeA = values[0],
                MediaPipeB = values[1],
                MediaPipeC = values[2],
                ThreeDdfaA = values[3],
                ThreeDdfaB = values[4],
                ThreeDdfaC = values[5]
            });
        }

        return samples;
    }

    private static bool TryNumbers(IEnumerable<string> parts, out double[] values)
    {
        values = parts.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        return values.Length == 6 && values.All(double.IsFinite);
    }

    private static string BuildHtml(PoseAlignmentSummary summary, IReadOnlyList<PoseAlignmentSample> recent)
    {
        var axisRows = string.Concat(new[] { summary.A, summary.B, summary.C }.Select(axis =>
            $"<tr><td>{H(axis.Name)}</td><td>{axis.SampleCount}</td><td>{axis.MediaPipeRangeDegrees:0.#} / {axis.ThreeDdfaRangeDegrees:0.#}</td><td>{axis.Scale:0.###} x + {axis.OffsetDegrees:0.###}</td><td>{axis.Correlation:0.###}</td><td>{axis.MeanAbsoluteErrorDegrees:0.##} / {axis.P95AbsoluteErrorDegrees:0.##}</td><td class=\"{(axis.Ready ? "good" : "warn") }\">{H(axis.Status)}</td></tr>"));
        var sampleRows = string.Concat(recent.OrderByDescending(static sample => sample.CapturedAtUtc).Select(sample =>
        {
            var calibratedA = summary.A.Scale * sample.MediaPipeA + summary.A.OffsetDegrees;
            var calibratedB = summary.B.Scale * sample.MediaPipeB + summary.B.OffsetDegrees;
            var calibratedC = summary.C.Scale * sample.MediaPipeC + summary.C.OffsetDegrees;
            return $"<tr><td>{H(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{sample.MediaPipeA:0.#}/{sample.MediaPipeB:0.#}/{sample.MediaPipeC:0.#}</td><td>{calibratedA:0.#}/{calibratedB:0.#}/{calibratedC:0.#}</td><td>{sample.ThreeDdfaA:0.#}/{sample.ThreeDdfaB:0.#}/{sample.ThreeDdfaC:0.#}</td><td>{Math.Abs(calibratedA - sample.ThreeDdfaA):0.#}/{Math.Abs(calibratedB - sample.ThreeDdfaB):0.#}/{Math.Abs(calibratedC - sample.ThreeDdfaC):0.#}</td></tr>";
        }));
        return $$$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="refresh" content="5"><title>A/B/C Alignment Audit</title><style>:root{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--good:#80e0a4;--warn:#ffd27a}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}main{max-width:1300px;margin:auto;padding:18px}.panel{border:1px solid var(--line);background:var(--panel);padding:14px;margin:0 0 14px;border-radius:6px}.good{color:var(--good)}.warn{color:var(--warn)}.muted{color:var(--muted)}h1{margin:0 0 6px;font-size:24px}table{width:100%;border-collapse:collapse}td,th{border-bottom:1px solid #1c3042;padding:7px 5px;text-align:left;vertical-align:top}th{color:var(--muted)}code{color:#b9d7ef}</style></head><body><main><section class="panel"><h1>A/B/C Alignment Audit</h1><p class="{{{(summary.ReadyForComparison ? "good" : "warn")}}}"><strong>{{{H(summary.Status)}}}</strong></p><p>{{{H(summary.Guidance)}}}</p><p class="muted">{{{summary.SampleCount}}} exact-frame MediaPipe/3DDFA pairs. MediaPipe is transformed to 3DDFA with the measured equation shown below; raw samples remain in <code>{{{CsvFileName}}}</code>.</p></section><section class="panel"><table><tr><th>Axis</th><th>Pairs</th><th>Motion range MP / 3DDFA</th><th>Measured transform</th><th>Correlation</th><th>Error mean / p95</th><th>Decision</th></tr>{{{axisRows}}}</table></section><section class="panel"><h2>Recent Exact-Frame Pairs</h2><table><tr><th>Time</th><th>MediaPipe raw A/B/C</th><th>MediaPipe calibrated A/B/C</th><th>3DDFA A/B/C</th><th>Absolute error A/B/C</th></tr>{{{sampleRows}}}</table></section></main></body></html>
""";
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0d;
        }

        var position = (sorted.Count - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 6) : 0d;

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
}

public sealed record PoseAngles(double A, double B, double C)
{
    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C);
}

public sealed class PoseAlignmentSample
{
    public DateTime CapturedAtUtc { get; init; }
    public double MediaPipeA { get; init; }
    public double MediaPipeB { get; init; }
    public double MediaPipeC { get; init; }
    public double ThreeDdfaA { get; init; }
    public double ThreeDdfaB { get; init; }
    public double ThreeDdfaC { get; init; }
}

public sealed class PoseAlignmentSummary
{
    public static PoseAlignmentSummary Waiting { get; } = new();
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public int SampleCount { get; init; }
    public bool ReadyForComparison { get; init; }
    public string Status { get; init; } = "waiting for exact-frame A/B/C pairs";
    public string Guidance { get; init; } = "Start avatar capture with MediaPipe selected, then slowly move through A, B, and C.";
    public PoseAxisAlignment A { get; init; } = new() { Name = "A around X" };
    public PoseAxisAlignment B { get; init; } = new() { Name = "B around Y" };
    public PoseAxisAlignment C { get; init; } = new() { Name = "C around Z" };
}

public sealed class PoseAxisAlignment
{
    public string Name { get; init; } = "";
    public int SampleCount { get; init; }
    public double Scale { get; init; }
    public double OffsetDegrees { get; init; }
    public double Correlation { get; init; }
    public double MediaPipeRangeDegrees { get; init; }
    public double ThreeDdfaRangeDegrees { get; init; }
    public double MeanAbsoluteErrorDegrees { get; init; }
    public double P95AbsoluteErrorDegrees { get; init; }
    public bool Ready { get; init; }
    public string Status { get; init; } = "waiting";
}
