param(
    [string]$PythonPath = "",
    [string]$VenvPath = ".\.venv"
)

$ErrorActionPreference = "Stop"

function Resolve-Python {
    param([string]$ConfiguredPath)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        $resolved = Resolve-Path -LiteralPath $ConfiguredPath -ErrorAction Stop
        return $resolved.Path
    }

    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        return $py.Source
    }

    throw "Python was not found. Install Python 3.10-3.12 or pass -PythonPath C:\Path\To\python.exe."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$venvFullPath = Join-Path $repoRoot $VenvPath
$python = Resolve-Python -ConfiguredPath $PythonPath
$requirements = Join-Path $repoRoot "Modules\Vision\MediaPipe\Sidecar\requirements.txt"

Write-Host "Using Python: $python"
Write-Host "Creating/updating venv: $venvFullPath"

if (-not (Test-Path -LiteralPath $venvFullPath)) {
    & $python -m venv $venvFullPath
}

$venvPython = Join-Path $venvFullPath "Scripts\python.exe"
if (-not (Test-Path -LiteralPath $venvPython)) {
    throw "Venv Python was not created: $venvPython"
}

& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install -r $requirements
& $venvPython -c "import mediapipe, cv2, onnxruntime as ort; assert 'DmlExecutionProvider' in ort.get_available_providers(); print('MediaPipe CPU and DirectML sidecar environment ready')"

Write-Host ""
Write-Host "To force Avatar Builder to use this environment, set:"
Write-Host "  `$env:AVATAR_BUILDER_MEDIAPIPE_PYTHON = `"$venvPython`""
