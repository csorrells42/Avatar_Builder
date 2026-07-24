using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed record MediaPipeDelegateProbeResult(bool IsAvailable, string Status);

internal sealed record MediaPipeDirectMlModelEnvironment(
	string DetectorModelPath,
	string LandmarkerModelPath,
	bool IsReady,
	string Status);

public sealed class MediaPipeSidecarPythonEnvironment
{
	private const string PythonOverrideVariable = "AVATAR_BUILDER_MEDIAPIPE_PYTHON";

	private const string GeneralPythonOverrideVariable = "AVATAR_BUILDER_PYTHON";

	private const string DisableVariable = "AVATAR_BUILDER_MEDIAPIPE_DISABLED";

	private const string RelativeScriptPath = "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py";

	private const string RelativeDirectMlScriptPath = "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_directml_sidecar.py";

	private const string RelativeDetectorOnnxPath = "dependencies/vision/dense-face-landmarks/onnx/face_detector.onnx";

	private const string RelativeLandmarkerOnnxPath = "dependencies/vision/dense-face-landmarks/onnx/face_landmarks_detector.onnx";

	public string PythonPath { get; private init; } = "";

	public string ScriptPath { get; private init; } = "";

	public string ModelPath { get; private init; } = "";

	public string DirectMlScriptPath { get; private init; } = "";

	public string DetectorOnnxPath { get; private init; } = "";

	public string LandmarkerOnnxPath { get; private init; } = "";

	public bool IsReady { get; private init; }

	public string Status { get; private init; } = "not checked";

	internal static MediaPipeDirectMlModelEnvironment DetectDirectMlModels()
	{
		string detectorModelPath = FindRuntimeFile(RelativeDetectorOnnxPath);
		string landmarkerModelPath = FindRuntimeFile(RelativeLandmarkerOnnxPath);
		if (string.IsNullOrWhiteSpace(detectorModelPath))
		{
			return new MediaPipeDirectMlModelEnvironment(
				"",
				landmarkerModelPath,
				false,
				"MediaPipe DirectML detector model is missing.");
		}
		if (string.IsNullOrWhiteSpace(landmarkerModelPath))
		{
			return new MediaPipeDirectMlModelEnvironment(
				detectorModelPath,
				"",
				false,
				"MediaPipe DirectML 478-point landmark model is missing.");
		}
		return new MediaPipeDirectMlModelEnvironment(
			detectorModelPath,
			landmarkerModelPath,
			true,
			"MediaPipe DirectML detector and 478-point landmark models are ready.");
	}

	public static MediaPipeSidecarPythonEnvironment Detect(DenseFaceLandmarkModelInfo modelInfo)
	{
		if (IsTruthy(Environment.GetEnvironmentVariable("AVATAR_BUILDER_MEDIAPIPE_DISABLED")))
		{
			return NotReady("MediaPipe sidecar disabled by AVATAR_BUILDER_MEDIAPIPE_DISABLED.");
		}
		string text = FindScriptPath();
		if (string.IsNullOrWhiteSpace(text))
		{
			return NotReady("MediaPipe sidecar script missing from runtime output.");
		}
		if (!modelInfo.ModelExists)
		{
			return NotReady(modelInfo.Status);
		}
		string text2 = FindPythonPath();
		if (string.IsNullOrWhiteSpace(text2))
		{
			return NotReady("Python not configured for MediaPipe sidecar. Set AVATAR_BUILDER_MEDIAPIPE_PYTHON or run tools\\SetupMediaPipeSidecar.ps1.");
		}
		(bool, string) tuple = CheckMediaPipeImport(text2);
		string directMlScriptPath = FindRuntimeFile(RelativeDirectMlScriptPath);
		string detectorOnnxPath = FindRuntimeFile(RelativeDetectorOnnxPath);
		string landmarkerOnnxPath = FindRuntimeFile(RelativeLandmarkerOnnxPath);
		if (!tuple.Item1)
		{
			return new MediaPipeSidecarPythonEnvironment
			{
				PythonPath = text2,
				ScriptPath = text,
				ModelPath = modelInfo.ModelPath,
				DirectMlScriptPath = directMlScriptPath,
				DetectorOnnxPath = detectorOnnxPath,
				LandmarkerOnnxPath = landmarkerOnnxPath,
				Status = tuple.Item2
			};
		}
		return new MediaPipeSidecarPythonEnvironment
		{
			PythonPath = text2,
			ScriptPath = text,
			ModelPath = modelInfo.ModelPath,
			DirectMlScriptPath = directMlScriptPath,
			DetectorOnnxPath = detectorOnnxPath,
			LandmarkerOnnxPath = landmarkerOnnxPath,
			IsReady = true,
			Status = "MediaPipe sidecar ready: " + Path.GetFileName(text2)
		};
	}

