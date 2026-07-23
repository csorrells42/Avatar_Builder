using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class MediaPipeSidecarPythonEnvironment
{
	private const string PythonOverrideVariable = "AVATAR_BUILDER_MEDIAPIPE_PYTHON";

	private const string GeneralPythonOverrideVariable = "AVATAR_BUILDER_PYTHON";

	private const string DisableVariable = "AVATAR_BUILDER_MEDIAPIPE_DISABLED";

	private const string RelativeScriptPath = "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py";

	public string PythonPath { get; private init; } = "";

	public string ScriptPath { get; private init; } = "";

	public string ModelPath { get; private init; } = "";

	public bool IsReady { get; private init; }

	public string Status { get; private init; } = "not checked";

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
		if (!tuple.Item1)
		{
			return new MediaPipeSidecarPythonEnvironment
			{
				PythonPath = text2,
				ScriptPath = text,
				ModelPath = modelInfo.ModelPath,
				Status = tuple.Item2
			};
		}
		return new MediaPipeSidecarPythonEnvironment
		{
			PythonPath = text2,
			ScriptPath = text,
			ModelPath = modelInfo.ModelPath,
			IsReady = true,
			Status = "MediaPipe sidecar ready: " + Path.GetFileName(text2)
		};
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
		List<string> list = new List<string>();
		list.Add(Path.Combine(AppContext.BaseDirectory, "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py".Replace('/', Path.DirectorySeparatorChar)));
		list.Add(Path.Combine(Environment.CurrentDirectory, "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py".Replace('/', Path.DirectorySeparatorChar)));
		list.AddRange(from root in EnumerateAncestors(AppContext.BaseDirectory)
			select Path.Combine(root, "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py".Replace('/', Path.DirectorySeparatorChar)));
		list.AddRange(from root in EnumerateAncestors(Environment.CurrentDirectory)
			select Path.Combine(root, "Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py".Replace('/', Path.DirectorySeparatorChar)));
		return list.FirstOrDefault(File.Exists) ?? "";
	}

	private static string FindPythonPath()
	{
		string[] array = new string[2] { "AVATAR_BUILDER_MEDIAPIPE_PYTHON", "AVATAR_BUILDER_PYTHON" };
		for (int i = 0; i < array.Length; i++)
		{
			string environmentVariable = Environment.GetEnvironmentVariable(array[i]);
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
		DirectoryInfo directory = new DirectoryInfo(start);
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
