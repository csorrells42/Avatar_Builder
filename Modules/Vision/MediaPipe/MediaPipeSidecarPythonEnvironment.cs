using System.Diagnostics;
using System.IO;

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
        if (IsTruthy(Environment.GetEnvironmentVariable(DisableVariable)))
        {
            return NotReady("MediaPipe sidecar disabled by AVATAR_BUILDER_MEDIAPIPE_DISABLED.");
        }

        var scriptPath = FindScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return NotReady("MediaPipe sidecar script missing from runtime output.");
        }

        if (!modelInfo.ModelExists)
        {
            return NotReady(modelInfo.Status);
        }

        var pythonPath = FindPythonPath();
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return NotReady("Python not configured for MediaPipe sidecar. Set AVATAR_BUILDER_MEDIAPIPE_PYTHON or run tools\\SetupMediaPipeSidecar.ps1.");
        }

        var importStatus = CheckMediaPipeImport(pythonPath);
        if (!importStatus.Ready)
        {
            return new MediaPipeSidecarPythonEnvironment
            {
                PythonPath = pythonPath,
                ScriptPath = scriptPath,
                ModelPath = modelInfo.ModelPath,
                Status = importStatus.Status
            };
        }

        return new MediaPipeSidecarPythonEnvironment
        {
            PythonPath = pythonPath,
            ScriptPath = scriptPath,
            ModelPath = modelInfo.ModelPath,
            IsReady = true,
            Status = $"MediaPipe sidecar ready: {Path.GetFileName(pythonPath)}"
        };
    }

    private static MediaPipeSidecarPythonEnvironment NotReady(string status)
    {
        return new MediaPipeSidecarPythonEnvironment { Status = status };
    }

    private static string FindScriptPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, RelativeScriptPath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(Environment.CurrentDirectory, RelativeScriptPath.Replace('/', Path.DirectorySeparatorChar))
        };

        candidates.AddRange(EnumerateAncestors(AppContext.BaseDirectory)
            .Select(root => Path.Combine(root, RelativeScriptPath.Replace('/', Path.DirectorySeparatorChar))));
        candidates.AddRange(EnumerateAncestors(Environment.CurrentDirectory)
            .Select(root => Path.Combine(root, RelativeScriptPath.Replace('/', Path.DirectorySeparatorChar))));

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string FindPythonPath()
    {
        foreach (var variable in new[] { PythonOverrideVariable, GeneralPythonOverrideVariable })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }
        }

        var roots = EnumerateAncestors(Environment.CurrentDirectory)
            .Concat(EnumerateAncestors(AppContext.BaseDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var venvPython = Path.Combine(root, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPython))
            {
                return venvPython;
            }
        }

        return FindOnPath("python.exe");
    }

    private static string FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static (bool Ready, string Status) CheckMediaPipeImport(string pythonPath)
    {
        try
        {
            using var process = new Process
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
                return (false, "Python process did not start for MediaPipe import check.");
            }

            if (!process.WaitForExit(8000))
            {
                TryKill(process);
                return (false, "Python MediaPipe import check timed out.");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0 && output.Contains("mediapipe-ready", StringComparison.OrdinalIgnoreCase))
            {
                return (true, "MediaPipe import check passed.");
            }

            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            return (false, $"Python found, but MediaPipe sidecar imports failed: {detail}");
        }
        catch (Exception ex)
        {
            return (false, $"Python MediaPipe import check failed: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        var directory = new DirectoryInfo(start);
        if (File.Exists(start))
        {
            directory = Directory.GetParent(start) ?? directory;
        }

        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
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