	public MediaPipeDelegateProbeResult ProbeDelegate(MediaPipeExecutionBackend backend)
	{
		if (!IsReady)
		{
			return new MediaPipeDelegateProbeResult(false, Status);
		}
		try
		{
			using Process process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = PythonPath,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			ConfigureStartInfo(process.StartInfo, backend, probe: true);
			if (!process.Start())
			{
				return new MediaPipeDelegateProbeResult(false, $"MediaPipe {backend.ToDisplayName()} delegate probe did not start.");
			}
			if (!process.WaitForExit(12000))
			{
				TryKill(process);
				return new MediaPipeDelegateProbeResult(false, $"MediaPipe {backend.ToDisplayName()} delegate probe timed out.");
			}
			string output = process.StandardOutput.ReadToEnd().Trim();
			string error = process.StandardError.ReadToEnd().Trim();
			if (process.ExitCode == 0 && output.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase))
			{
				return new MediaPipeDelegateProbeResult(true, backend == MediaPipeExecutionBackend.Gpu
					? "MediaPipe GPU (DirectML) detector and 478-point landmarker are ready."
					: "MediaPipe CPU sidecar is ready.");
			}
			string detail = ExtractProbeFailure(string.IsNullOrWhiteSpace(error) ? output : error);
			return new MediaPipeDelegateProbeResult(false, $"MediaPipe {backend.ToDisplayName()} delegate is unavailable: {detail}");
		}
		catch (Exception ex)
		{
			return new MediaPipeDelegateProbeResult(false, $"MediaPipe {backend.ToDisplayName()} delegate probe failed: {ex.Message}");
		}
	}

	public void ConfigureStartInfo(ProcessStartInfo startInfo, MediaPipeExecutionBackend backend, bool probe)
	{
		if (backend == MediaPipeExecutionBackend.Gpu)
		{
			if (string.IsNullOrWhiteSpace(DirectMlScriptPath) || !File.Exists(DirectMlScriptPath))
			{
				throw new FileNotFoundException("MediaPipe DirectML sidecar script is missing.", DirectMlScriptPath);
			}
			if (string.IsNullOrWhiteSpace(DetectorOnnxPath) || !File.Exists(DetectorOnnxPath))
			{
				throw new FileNotFoundException("MediaPipe DirectML detector model is missing.", DetectorOnnxPath);
			}
			if (string.IsNullOrWhiteSpace(LandmarkerOnnxPath) || !File.Exists(LandmarkerOnnxPath))
			{
				throw new FileNotFoundException("MediaPipe DirectML landmark model is missing.", LandmarkerOnnxPath);
			}
			startInfo.ArgumentList.Add(DirectMlScriptPath);
			startInfo.ArgumentList.Add("--detector-model");
			startInfo.ArgumentList.Add(DetectorOnnxPath);
			startInfo.ArgumentList.Add("--landmarker-model");
			startInfo.ArgumentList.Add(LandmarkerOnnxPath);
		}
		else
		{
			startInfo.ArgumentList.Add(ScriptPath);
			startInfo.ArgumentList.Add("--model");
			startInfo.ArgumentList.Add(ModelPath);
			startInfo.ArgumentList.Add("--delegate");
			startInfo.ArgumentList.Add("cpu");
		}
		if (probe)
		{
			startInfo.ArgumentList.Add("--probe");
		}
	}

	private static string ExtractProbeFailure(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
		{
			return "the runtime did not provide a reason";
		}
		string[] lines = detail.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string message = lines.FirstOrDefault((string line) => line.Contains("GPU processing is disabled", StringComparison.OrdinalIgnoreCase))
			?? lines.FirstOrDefault((string line) => line.Contains("delegate", StringComparison.OrdinalIgnoreCase) && line.Contains("failed", StringComparison.OrdinalIgnoreCase))
			?? lines.LastOrDefault()
			?? detail.Trim();
		const int maximumStatusLength = 320;
		return message.Length <= maximumStatusLength ? message : message[..maximumStatusLength] + "...";
	}

	private static MediaPipeSidecarPythonEnvironment NotReady(string status)
	{
		return new MediaPipeSidecarPythonEnvironment
		{
			Status = status
		};
	}

	private static string FindScriptPath()
	{
		return FindRuntimeFile(RelativeScriptPath);
	}

	private static string FindRuntimeFile(string relativePath)
	{
		string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
		List<string> list = new List<string>
		{
			Path.Combine(AppContext.BaseDirectory, platformPath),
			Path.Combine(Environment.CurrentDirectory, platformPath)
		};
		list.AddRange(from root in EnumerateAncestors(AppContext.BaseDirectory)
			select Path.Combine(root, platformPath));
		list.AddRange(from root in EnumerateAncestors(Environment.CurrentDirectory)
			select Path.Combine(root, platformPath));
		return list.FirstOrDefault(File.Exists) ?? "";
	}

	private static string FindPythonPath()
	{
		string[] array = new string[2] { "AVATAR_BUILDER_MEDIAPIPE_PYTHON", "AVATAR_BUILDER_PYTHON" };
		for (int i = 0; i < array.Length; i++)
		{
			string? environmentVariable = Environment.GetEnvironmentVariable(array[i]);
			if (!string.IsNullOrWhiteSpace(environmentVariable) && File.Exists(environmentVariable))
			{
				return environmentVariable;
			}
		}
		foreach (string item in EnumerateAncestors(Environment.CurrentDirectory).Concat(EnumerateAncestors(AppContext.BaseDirectory)).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			string text = Path.Combine(item, ".venv", "Scripts", "python.exe");
			if (File.Exists(text))
			{
				return text;
			}
		}
		return FindOnPath("python.exe");
	}

	private static string FindOnPath(string executableName)
	{
		string[] array = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text = Path.Combine(array[i], executableName);
			if (File.Exists(text))
			{
				return text;
			}
		}
		return "";
	}

	private static (bool Ready, string Status) CheckMediaPipeImport(string pythonPath)
	{
		try
		{
			using Process process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = pythonPath,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			process.StartInfo.ArgumentList.Add("-c");
			process.StartInfo.ArgumentList.Add("import mediapipe; import cv2; print('mediapipe-ready')");
			if (!process.Start())
			{
				return (Ready: false, Status: "Python process did not start for MediaPipe import check.");
			}
			if (!process.WaitForExit(8000))
			{
				TryKill(process);
				return (Ready: false, Status: "Python MediaPipe import check timed out.");
			}
			string text = process.StandardOutput.ReadToEnd().Trim();
			string text2 = process.StandardError.ReadToEnd().Trim();
			if (process.ExitCode == 0 && text.Contains("mediapipe-ready", StringComparison.OrdinalIgnoreCase))
			{
				return (Ready: true, Status: "MediaPipe import check passed.");
			}
			string text3 = (string.IsNullOrWhiteSpace(text2) ? text : text2);
			return (Ready: false, Status: "Python found, but MediaPipe sidecar imports failed: " + text3);
		}
		catch (Exception ex)
		{
			return (Ready: false, Status: "Python MediaPipe import check failed: " + ex.Message);
		}
	}

	private static IEnumerable<string> EnumerateAncestors(string start)
	{
		DirectoryInfo? directory = new DirectoryInfo(start);
		if (File.Exists(start))
		{
			directory = Directory.GetParent(start) ?? directory;
		}
		while (directory != null)
		{
			yield return directory.FullName;
			directory = directory.Parent;
		}
	}

	private static bool IsTruthy(string? value)
	{
		if (!string.Equals(value, "1", StringComparison.Ordinal) && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static void TryKill(Process process)
	{
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
	}
}
