using System.IO;
using System.Text.Json;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxModelInfo
{
    private const string RelativeModelDirectory = "dependencies/vision/3ddfa-onnx";
    private const string DefaultManifestFileName = "three_ddfa_onnx_manifest.json";
    private const string DefaultPrimaryModelFileName = "3DDFA_V2/TDDFA_ONNX.py";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string ModelDirectory { get; init; } = "";

    public string ManifestPath { get; init; } = "";

    public string PrimaryModelPath { get; init; } = "";

    public string Backend { get; init; } = "3DDFA/ONNX face reconstruction lane";

    public string BackendId { get; init; } = "";

    public string SourceRepository { get; init; } = "";

    public string Runtime { get; init; } = "";

    public IReadOnlyList<string> ModelFiles { get; init; } = [];

    public IReadOnlyList<IReadOnlyList<string>> AlternativeModelFileGroups { get; init; } = [];

    public IReadOnlyList<string> RuntimeFiles { get; init; } = [];

    public IReadOnlyList<string> ExpectedOutputs { get; init; } = [];

    public string InferenceImplementationStatus { get; init; } = "";

    public string ManifestStatus { get; init; } = "";

    public string Notes { get; init; } = "";

    public bool ManifestExists => File.Exists(ManifestPath);

    public bool PrimaryModelExists => File.Exists(PrimaryModelPath);

    public bool ModelFilesExist => ModelFiles.Count > 0
        && ModelFiles.All(file => File.Exists(BuildModelPath(file)))
        && AlternativeModelFileGroups.All(group => group.Any(file => File.Exists(BuildModelPath(file))));

    public bool RuntimeFilesExist => RuntimeFiles.Count == 0
        || RuntimeFiles.All(file => File.Exists(BuildModelPath(file)));

    public bool IsReady => ManifestExists
        && (ModelFiles.Count > 0 ? ModelFilesExist : PrimaryModelExists);

    public bool CanRunInference => IsReady
        && RuntimeFilesExist
        && string.Equals(InferenceImplementationStatus, "ready", StringComparison.OrdinalIgnoreCase);

    public string Status
    {
        get
        {
            if (!ManifestExists)
            {
                return "3DDFA/ONNX manifest missing";
            }

            if (!IsReady)
            {
                return "3DDFA/ONNX model bundle missing";
            }

            if (!RuntimeFilesExist)
            {
                return "3DDFA/ONNX runtime files missing";
            }

            if (!CanRunInference)
            {
                return string.IsNullOrWhiteSpace(InferenceImplementationStatus)
                    ? "3DDFA/ONNX model present; adapter not ready"
                    : $"3DDFA/ONNX model present; adapter {FormatStatus(InferenceImplementationStatus)}";
            }

            return "3DDFA/ONNX reconstruction lane ready";
        }
    }

    public static ThreeDdfaOnnxModelInfo Load()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            RelativeModelDirectory.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, DefaultManifestFileName);
        var primaryModelPath = BuildModelPath(directory, DefaultPrimaryModelFileName);
        if (!File.Exists(manifestPath))
        {
            return new ThreeDdfaOnnxModelInfo
            {
                ModelDirectory = directory,
                ManifestPath = manifestPath,
                PrimaryModelPath = primaryModelPath,
                ModelFiles = [DefaultPrimaryModelFileName]
            };
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(manifestPath),
                ManifestJsonOptions);
            var modelFiles = manifest?.ModelFiles?.Where(static file => !string.IsNullOrWhiteSpace(file)).ToList() ?? [];
            var primaryModel = string.IsNullOrWhiteSpace(manifest?.PrimaryModelFile)
                ? modelFiles.FirstOrDefault() ?? DefaultPrimaryModelFileName
                : manifest.PrimaryModelFile;
            if (modelFiles.Count == 0)
            {
                modelFiles.Add(primaryModel);
            }

            return new ThreeDdfaOnnxModelInfo
            {
                ModelDirectory = directory,
                ManifestPath = manifestPath,
                PrimaryModelPath = BuildModelPath(directory, primaryModel),
                Backend = string.IsNullOrWhiteSpace(manifest?.Backend) ? "3DDFA/ONNX face reconstruction lane" : manifest.Backend,
                BackendId = manifest?.BackendId ?? "",
                SourceRepository = manifest?.SourceRepository ?? "",
                Runtime = manifest?.Runtime ?? "",
                ModelFiles = modelFiles,
                AlternativeModelFileGroups = manifest?.AlternativeModelFileGroups ?? [],
                RuntimeFiles = manifest?.RuntimeFiles ?? [],
                ExpectedOutputs = manifest?.ExpectedOutputs ?? [],
                InferenceImplementationStatus = manifest?.InferenceImplementationStatus ?? "",
                ManifestStatus = manifest?.Status ?? "",
                Notes = manifest?.Notes ?? ""
            };
        }
        catch (Exception ex)
        {
            return new ThreeDdfaOnnxModelInfo
            {
                ModelDirectory = directory,
                ManifestPath = manifestPath,
                PrimaryModelPath = primaryModelPath,
                ManifestStatus = $"manifest unreadable: {ex.Message}",
                ModelFiles = [DefaultPrimaryModelFileName]
            };
        }
    }

    private static string FormatStatus(string status)
    {
        return status.Replace('_', ' ').Trim();
    }

    private string BuildModelPath(string relativePath)
    {
        return BuildModelPath(ModelDirectory, relativePath);
    }

    private static string BuildModelPath(string directory, string relativePath)
    {
        return Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class Manifest
    {
        public string Backend { get; init; } = "";

        public string BackendId { get; init; } = "";

        public string PrimaryModelFile { get; init; } = "";

        public IReadOnlyList<string> ModelFiles { get; init; } = [];

        public IReadOnlyList<IReadOnlyList<string>> AlternativeModelFileGroups { get; init; } = [];

        public string SourceRepository { get; init; } = "";

        public string Runtime { get; init; } = "";

        public IReadOnlyList<string> RuntimeFiles { get; init; } = [];

        public IReadOnlyList<string> ExpectedOutputs { get; init; } = [];

        public string InferenceImplementationStatus { get; init; } = "";

        public string Status { get; init; } = "";

        public string Notes { get; init; } = "";
    }
}
