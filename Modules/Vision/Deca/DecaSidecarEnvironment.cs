using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed class DecaSidecarEnvironment
{
	private const string RelativeScriptPath = "Modules/Vision/Deca/Sidecar/deca_sidecar.py";

	private const string RelativeRepositoryPath = "dependencies/vision/deca/DECA";

	public string PythonPath { get; private init; } = "";

	public string ScriptPath { get; private init; } = "";

	public string RepositoryPath { get; private init; } = "";

	public string ModelPath { get; private init; } = "";

	public bool IsReady { get; private set; }

	public string Status { get; private set; } = "not checked";

	public static DecaSidecarEnvironment Detect()
	{
		if (IsTruthy(Environment.GetEnvironmentVariable("AVATAR_BUILDER_DECA_DISABLED")))
		{
			return NotReady("DECA disabled by AVATAR_BUILDER_DECA_DISABLED.");
		}
		string text = FindFile("Modules/Vision/Deca/Sidecar/deca_sidecar.py");
		if (string.IsNullOrWhiteSpace(text))
		{
			return NotReady("DECA sidecar script is missing from the runtime output.");
		}
		string text2 = Environment.GetEnvironmentVariable("AVATAR_BUILDER_DECA_REPO") ?? "";
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = FindDirectory("dependencies/vision/deca/DECA");
		}
		if (string.IsNullOrWhiteSpace(text2) || !File.Exists(Path.Combine(text2, "decalib", "deca.py")))
		{
			return NotReady("DECA selected but its official repository is not staged. Set AVATAR_BUILDER_DECA_REPO or place it under dependencies\\vision\\deca\\DECA.");
		}
		string text3 = Environment.GetEnvironmentVariable("AVATAR_BUILDER_DECA_MODEL") ?? "";
		if (string.IsNullOrWhiteSpace(text3))
		{
			text3 = Path.Combine(text2, "data", "deca_model.tar");
		}
		if (!File.Exists(text3))
		{
			return NotReady("DECA selected but deca_model.tar is missing. Set AVATAR_BUILDER_DECA_MODEL after obtaining a model under its license.");
		}
		if (!File.Exists(Path.Combine(text2, "data", "generic_model.pkl")))
		{
			return NotReady("DECA selected but the licensed FLAME generic_model.pkl asset is missing from its data folder.");
		}
		if (!File.Exists(Path.Combine(text2, "data", "landmark_embedding.npy")))
		{
			return NotReady("DECA selected but landmark_embedding.npy is missing from its data folder.");
		}
		if (string.IsNullOrWhiteSpace(FindFile("Modules/Vision/Deca/Resources/mediapipe_landmark_embedding.npz")))
		{
			return NotReady("DECA selected but the MediaPipe-to-FLAME surface embedding is missing from the runtime output.");
		}
		string text4 = FindPythonPath();
		if (string.IsNullOrWhiteSpace(text4))
		{
			return NotReady("Python is not configured for DECA. Set AVATAR_BUILDER_DECA_PYTHON or AVATAR_BUILDER_PYTHON.");
		}
		(bool, string) tuple = CheckImports(text4, text2);
		DecaSidecarEnvironment obj = new DecaSidecarEnvironment
		{
			PythonPath = text4,
			ScriptPath = text,
			RepositoryPath = text2,
			ModelPath = text3
		};
		(obj.IsReady, obj.Status) = tuple;
		return obj;
	}

	private static DecaSidecarEnvironment NotReady(string status)
	{
		return new DecaSidecarEnvironment
		{
			Status = status
		};
	}

	private static string FindPythonPath()
	{
		string[] array = new string[2] { "AVATAR_BUILDER_DECA_PYTHON", "AVATAR_BUILDER_PYTHON" };
		for (int i = 0; i < array.Length; i++)
		{
			string environmentVariable = Environment.GetEnvironmentVariable(array[i]);
			if (!string.IsNullOrWhiteSpace(environmentVariable) && File.Exists(environmentVariable))
			{
				return environmentVariable;
			}
		}
		foreach (string item in EnumerateRoots())
		{
			array = new string[2] { ".venv-deca", ".venv" };
			foreach (string path in array)
			{
				string text = Path.Combine(item, path, "Scripts", "python.exe");
				if (File.Exists(text))
				{
					return text;
				}
			}
		}
		return "";
	}

	private static string FindFile(string relativePath)
	{
		string relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return (from root in EnumerateRoots()
			select Path.Combine(root, relative)).FirstOrDefault(File.Exists) ?? "";
	}

	private static string FindDirectory(string relativePath)
	{
		string relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return (from root in EnumerateRoots()
			select Path.Combine(root, relative)).FirstOrDefault(Directory.Exists) ?? "";
	}

	private static IEnumerable<string> EnumerateRoots()
	{
		return EnumerateAncestors(Environment.CurrentDirectory).Concat(EnumerateAncestors(AppContext.BaseDirectory)).Distinct<string>(StringComparer.OrdinalIgnoreCase);
	}

	private static IEnumerable<string> EnumerateAncestors(string start)
	{
		for (DirectoryInfo directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
		{
			yield return directory.FullName;
		}
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
			process.StartInfo.ArgumentList.Add("import sys; sys.path.insert(0, r'" + repositoryPath.Replace("'", "\\'") + "'); import cv2, numpy, scipy, torch, torchvision, yacs; from decalib.models.encoders import ResnetEncoder; from decalib.models.FLAME import FLAME; print('deca-ready')");
			if (!process.Start() || !process.WaitForExit(20000))
			{
				TryKill(process);
				return (Ready: false, Status: "DECA Python import check timed out.");
			}
			string text = process.StandardOutput.ReadToEnd();
			string text2 = process.StandardError.ReadToEnd().Trim();
			return (process.ExitCode == 0 && text.Contains("deca-ready", StringComparison.OrdinalIgnoreCase)) ? (Ready: true, Status: "DECA/FLAME sidecar ready.") : (Ready: false, Status: "DECA Python imports failed: " + (string.IsNullOrWhiteSpace(text2) ? text.Trim() : text2));
		}
		catch (Exception ex)
		{
			return (Ready: false, Status: "DECA Python import check failed: " + ex.Message);
		}
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

	private static bool IsTruthy(string? value)
	{
		if (!string.Equals(value, "1", StringComparison.Ordinal) && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
