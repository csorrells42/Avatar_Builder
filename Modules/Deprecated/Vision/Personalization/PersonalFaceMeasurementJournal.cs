using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceMeasurementJournal
{
    public const long DefaultBudgetBytes = 10_000_000_000L;
    public const int DefaultRecentSampleReadLimit = 50_000;
    private const string MeasurementsFolderName = "measurements";
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly TimeSpan _minimumWriteInterval;
    private readonly long _budgetBytes;
    private DateTime _lastWriteAtUtc = DateTime.MinValue;

    public PersonalFaceMeasurementJournal()
        : this(TimeSpan.FromSeconds(2), DefaultBudgetBytes)
    {
    }

    public PersonalFaceMeasurementJournal(TimeSpan minimumWriteInterval, long budgetBytes)
    {
        _minimumWriteInterval = minimumWriteInterval < TimeSpan.Zero ? TimeSpan.Zero : minimumWriteInterval;
        _budgetBytes = Math.Max(1_000_000L, budgetBytes);
    }

    public string WriteAcceptedSampleIfDue(
        string personalModelFolder,
        PersonalFaceModelUpdate update,
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        FaceLockStabilityAnalysis stability,
        PersonalFaceCaptureQualityAssessment captureQuality,
        HeadPoseEstimate? headPose = null)
    {
        ArgumentNullException.ThrowIfNull(captureQuality);

        if (!update.Accepted || update.SampleWeight <= 0d || !captureQuality.CanCollectMeasurements)
        {
            return "";
        }

        var capturedAtUtc = metrics.CapturedAtUtc != default
            ? metrics.CapturedAtUtc
            : frame.CapturedAtUtc != default ? frame.CapturedAtUtc : DateTime.UtcNow;
        if (_lastWriteAtUtc != DateTime.MinValue && capturedAtUtc - _lastWriteAtUtc < _minimumWriteInterval)
        {
            return "";
        }

        var measurementsFolder = Path.Combine(personalModelFolder, MeasurementsFolderName);
        Directory.CreateDirectory(measurementsFolder);
        var path = Path.Combine(measurementsFolder, $"{capturedAtUtc:yyyy-MM-dd}.jsonl");
        var sample = PersonalFaceMeasurementSample.Create(update, frame, metrics, stability, captureQuality, headPose);
        File.AppendAllText(path, JsonSerializer.Serialize(sample, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        _lastWriteAtUtc = capturedAtUtc;
        EnforceBudget(measurementsFolder, _budgetBytes);
        return path;
    }

    public static long GetMeasurementsSizeBytes(string personalModelFolder)
    {
        var measurementsFolder = GetMeasurementsFolder(personalModelFolder);
        return Directory.Exists(measurementsFolder)
            ? Directory.EnumerateFiles(measurementsFolder, "*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path).Length)
                .Sum()
            : 0L;
    }

    public static PersonalFaceMeasurementBudgetReport EnforceBudgetForModelFolder(
        string personalModelFolder,
        long budgetBytes = DefaultBudgetBytes)
    {
        return EnforceBudget(GetMeasurementsFolder(personalModelFolder), budgetBytes);
    }

    public static IReadOnlyList<PersonalFaceMeasurementSample> ReadRecentSamples(
        string personalModelFolder,
        int maxSamples = DefaultRecentSampleReadLimit)
    {
        var measurementsFolder = GetMeasurementsFolder(personalModelFolder);
        if (!Directory.Exists(measurementsFolder) || maxSamples <= 0)
        {
            return [];
        }

        var samples = new List<PersonalFaceMeasurementSample>(Math.Min(maxSamples, 4096));
        foreach (var file in Directory
            .EnumerateFiles(measurementsFolder, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static file => file.LastWriteTimeUtc))
        {
            foreach (var line in ReadLinesNewestFirst(file.FullName))
            {
                if (samples.Count >= maxSamples)
                {
                    return samples
                        .Where(static sample => sample.CapturedAtUtc != default)
                        .OrderBy(static sample => sample.CapturedAtUtc)
                        .ToList();
                }

                var sample = TryReadSample(line);
                if (sample is not null)
                {
                    samples.Add(sample);
                }
            }
        }

        return samples
            .Where(static sample => sample.CapturedAtUtc != default)
            .OrderBy(static sample => sample.CapturedAtUtc)
            .ToList();
    }

    public static PersonalFaceMeasurementBudgetReport EnforceBudget(string measurementsFolder, long budgetBytes)
    {
        if (!Directory.Exists(measurementsFolder))
        {
            return new PersonalFaceMeasurementBudgetReport(0L, 0L, Math.Max(1_000_000L, budgetBytes), 0);
        }

        var files = Directory
            .EnumerateFiles(measurementsFolder, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(static path => new FileInfo(path))
            .OrderBy(static file => file.LastWriteTimeUtc)
            .ToList();
        var totalBytes = files.Sum(static file => file.Length);
        var beforeBytes = totalBytes;
        var targetBytes = Math.Max(1_000_000L, budgetBytes);
        var deletedFiles = 0;
        for (var index = 0; index < files.Count && totalBytes > targetBytes; index++)
        {
            var file = files[index];
            totalBytes -= file.Length;
            file.Delete();
            deletedFiles++;
        }

        return new PersonalFaceMeasurementBudgetReport(beforeBytes, Math.Max(0L, totalBytes), targetBytes, deletedFiles);
    }

    private static string GetMeasurementsFolder(string personalModelFolder)
    {
        return Path.Combine(personalModelFolder, MeasurementsFolderName);
    }

    private static IEnumerable<string> ReadLinesNewestFirst(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            yield break;
        }

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                yield return lines[index];
            }
        }
    }

    private static PersonalFaceMeasurementSample? TryReadSample(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<PersonalFaceMeasurementSample>(line, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record PersonalFaceMeasurementBudgetReport(
    long BytesBefore,
    long BytesAfter,
    long BudgetBytes,
    int DeletedFileCount);
