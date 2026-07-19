param(
    [string]$Python = "",
    [switch]$SkipPythonPackages
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$bundleRoot = Join-Path $repoRoot "dependencies\vision\3ddfa-onnx"
$targetRepo = Join-Path $bundleRoot "3DDFA_V2"
$venvRoot = Join-Path $repoRoot ".venv"

New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null

if (-not (Test-Path $targetRepo)) {
    git clone https://github.com/cleardusk/3DDFA_V2.git $targetRepo
} else {
    git -C $targetRepo pull --ff-only
}

if ([string]::IsNullOrWhiteSpace($Python)) {
    if (Test-Path (Join-Path $venvRoot "Scripts\python.exe")) {
        $Python = Join-Path $venvRoot "Scripts\python.exe"
    } else {
        $Python = "python"
    }
}

if (-not $SkipPythonPackages) {
    & $Python -m pip install --upgrade pip
    & $Python -m pip install numpy opencv-python pyyaml onnx onnxruntime onnxscript torch torchvision scipy scikit-image cython
}

$env:AVATAR_BUILDER_3DDFA_REPO = $targetRepo
$env:AVATAR_BUILDER_3DDFA_PYTHON = $Python
$weightCandidates = @(
    (Join-Path $targetRepo "weights\mb1_120x120.onnx"),
    (Join-Path $targetRepo "weights\mb1_120x120.pth")
)
$hasWeights = $false
foreach ($candidate in $weightCandidates) {
    if (Test-Path $candidate) {
        $hasWeights = $true
        break
    }
}

Write-Host "3DDFA_V2 repo: $targetRepo"
Write-Host "Python: $Python"
Write-Host "Set these before launching Avatar Builder if you are not using the repo-local .venv:"
Write-Host "  `$env:AVATAR_BUILDER_3DDFA_REPO = `"$targetRepo`""
Write-Host "  `$env:AVATAR_BUILDER_3DDFA_PYTHON = `"$Python`""
Write-Host ""
if ($hasWeights) {
    Write-Host "3DDFA mb1_120x120 weights found."
} else {
    Write-Host "3DDFA mb1_120x120 weights were not found yet."
    Write-Host "Follow the 3DDFA_V2 README for its official weights and place one of these under:"
    Write-Host "  $targetRepo\weights\mb1_120x120.onnx"
    Write-Host "  $targetRepo\weights\mb1_120x120.pth"
}
Write-Host ""
Write-Host "The Avatar Builder sidecar uses the app's MediaPipe/OpenCV face box first."
Write-Host "3DDFA FaceBoxes weights/helpers are optional fallback detection for frames without a supplied face box."
