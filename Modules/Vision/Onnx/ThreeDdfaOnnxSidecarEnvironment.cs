using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxSidecarEnvironment
{
	private const string PythonOverrideVariable = "AVATAR_BUILDER_3DDFA_PYTHON";

	private const string GeneralPythonOverrideVariable = "AVATAR_BUILDER_PYTHON";

	private const string RepoOverrideVariable = "AVATAR_BUILDER_3DDFA_REPO";

	private const string ConfigOverrideVariable = "AVATAR_BUILDER_3DDFA_CONFIG";

	private const string DisableVariable = "AVATAR_BUILDER_3DDFA_DISABLED";

	private const string RelativeScriptPath = "Modules/Vision/Onnx/Sidecar/three_ddfa_onnx_sidecar.py";

	private const string RelativeBundledRepoPath = "dependencies/vision/3ddfa-onnx/3DDFA_V2";

	private const string DefaultConfigRelativePath = "configs/mb1_120x120.yml";

	public string PythonPath { get; private init; } = "";

	public string ScriptPath { get; private init; } = "";

	public string RepositoryPath { get; private init; } = "";

	public string ConfigPath { get; private init; } = "";

	public bool IsReady { get; private init; }

	public string Status { get; private init; } = "not checked";

	public static ThreeDdfaOnnxSidecarEnvironment Detect(ThreeDdfaOnnxModelInfo modelInfo)
	{
		if (IsTruthy(Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_DISABLED")))
		{
			return NotReady("3DDFA/ONNX sidecar disabled by AVATAR_BUILDER_3DDFA_DISABLED.");
		}
		string text = FindScriptPath();
		if (string.IsNullOrWhiteSpace(text))
		{
			return NotReady("3DDFA/ONNX sidecar script missing from runtime output.");
		}
		string text2 = FindRepositoryPath(modelInfo);
		if (string.IsNullOrWhiteSpace(text2))
		{
			return NotReady("3DDFA_V2 repository missing. Run tools\\SetupThreeDdfaOnnxSidecar.ps1 or set AVATAR_BUILDER_3DDFA_REPO.");
		}
		string text3 = FindConfigPath(text2);
		if (string.IsNullOrWhiteSpace(text3))
		{
			return NotReady("3DDFA config missing. Expected configs\\mb1_120x120.yml or AVATAR_BUILDER_3DDFA_CONFIG.");
		}
		string text4 = FindPythonPath();
		if (string.IsNullOrWhiteSpace(text4))
		{
			return NotReady("Python not configured for 3DDFA/ONNX sidecar. Set AVATAR_BUILDER_3DDFA_PYTHON or AVATAR_BUILDER_PYTHON.");
		}
		(bool, string) tuple = CheckImports(text4, text2);
		if (!tuple.Item1)
		{
			return new ThreeDdfaOnnxSidecarEnvironment
			{
				PythonPath = text4,
				ScriptPath = text,
				RepositoryPath = text2,
				ConfigPath = text3,
				Status = tuple.Item2
			};
		}
		return new ThreeDdfaOnnxSidecarEnvironment
		{
			PythonPath = text4,
			ScriptPath = text,
			RepositoryPath = text2,
			ConfigPath = text3,
			IsReady = true,
			Status = "3DDFA/ONNX sidecar ready: " + Path.GetFileName(text4)
		};
	}

	private static ThreeDdfaOnnxSidecarEnvironment NotReady(string status)
	{
		return new ThreeDdfaOnnxSidecarEnvironment
		{
			Status = status
		};
	}

	private static string FindRepositoryPath(ThreeDdfaOnnxModelInfo modelInfo)
	{
		string environmentVariable = Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_REPO");
		if (!string.IsNullOrWhiteSpace(environmentVariable) && File.Exists(Path.Combine(environmentVariable, "TDDFA_ONNX.py")))
		{
			return environmentVariable;
		}
		string text = Path.Combine(modelInfo.ModelDirectory, "3DDFA_V2");
		if (File.Exists(Path.Combine(text, "TDDFA_ONNX.py")))
		{
			return text;
		}
		string relative = "dependencies/vision/3ddfa-onnx/3DDFA_V2".Replace('/', Path.DirectorySeparatorChar);
		return (from root in EnumerateAncestors(Environment.CurrentDirectory).Concat(EnumerateAncestors(AppContext.BaseDirectory))
			select Path.Combine(root, relative)).FirstOrDefault((string path) => File.Exists(Path.Combine(path, "TDDFA_ONNX.py"))) ?? "";
	}

	private static string FindConfigPath(string repositoryPath)
	{
		string environmentVariable = Environment.GetEnvironmentVariable("AVATAR_BUILDER_3DDFA_CONFIG");
		if (!string.IsNullOrWhiteSpace(environmentVariable) && File.Exists(environmentVariable))
		{
			return environmentVariable;
		}
		string text = Path.Combine(repositoryPath, "configs/mb1_120x120.yml".Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(text))
		{
			return "";
		}
		return text;
	}

	private static string FindScriptPath()
	{
		string relative = "Modules/Vision/Onnx/Sidecar/three_ddfa_onnx_sidecar.py".Replace('/', Path.DirectorySeparatorChar);
		List<string> list = new List<string>();
		list.Add(Path.Combine(AppContext.BaseDirectory, relative));
		list.Add(Path.Combine(Environment.CurrentDirectory, relative));
		list.AddRange(from root in EnumerateAncestors(AppContext.BaseDirectory)
			select Path.Combine(root, relative));
		list.AddRange(from root in EnumerateAncestors(Environment.CurrentDirectory)
			select Path.Combine(root, relative));
		return list.FirstOrDefault(File.Exists) ?? "";
	}

	private static string FindPythonPath()
	{
		string[] array = new string[2] { "AVATAR_BUILDER_3DDFA_PYTHON", "AVATAR_BUILDER_PYTHON" };
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

	private static (bool Ready, string Status) CheckImports(string pythonPath, string repositoryPath)
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
			process.StartInfo.ArgumentList.Add("import sys; sys.path.insert(0, r'" + repositoryPath.Replace("'", "\\'") + "'); import cv2, yaml, onnxruntime; from TDDFA_ONNX import TDDFA_ONNX; print('3ddfa-ready')");
			if (!process.Start())
			{
				return (Ready: false, Status: "Python process did not start for 3DDFA import check.");
			}
			if (!process.WaitForExit(12000))
			{
				TryKill(process);
				return (Ready: false, Status: "Python 3DDFA import check timed out.");
			}
			string text = process.StandardOutput.ReadToEnd().Trim();
			string text2 = process.StandardError.ReadToEnd().Trim();
			if (process.ExitCode == 0 && text.Contains("3ddfa-ready", StringComparison.OrdinalIgnoreCase))
			{
				return (Ready: true, Status: "3DDFA import check passed.");
			}
			string text3 = (string.IsNullOrWhiteSpace(text2) ? text : text2);
			return (Ready: false, Status: "Python found, but 3DDFA/ONNX imports failed: " + text3);
		}
		catch (Exception ex)
		{
			return (Ready: false, Status: "Python 3DDFA import check failed: " + ex.Message);
		}
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
