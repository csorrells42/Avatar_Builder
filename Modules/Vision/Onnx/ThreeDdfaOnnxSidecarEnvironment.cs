using System.Diagnostics;
using System.IO;

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
        if (IsTruthy(Environment.GetEnvironmentVariable(DisableVariable)))
        {
            return NotReady("3DDFA/ONNX sidecar disabled by AVATAR_BUILDER_3DDFA_DISABLED.");
        }

        var scriptPath = FindScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return NotReady("3DDFA/ONNX sidecar script missing from runtime output.");
        }

        var repositoryPath = FindRepositoryPath(modelInfo);
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return NotReady("3DDFA_V2 repository missing. Run tools\\SetupThreeDdfaOnnxSidecar.ps1 or set AVATAR_BUILDER_3DDFA_REPO.");
        }

        var configPath = FindConfigPath(repositoryPath);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return NotReady("3DDFA config missing. Expected configs\\mb1_120x120.yml or AVATAR_BUILDER_3DDFA_CONFIG.");
        }

        var pythonPath = FindPythonPath();
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return NotReady("Python not configured for 3DDFA/ONNX sidecar. Set AVATAR_BUILDER_3DDFA_PYTHON or AVATAR_BUILDER_PYTHON.");
        }

        var importStatus = CheckImports(pythonPath, repositoryPath);
        if (!importStatus.Ready)
        {
            return new ThreeDdfaOnnxSidecarEnvironment
            {
                PythonPath = pythonPath,
                ScriptPath = scriptPath,
                RepositoryPath = repositoryPath,
                ConfigPath = configPath,
                Status = importStatus.Status
            };
        }

        return new ThreeDdfaOnnxSidecarEnvironment
        {
            PythonPath = pythonPath,
            ScriptPath = scriptPath,
            RepositoryPath = repositoryPath,
            ConfigPath = configPath,
            IsReady = true,
            Status = $"3DDFA/ONNX sidecar ready: {Path.GetFileName(pythonPath)}"
        };
    }

    private static ThreeDdfaOnnxSidecarEnvironment NotReady(string status)
    {
        return new ThreeDdfaOnnxSidecarEnvironment { Status = status };
    }

    private static string FindRepositoryPath(ThreeDdfaOnnxModelInfo modelInfo)
    {
        var configured = Environment.GetEnvironmentVariable(RepoOverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "TDDFA_ONNX.py")))
        {
            return configured;
        }

        var bundled = Path.Combine(modelInfo.ModelDirectory, "3DDFA_V2");
        if (File.Exists(Path.Combine(bundled, "TDDFA_ONNX.py")))
        {
            return bundled;
        }

        var relative = RelativeBundledRepoPath.Replace('/', Path.DirectorySeparatorChar);
        var candidates = EnumerateAncestors(Environment.CurrentDirectory)
            .Concat(EnumerateAncestors(AppContext.BaseDirectory))
            .Select(root => Path.Combine(root, relative));
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "TDDFA_ONNX.py"))) ?? "";
    }

    private static string FindConfigPath(string repositoryPath)
    {
        var configured = Environment.GetEnvironmentVariable(ConfigOverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var path = Path.Combine(repositoryPath, DefaultConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : "";
    }

    private static string FindScriptPath()
    {
        var relative = RelativeScriptPath.Replace('/', Path.DirectorySeparatorChar);
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, relative),
            Path.Combine(Environment.CurrentDirectory, relative)
        };

        candidates.AddRange(EnumerateAncestors(AppContext.BaseDirectory).Select(root => Path.Combine(root, relative)));
        candidates.AddRange(EnumerateAncestors(Environment.CurrentDirectory).Select(root => Path.Combine(root, relative)));
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

    private static (bool Ready, string Status) CheckImports(string pythonPath, string repositoryPath)
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
            process.StartInfo.ArgumentList.Add(
                "import sys; sys.path.insert(0, r'" + repositoryPath.Replace("'", "\\'") + "'); import cv2, yaml, onnxruntime; from TDDFA_ONNX import TDDFA_ONNX; print('3ddfa-ready')");

            if (!process.Start())
            {
                return (false, "Python process did not start for 3DDFA import check.");
            }

            if (!process.WaitForExit(12000))
            {
                TryKill(process);
                return (false, "Python 3DDFA import check timed out.");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0 && output.Contains("3ddfa-ready", StringComparison.OrdinalIgnoreCase))
            {
                return (true, "3DDFA import check passed.");
            }

            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            return (false, $"Python found, but 3DDFA/ONNX imports failed: {detail}");
        }
        catch (Exception ex)
        {
            return (false, $"Python 3DDFA import check failed: {ex.Message}");
        }
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
