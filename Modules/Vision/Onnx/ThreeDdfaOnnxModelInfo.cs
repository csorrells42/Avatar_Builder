using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxModelInfo
{
	private sealed class Manifest
	{
		public string Backend { get; init; } = "";

		public string BackendId { get; init; } = "";

		public string PrimaryModelFile { get; init; } = "";

		public IReadOnlyList<string> ModelFiles { get; init; } = Array.Empty<string>();

		public IReadOnlyList<IReadOnlyList<string>> AlternativeModelFileGroups { get; init; } = Array.Empty<IReadOnlyList<string>>();

		public string SourceRepository { get; init; } = "";

		public string Runtime { get; init; } = "";

		public IReadOnlyList<string> RuntimeFiles { get; init; } = Array.Empty<string>();

		public IReadOnlyList<string> ExpectedOutputs { get; init; } = Array.Empty<string>();

		public string InferenceImplementationStatus { get; init; } = "";

		public string Status { get; init; } = "";

		public string Notes { get; init; } = "";
	}

	private const string RelativeModelDirectory = "dependencies/vision/3ddfa-onnx";

	private const string DefaultManifestFileName = "three_ddfa_onnx_manifest.json";

	private const string DefaultPrimaryModelFileName = "3DDFA_V2/TDDFA_ONNX.py";

	private static readonly JsonSerializerOptions ManifestJsonOptions = new JsonSerializerOptions
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

	public IReadOnlyList<string> ModelFiles { get; init; } = Array.Empty<string>();

	public IReadOnlyList<IReadOnlyList<string>> AlternativeModelFileGroups { get; init; } = Array.Empty<IReadOnlyList<string>>();

	public IReadOnlyList<string> RuntimeFiles { get; init; } = Array.Empty<string>();

	public IReadOnlyList<string> ExpectedOutputs { get; init; } = Array.Empty<string>();

	public string InferenceImplementationStatus { get; init; } = "";

	public string ManifestStatus { get; init; } = "";

	public string Notes { get; init; } = "";

	public bool ManifestExists => File.Exists(ManifestPath);

	public bool PrimaryModelExists => File.Exists(PrimaryModelPath);

	public bool ModelFilesExist
	{
		get
		{
			if (ModelFiles.Count > 0 && ModelFiles.All((string file) => File.Exists(BuildModelPath(file))))
			{
				return AlternativeModelFileGroups.All((IReadOnlyList<string> group) => group.Any((string file) => File.Exists(BuildModelPath(file))));
			}
			return false;
		}
	}

	public bool RuntimeFilesExist
	{
		get
		{
			if (RuntimeFiles.Count != 0)
			{
				return RuntimeFiles.All((string file) => File.Exists(BuildModelPath(file)));
			}
			return true;
		}
	}

	public bool IsReady
	{
		get
		{
			if (ManifestExists)
			{
				if (ModelFiles.Count <= 0)
				{
					return PrimaryModelExists;
				}
				return ModelFilesExist;
			}
			return false;
		}
	}

	public bool CanRunInference
	{
		get
		{
			if (IsReady && RuntimeFilesExist)
			{
				return string.Equals(InferenceImplementationStatus, "ready", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}

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
				if (!string.IsNullOrWhiteSpace(InferenceImplementationStatus))
				{
					return "3DDFA/ONNX model present; adapter " + FormatStatus(InferenceImplementationStatus);
				}
				return "3DDFA/ONNX model present; adapter not ready";
			}
			return "3DDFA/ONNX reconstruction lane ready";
		}
	}

	public static ThreeDdfaOnnxModelInfo Load()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "dependencies/vision/3ddfa-onnx".Replace('/', Path.DirectorySeparatorChar));
		string text2 = Path.Combine(text, "three_ddfa_onnx_manifest.json");
		string primaryModelPath = BuildModelPath(text, "3DDFA_V2/TDDFA_ONNX.py");
		if (!File.Exists(text2))
		{
			return new ThreeDdfaOnnxModelInfo
			{
				ModelDirectory = text,
				ManifestPath = text2,
				PrimaryModelPath = primaryModelPath,
				ModelFiles = ["3DDFA_V2/TDDFA_ONNX.py"]
			};
		}
		try
		{
			Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(text2), ManifestJsonOptions);
			List<string> list = manifest?.ModelFiles?.Where((string file) => !string.IsNullOrWhiteSpace(file)).ToList() ?? new List<string>();
			string text3 = (string.IsNullOrWhiteSpace(manifest?.PrimaryModelFile) ? (list.FirstOrDefault() ?? "3DDFA_V2/TDDFA_ONNX.py") : manifest.PrimaryModelFile);
			if (list.Count == 0)
			{
				list.Add(text3);
			}
			return new ThreeDdfaOnnxModelInfo
			{
				ModelDirectory = text,
				ManifestPath = text2,
				PrimaryModelPath = BuildModelPath(text, text3),
				Backend = (string.IsNullOrWhiteSpace(manifest?.Backend) ? "3DDFA/ONNX face reconstruction lane" : manifest.Backend),
				BackendId = (manifest?.BackendId ?? ""),
				SourceRepository = (manifest?.SourceRepository ?? ""),
				Runtime = (manifest?.Runtime ?? ""),
				ModelFiles = list,
				AlternativeModelFileGroups = (manifest?.AlternativeModelFileGroups ?? Array.Empty<IReadOnlyList<string>>()),
				RuntimeFiles = (manifest?.RuntimeFiles ?? Array.Empty<string>()),
				ExpectedOutputs = (manifest?.ExpectedOutputs ?? Array.Empty<string>()),
				InferenceImplementationStatus = (manifest?.InferenceImplementationStatus ?? ""),
				ManifestStatus = (manifest?.Status ?? ""),
				Notes = (manifest?.Notes ?? "")
			};
		}
		catch (Exception ex)
		{
			return new ThreeDdfaOnnxModelInfo
			{
				ModelDirectory = text,
				ManifestPath = text2,
				PrimaryModelPath = primaryModelPath,
				ManifestStatus = "manifest unreadable: " + ex.Message,
				ModelFiles = ["3DDFA_V2/TDDFA_ONNX.py"]
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
}
