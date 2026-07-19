using AvatarBuilder.Modules.Vision.Common;
using System.IO;
using System.Text.Json;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class DenseFaceLandmarkModelInfo
{
    private const string RelativeModelDirectory = "dependencies/vision/dense-face-landmarks";
    private const string DefaultManifestFileName = "face_landmarker_manifest.json";
    private const string DefaultModelFileName = "face_landmarker.task";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string ModelDirectory { get; init; } = "";

    public string ManifestPath { get; init; } = "";

    public string ModelPath { get; init; } = "";

    public string Backend { get; init; } = "Dense face landmark backend";

    public int ExpectedLandmarks { get; init; }

    public IReadOnlyList<string> Outputs { get; init; } = [];

    public string ModelUrl { get; init; } = "";

    public string Sha256 { get; init; } = "";

    public string Runtime { get; init; } = "";

    public IReadOnlyList<string> RuntimeFiles { get; init; } = [];

    public string InferenceImplementationStatus { get; init; } = "";

    public string ManifestStatus { get; init; } = "";

    public string Notes { get; init; } = "";

    public bool ManifestExists => File.Exists(ManifestPath);

    public bool ModelExists => File.Exists(ModelPath);

    public bool IsReady => ManifestExists && ModelExists;

    public bool RuntimeFilesExist => RuntimeFiles.Count > 0
        && RuntimeFiles.All(file => File.Exists(Path.Combine(ModelDirectory, file)));

    public bool CanRunInference => IsReady
        && string.Equals(InferenceImplementationStatus, "ready", StringComparison.OrdinalIgnoreCase)
        && (RuntimeFiles.Count == 0 || RuntimeFilesExist);

    public string Status
    {
        get
        {
            if (!ManifestExists)
            {
                return "dense landmark manifest missing";
            }

            if (!ModelExists)
            {
                return "dense landmark model missing";
            }

            if (!CanRunInference)
            {
                return string.IsNullOrWhiteSpace(InferenceImplementationStatus)
                    ? "dense landmark model present; inference runtime not ready"
                    : $"dense landmark model present; inference {FormatInferenceStatus(InferenceImplementationStatus)}";
            }

            return "dense landmark model and runtime ready";
        }
    }

    private static string FormatInferenceStatus(string status)
    {
        status = status.Replace('_', ' ').Trim();
        return status.StartsWith("runtime ", StringComparison.OrdinalIgnoreCase)
            ? status
            : $"runtime {status}";
    }

    public static DenseFaceLandmarkModelInfo Load()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            RelativeModelDirectory.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, DefaultManifestFileName);
        var modelPath = Path.Combine(directory, DefaultModelFileName);
        if (!File.Exists(manifestPath))
        {
            return new DenseFaceLandmarkModelInfo
            {
                ModelDirectory = directory,
                ManifestPath = manifestPath,
                ModelPath = modelPath
            };
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(manifestPath),
                ManifestJsonOptions);
            var modelFile = string.IsNullOrWhiteSpace(manifest?.ModelFile)
                ? DefaultModelFileName
                : manifest.ModelFile;
            return new DenseFaceLandmarkModelInfo
            {
                ModelDirectory = directory,
                ManifestPath = manifestPath,
                ModelPath = Path.Combine(directory, modelFile),
                Backend = string.IsNullOrWhiteSpace(manifest?.Backend) ? "Dense face landmark backend" : manifest.Backend,
                ExpectedLandmarks = manifest?.ExpectedLandmarks ?? 0,
                Outputs = manifest?.Outputs ?? [],
                ModelUrl = manifest?.ModelUrl ?? "",
                Sha256 = manifest?.Sha256 ?? "",
                Runtime = manifest?.Runtime ?? "",
                RuntimeFiles = manifest?.RuntimeFiles ?? [],
                InferenceImplementationStatus = manifest?.InferenceImplementationStatus ?? "",
                ManifestStatus = manifest?.Status ?? "",
                Notes = manifest?.Notes ?? ""
            };
        }
        catch (Exception ex)
        {
            return new DenseFaceLandmarkModelInfo
            {
                ModelDirectory = directory,
                ManifestPath = manifestPath,
                ModelPath = modelPath,
                ManifestStatus = $"manifest unreadable: {ex.Message}"
            };
        }
    }

    private sealed class Manifest
    {
        public string Backend { get; init; } = "";

        public string ModelFile { get; init; } = "";

        public string ModelUrl { get; init; } = "";

        public string Sha256 { get; init; } = "";

        public int ExpectedLandmarks { get; init; }

        public IReadOnlyList<string> Outputs { get; init; } = [];

        public string Runtime { get; init; } = "";

        public IReadOnlyList<string> RuntimeFiles { get; init; } = [];

        public string InferenceImplementationStatus { get; init; } = "";

        public string Status { get; init; } = "";

        public string Notes { get; init; } = "";
    }
}
