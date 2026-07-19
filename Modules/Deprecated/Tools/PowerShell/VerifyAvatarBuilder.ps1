param(
    [string]$SampleMediaPath = "",
    [string]$SampleImagePath = "",
    [string]$SampleVideoPath = "",
    [string]$EvaluationOutputFolder = "",
    [double]$SampleFramesPerSecond = 2,
    [string]$EyeInset = "auto",
    [string]$BuildOutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$denseFaceLandmarkManifestPath = Join-Path $repoRoot "dependencies\vision\dense-face-landmarks\face_landmarker_manifest.json"
$denseFaceLandmarkModelPath = Join-Path $repoRoot "dependencies\vision\dense-face-landmarks\face_landmarker.task"
$denseFaceLandmarkTrackerSourcePath = Join-Path $repoRoot "Modules\Vision\MediaPipe\DenseFaceMeshLandmarkTracker.cs"
$mediaPipeSidecarTrackerSourcePath = Join-Path $repoRoot "Modules\Vision\MediaPipe\MediaPipeFaceLandmarkerSidecarTracker.cs"
$mediaPipeSidecarScriptSourcePath = Join-Path $repoRoot "Modules\Vision\MediaPipe\Sidecar\mediapipe_face_landmarker_sidecar.py"
$mediaPipeSidecarRequirementsSourcePath = Join-Path $repoRoot "Modules\Vision\MediaPipe\Sidecar\requirements.txt"
$targetFramework = "net10.0-windows"
$runStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ")
if ([string]::IsNullOrWhiteSpace($BuildOutputRoot)) {
    $resolvedBuildOutputRoot = Join-Path ([IO.Path]::GetTempPath()) "AvatarBuilderVerify\$runStamp"
}
elseif ([IO.Path]::IsPathRooted($BuildOutputRoot)) {
    $resolvedBuildOutputRoot = $BuildOutputRoot
}
else {
    $resolvedBuildOutputRoot = Join-Path $repoRoot $BuildOutputRoot
}

$appBaseOutputPath = Join-Path $resolvedBuildOutputRoot "app\bin\"
$smokeBaseOutputPath = Join-Path $resolvedBuildOutputRoot "smoke\bin\"
$evalBaseOutputPath = Join-Path $resolvedBuildOutputRoot "eval\bin\"
$buildOutputRoot = Join-Path $appBaseOutputPath "Debug\$targetFramework"
$smokeAssemblyPath = Join-Path $smokeBaseOutputPath "Debug\$targetFramework\AvatarBuilderVisionSmoke.dll"
$evalAssemblyPath = Join-Path $evalBaseOutputPath "Debug\$targetFramework\AvatarBuilderVisionEval.dll"

New-Item -ItemType Directory -Force -Path $resolvedBuildOutputRoot | Out-Null
Write-Host "Verifier build output root: $resolvedBuildOutputRoot"

function Get-MediaPipePythonPath {
    foreach ($variable in @("AVATAR_BUILDER_MEDIAPIPE_PYTHON", "AVATAR_BUILDER_PYTHON")) {
        $configured = [Environment]::GetEnvironmentVariable($variable)
        if (-not [string]::IsNullOrWhiteSpace($configured) -and (Test-Path -LiteralPath $configured)) {
            return $configured
        }
    }

    $venvPython = Join-Path $repoRoot ".venv\Scripts\python.exe"
    if (Test-Path -LiteralPath $venvPython) {
        return $venvPython
    }

    $pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($null -ne $pythonCommand -and -not [string]::IsNullOrWhiteSpace($pythonCommand.Source)) {
        return $pythonCommand.Source
    }

    return ""
}

function Test-MediaPipePythonImports {
    param([string]$PythonPath)

    if ([string]::IsNullOrWhiteSpace($PythonPath) -or -not (Test-Path -LiteralPath $PythonPath)) {
        return $false
    }

    $output = & $PythonPath -c "import mediapipe; import cv2; print('mediapipe-ready')" 2>&1
    return $LASTEXITCODE -eq 0 -and (($output -join "`n") -match "mediapipe-ready")
}

function Test-MediaPipeSidecarRuntimeReady {
    if (-not (Test-Path -LiteralPath $denseFaceLandmarkModelPath) -or
        -not (Test-Path -LiteralPath $mediaPipeSidecarTrackerSourcePath) -or
        -not (Test-Path -LiteralPath $mediaPipeSidecarScriptSourcePath)) {
        return $false
    }

    $pythonPath = Get-MediaPipePythonPath
    return Test-MediaPipePythonImports -PythonPath $pythonPath
}

function Test-DenseFullFaceRuntimeReady {
    if (-not (Test-Path -LiteralPath $denseFaceLandmarkManifestPath) -or
        -not (Test-Path -LiteralPath $denseFaceLandmarkModelPath)) {
        return $false
    }

    $manifest = Get-Content -LiteralPath $denseFaceLandmarkManifestPath -Raw | ConvertFrom-Json
    if ($manifest.inferenceImplementationStatus -ne "ready") {
        return $false
    }

    if (-not (Test-Path -LiteralPath $denseFaceLandmarkTrackerSourcePath)) {
        return $false
    }

    $trackerSource = Get-Content -LiteralPath $denseFaceLandmarkTrackerSourcePath -Raw
    if ($trackerSource -notmatch "InferenceImplementationCompiled\s*=\s*true") {
        return $false
    }

    if ($null -eq $manifest.runtimeFiles) {
        return $true
    }

    foreach ($runtimeFile in $manifest.runtimeFiles) {
        if ([string]::IsNullOrWhiteSpace($runtimeFile)) {
            continue
        }

        $runtimePath = Join-Path (Split-Path -Parent $denseFaceLandmarkManifestPath) $runtimeFile
        if (-not (Test-Path -LiteralPath $runtimePath)) {
            return $false
        }
    }

    return $true
}

function Test-IsImagePath {
    param([string]$Path)

    $extension = [IO.Path]::GetExtension($Path)
    return $extension -match '^\.(png|jpg|jpeg|bmp|tif|tiff)$'
}

function Get-ResolvedSamplePath {
    if (-not [string]::IsNullOrWhiteSpace($SampleMediaPath)) {
        return $SampleMediaPath
    }

    if (-not [string]::IsNullOrWhiteSpace($SampleImagePath)) {
        return $SampleImagePath
    }

    return $SampleVideoPath
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Body
    )

    Write-Host ""
    Write-Host "== $Name =="
    & $Body
}

function Invoke-Checked {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Command
    )

    & $Command[0] @($Command | Select-Object -Skip 1)
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $($Command -join ' ')"
    }
}

Invoke-Step "Build Avatar Builder" {
    Invoke-Checked dotnet build .\AvatarBuilder.csproj --no-restore /p:UseSharedCompilation=false /p:UseAppHost=false "/p:BaseOutputPath=$appBaseOutputPath"
}

Invoke-Step "Build synthetic aperture and cue smoke checks" {
    Invoke-Checked dotnet build .\tools\AvatarBuilderVisionSmoke\AvatarBuilderVisionSmoke.csproj --no-restore /p:UseSharedCompilation=false /p:UseAppHost=false "/p:BaseOutputPath=$smokeBaseOutputPath"
}

Invoke-Step "Run synthetic aperture and cue smoke checks" {
    Invoke-Checked dotnet $smokeAssemblyPath
}

Invoke-Step "Build offline vision evaluator" {
    Invoke-Checked dotnet build .\tools\AvatarBuilderVisionEval\AvatarBuilderVisionEval.csproj --no-restore /p:UseSharedCompilation=false /p:UseAppHost=false "/p:BaseOutputPath=$evalBaseOutputPath"
}

Invoke-Step "Check dense landmark model bundle status" {
    $manifestPath = $denseFaceLandmarkManifestPath
    $modelPath = $denseFaceLandmarkModelPath

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Dense landmark manifest is missing: $manifestPath"
    }

    Write-Host "Dense manifest present: $manifestPath"
    if (Test-Path -LiteralPath $modelPath) {
        Write-Host "Dense model present: $modelPath"
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($manifest.sha256)) {
            $modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash
            if ($modelHash -ne $manifest.sha256) {
                throw "Dense Face Landmarker model SHA256 mismatch. Expected $($manifest.sha256), got $modelHash"
            }

            Write-Host "Dense Face Landmarker model SHA256 verified: $modelHash"
        }

        if (Test-DenseFullFaceRuntimeReady) {
            Write-Host "Dense manifest/native inference runtime ready."
        }
        else {
            Write-Host "Dense manifest/native inference gate not ready; MediaPipe sidecar runtime is checked separately."
        }
    }
    else {
        Write-Host "Dense model not bundled yet; OpenCV aperture fallback remains active."
    }
}

Invoke-Step "Check MediaPipe sidecar files and local Python readiness" {
    foreach ($sourcePath in @($mediaPipeSidecarTrackerSourcePath, $mediaPipeSidecarScriptSourcePath, $mediaPipeSidecarRequirementsSourcePath)) {
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "MediaPipe sidecar source artifact is missing: $sourcePath"
        }

        Write-Host "MediaPipe sidecar source artifact present: $sourcePath"
    }

    $outputScript = Join-Path $buildOutputRoot "Modules\Vision\MediaPipe\Sidecar\mediapipe_face_landmarker_sidecar.py"
    $outputRequirements = Join-Path $buildOutputRoot "Modules\Vision\MediaPipe\Sidecar\requirements.txt"
    foreach ($outputPath in @($outputScript, $outputRequirements)) {
        if (-not (Test-Path -LiteralPath $outputPath)) {
            throw "MediaPipe sidecar artifact was not copied to build output: $outputPath"
        }

        Write-Host "MediaPipe sidecar output artifact present: $outputPath"
    }

    $pythonPath = Get-MediaPipePythonPath
    if ([string]::IsNullOrWhiteSpace($pythonPath)) {
        Write-Host "MediaPipe Python not configured; run tools\SetupMediaPipeSidecar.ps1 with a Python path to enable dense tracking."
        return
    }

    Write-Host "MediaPipe Python candidate: $pythonPath"
    if (Test-MediaPipePythonImports -PythonPath $pythonPath) {
        Write-Host "MediaPipe Python imports verified; dense sidecar runtime can be used."
    }
    else {
        Write-Host "MediaPipe Python candidate cannot import mediapipe/cv2 yet; OpenCV fallback remains active."
    }
}

Invoke-Step "Check OpenCV LBF facemark model bundle status" {
    $manifestPath = Join-Path $repoRoot "dependencies\vision\opencv\facemark\lbfmodel_manifest.json"
    $modelPath = Join-Path $repoRoot "dependencies\vision\opencv\facemark\lbfmodel.yaml"

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "OpenCV LBF facemark manifest is missing: $manifestPath"
    }

    Write-Host "OpenCV LBF facemark manifest present: $manifestPath"
    if (-not (Test-Path -LiteralPath $modelPath)) {
        throw "OpenCV LBF facemark model is missing: $modelPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash
    if (-not [string]::IsNullOrWhiteSpace($manifest.sha256) -and $modelHash -ne $manifest.sha256) {
        throw "OpenCV LBF facemark model SHA256 mismatch. Expected $($manifest.sha256), got $modelHash"
    }

    Write-Host "OpenCV LBF facemark model present: $modelPath"
    Write-Host "OpenCV LBF facemark model SHA256 verified: $modelHash"
}

Invoke-Step "Check OpenCV YuNet face detector model bundle status" {
    $manifestPath = Join-Path $repoRoot "dependencies\vision\opencv\yunet\yunet_manifest.json"
    $modelPath = Join-Path $repoRoot "dependencies\vision\opencv\yunet\face_detection_yunet_2023mar.onnx"

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "OpenCV YuNet manifest is missing: $manifestPath"
    }

    Write-Host "OpenCV YuNet manifest present: $manifestPath"
    if (Test-Path -LiteralPath $modelPath) {
        Write-Host "OpenCV YuNet model present: $modelPath"
    }
    else {
        Write-Host "OpenCV YuNet model not bundled yet; Haar face localization remains active."
    }
}

Invoke-Step "Run synthetic bottom-right eye inset video evaluation" {
    $syntheticRoot = Join-Path $repoRoot "output\vision-synthetic"
    $syntheticVideo = Join-Path $syntheticRoot "sleepy_eye_inset.avi"
    $syntheticEval = Join-Path $syntheticRoot "eval"
    New-Item -ItemType Directory -Force -Path $syntheticRoot | Out-Null
    if (Test-Path -LiteralPath $syntheticEval) {
        Remove-Item -LiteralPath $syntheticEval -Recurse -Force
    }

    Invoke-Checked dotnet $smokeAssemblyPath --write-synthetic-video $syntheticVideo
    Invoke-Checked dotnet $evalAssemblyPath $syntheticVideo $syntheticEval 6 --eye-inset auto --write-overlays

    $summaryPath = Join-Path $syntheticEval "vision_eval_summary.json"
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        throw "Synthetic vision evaluation summary was not written: $summaryPath"
    }

    $csvPath = Join-Path $syntheticEval "vision_eval.csv"
    if (-not (Test-Path -LiteralPath $csvPath)) {
        throw "Synthetic vision evaluation CSV was not written: $csvPath"
    }

    $reportPath = Join-Path $syntheticEval "vision_eval_report.html"
    if (-not (Test-Path -LiteralPath $reportPath)) {
        throw "Synthetic vision evaluation HTML report was not written: $reportPath"
    }

    $reportText = Get-Content -LiteralPath $reportPath -Raw
    if ($reportText -notmatch "Avatar Builder Vision Evaluation" -or
        $reportText -notmatch "overlay_frames/frame_") {
        throw "Synthetic vision evaluation HTML report did not include the expected title and overlay frame references."
    }

    if ($reportText -notmatch "Raw vs Working Eye" -or
        $reportText -notmatch "Raw vs Working Mouth" -or
        $reportText -notmatch "Largest raw/working eye correction" -or
        $reportText -notmatch "raw eye") {
        throw "Synthetic vision evaluation HTML report did not include raw-vs-working correction audit fields."
    }

    foreach ($requiredReviewLabel in @("XYZABC", "Lowest A rotation around X", "Highest B rotation around Y", "Closest Z/apparent scale", "Farthest Z/apparent scale")) {
        if ($reportText -notmatch [regex]::Escape($requiredReviewLabel)) {
            throw "Synthetic vision evaluation HTML report did not include XYZABC/Z review evidence: $requiredReviewLabel"
        }
    }

    $csvHeader = Get-Content -LiteralPath $csvPath -TotalCount 1
    foreach ($requiredHeader in @("RawLeftEyeOpening", "RawRightEyeOpening", "RawAverageEyeOpening", "RawMouthOpening", "RawJawDroop", "JawDroop", "JawDroopVelocity", "FaceReliabilityStatus", "FaceReliability", "FaceContinuity", "EyeReliability", "MouthReliability", "MediaPipeEyeOpeningCorrection", "MediaPipeMouthOpeningCorrection", "MediaPipeEyeOpeningCorrected", "MediaPipeMouthOpeningCorrected", "CueJawDroopChange", "CueStatus", "CueBaselineReady", "CueEyeClosure", "CueMouthOpeningChange", "CueMediaPipeBlinkChange", "EyeInsetCueStatus", "EyeInsetCueClosure", "EyeInsetCueScore")) {
        if ($csvHeader -notmatch "(^|,)$requiredHeader(,|$)") {
            throw "Synthetic vision evaluation CSV is missing cue evidence column: $requiredHeader"
        }
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if ($summary.FrameCount -lt 20) {
        throw "Synthetic vision evaluation sampled too few frames: $($summary.FrameCount)"
    }

    if ([string]::IsNullOrWhiteSpace($summary.ReportPath) -or -not (Test-Path -LiteralPath $summary.ReportPath)) {
        throw "Synthetic vision evaluation summary did not point to an existing HTML report: $($summary.ReportPath)"
    }

    if (-not $summary.OverlayFramesEnabled -or $summary.OverlayFrameCount -lt $summary.FrameCount) {
        throw "Synthetic vision evaluation did not write overlay audit frames for sampled frames. Overlay count: $($summary.OverlayFrameCount), frame count: $($summary.FrameCount)"
    }

    if ($summary.FaceDetectionRate -lt 0.85) {
        throw "Synthetic full-frame face detection rate too low: $($summary.FaceDetectionRate)"
    }

    if ($summary.LandmarkEyeMeasurementRate -lt 0.85) {
        throw "Synthetic full-frame landmark eye measurement rate too low: $($summary.LandmarkEyeMeasurementRate)"
    }

    if ($summary.LandmarkMouthMeasurementRate -lt 0.85) {
        throw "Synthetic full-frame landmark mouth measurement rate too low: $($summary.LandmarkMouthMeasurementRate)"
    }

    if ($summary.LandmarkEyeUsableRate -lt 0.50) {
        throw "Synthetic full-frame usable landmark eye rate too low: $($summary.LandmarkEyeUsableRate)"
    }

    if ($summary.LandmarkMouthUsableRate -lt 0.50) {
        throw "Synthetic full-frame usable landmark mouth rate too low: $($summary.LandmarkMouthUsableRate)"
    }

    if ($null -eq $summary.LandmarkAverageOverallQuality -or $summary.LandmarkAverageOverallQuality -lt 45) {
        throw "Synthetic full-frame average landmark quality too low: $($summary.LandmarkAverageOverallQuality)"
    }

    if ($null -eq $summary.FaceReliabilityAverage -or
        $null -eq $summary.FaceContinuityAverage -or
        $null -eq $summary.EyeReliabilityAverage -or
        $null -eq $summary.MouthReliabilityAverage) {
        throw "Synthetic full-frame face reliability evidence summary fields were not exported."
    }

    if ($summary.FaceReliabilityAverage -lt 55) {
        throw "Synthetic full-frame face reliability too low: $($summary.FaceReliabilityAverage)"
    }

    if ($summary.LandmarkEyeImageQualityRate -lt 0.25) {
        throw "Synthetic full-frame eye image diagnostic rate too low: $($summary.LandmarkEyeImageQualityRate)"
    }

    if ($null -eq $summary.LandmarkAverageEyeContrast -or $summary.LandmarkAverageEyeContrast -lt 20) {
        throw "Synthetic full-frame eye contrast diagnostics too weak: $($summary.LandmarkAverageEyeContrast)"
    }

    if ($null -eq $summary.LandmarkAverageEyeSharpness -or $summary.LandmarkAverageEyeSharpness -lt 20) {
        throw "Synthetic full-frame eye sharpness diagnostics too weak: $($summary.LandmarkAverageEyeSharpness)"
    }

    if ($null -eq $summary.LandmarkAverageEyeDarkCoverage -or $summary.LandmarkAverageEyeDarkCoverage -lt 5) {
        throw "Synthetic full-frame eye dark-aperture coverage diagnostics too weak: $($summary.LandmarkAverageEyeDarkCoverage)"
    }

    if ($null -eq $summary.LandmarkMaximumRawEyeAsymmetry -or $summary.LandmarkMaximumRawEyeAsymmetry -lt 15) {
        throw "Synthetic full-frame raw eye asymmetry evidence was not exported: $($summary.LandmarkMaximumRawEyeAsymmetry)"
    }

    $mediaPipeSidecarReady = Test-MediaPipeSidecarRuntimeReady
    $denseNativeReady = Test-DenseFullFaceRuntimeReady
    $mediaPipeSustainedDenseReady = $mediaPipeSidecarReady -and $null -ne $summary.MediaPipeDenseLockRate -and $summary.MediaPipeDenseLockRate -ge 0.25
    $sustainedDenseFullFaceReady = $denseNativeReady -or $mediaPipeSustainedDenseReady

    if ($mediaPipeSidecarReady) {
        foreach ($requiredSummaryField in @("MediaPipeDenseLockFrames", "MediaPipeDenseLockRate", "MediaPipeBlendshapeFrames", "MediaPipeBlendshapeFrameRate")) {
            if ($summary.PSObject.Properties.Name -notcontains $requiredSummaryField) {
                throw "Synthetic vision evaluation summary is missing MediaPipe dense contribution field: $requiredSummaryField"
            }
        }

        if ($summary.MediaPipeDenseLockFrames -lt 1) {
            throw "MediaPipe sidecar runtime is ready, but the synthetic evaluation did not record any dense landmark lock frames."
        }

        if ($summary.MediaPipeBlendshapeFrames -lt 1) {
            throw "MediaPipe sidecar runtime is ready, but the synthetic evaluation did not export any blendshape evidence frames."
        }

        Write-Host "MediaPipe sidecar dense contribution verified: dense locks $($summary.MediaPipeDenseLockFrames)/$($summary.FrameCount), blendshape frames $($summary.MediaPipeBlendshapeFrames)/$($summary.FrameCount)"
    }

    if ($sustainedDenseFullFaceReady -and
        ($null -eq $summary.LandmarkMinimumEyeAgreement -or $summary.LandmarkMinimumEyeAgreement -lt 50)) {
        throw "Synthetic full-frame eye agreement fell too low for the controlled clip: $($summary.LandmarkMinimumEyeAgreement)"
    }

    if ($summary.LandmarkPossibleOneEyeArtifactFrames -ne 0) {
        throw "Synthetic full-frame clean clip incorrectly flagged one-eye artifacts: $($summary.LandmarkPossibleOneEyeArtifactFrames)"
    }

    if ($null -eq $summary.LandmarkLeftEyeReconstructedFrames -or
        $null -eq $summary.LandmarkRightEyeReconstructedFrames -or
        $null -eq $summary.LandmarkMouthReconstructedFrames -or
        $null -eq $summary.LandmarkEyeArtifactSuppressedFrames) {
        throw "Synthetic full-frame reconstruction evidence fields were not exported."
    }

    if ($null -eq $summary.LandmarkCueUsableRate -or
        $null -eq $summary.LandmarkCueBaselineReadyRate -or
        $null -eq $summary.LandmarkMaximumCueScore -or
        $null -eq $summary.MediaPipeCueBlinkBaselineReadyRate) {
        throw "Synthetic full-frame cue evidence summary fields were not exported."
    }

    foreach ($requiredSummaryField in @("MediaPipeEyeOpeningCorrectedFrames", "MediaPipeMouthOpeningCorrectedFrames", "MediaPipeMaximumAbsoluteEyeOpeningCorrection", "MediaPipeMaximumAbsoluteMouthOpeningCorrection")) {
        if ($summary.PSObject.Properties.Name -notcontains $requiredSummaryField) {
            throw "Synthetic vision evaluation summary is missing MediaPipe correction field: $requiredSummaryField"
        }
    }

    foreach ($requiredSummaryField in @("RawAverageEyeOpening", "RawMinimumEyeOpening", "RawEyeOpeningSlopePerSecond", "EyeOpeningRawWorkingPairedFrames", "EyeOpeningRawWorkingMaximumAbsoluteDelta", "RawAverageMouthOpening", "RawMouthOpeningSlopePerSecond", "MouthOpeningRawWorkingPairedFrames", "MouthOpeningRawWorkingMaximumAbsoluteDelta")) {
        if ($summary.PSObject.Properties.Name -notcontains $requiredSummaryField) {
            throw "Synthetic vision evaluation summary is missing raw-vs-working audit field: $requiredSummaryField"
        }
    }

    if ($summary.EyeOpeningRawWorkingPairedFrames -lt 20) {
        throw "Synthetic raw-vs-working eye audit had too few paired frames: $($summary.EyeOpeningRawWorkingPairedFrames)"
    }

    if ($null -eq $summary.EyeOpeningRawWorkingMaximumAbsoluteDelta -or $summary.EyeOpeningRawWorkingMaximumAbsoluteDelta -lt 0.03) {
        throw "Synthetic raw-vs-working eye audit did not expose a meaningful correction delta: $($summary.EyeOpeningRawWorkingMaximumAbsoluteDelta)"
    }

    if ($summary.MouthOpeningRawWorkingPairedFrames -lt 20) {
        throw "Synthetic raw-vs-working mouth audit had too few paired frames: $($summary.MouthOpeningRawWorkingPairedFrames)"
    }

    if ($null -eq $summary.MouthOpeningRawWorkingMaximumAbsoluteDelta -or $summary.MouthOpeningRawWorkingMaximumAbsoluteDelta -lt 0.03) {
        throw "Synthetic raw-vs-working mouth audit did not expose a meaningful correction delta: $($summary.MouthOpeningRawWorkingMaximumAbsoluteDelta)"
    }

    if ($summary.LandmarkCueUsableRate -lt 0.30) {
        throw "Synthetic full-frame cue usable rate too low: $($summary.LandmarkCueUsableRate)"
    }

    if ($sustainedDenseFullFaceReady) {
        if ($summary.LandmarkTrendUsableRate -lt 0.25) {
            throw "Synthetic full-frame landmark trend usable rate too low: $($summary.LandmarkTrendUsableRate)"
        }

        if ($null -eq $summary.LandmarkMaximumTrendCueScore -or $summary.LandmarkMaximumTrendCueScore -lt 12) {
            throw "Synthetic full-frame landmark trend cue score too low: $($summary.LandmarkMaximumTrendCueScore)"
        }

        $fullFrameEyeClosureTrendDetected =
            $null -ne $summary.EyeOpeningSlopePerSecond -and
            $summary.EyeOpeningSlopePerSecond -lt -0.005 -and
            $null -ne $summary.LandmarkMaximumEyeClosingTrend -and
            $summary.LandmarkMaximumEyeClosingTrend -ge 15 -and
            $null -ne $summary.LandmarkMinimumTrendEyeSlope -and
            $summary.LandmarkMinimumTrendEyeSlope -lt -0.005
        if ($fullFrameEyeClosureTrendDetected) {
            Write-Host "Synthetic full-frame eye trend verified: rolling closure $($summary.LandmarkMaximumEyeClosingTrend), slope $($summary.EyeOpeningSlopePerSecond), trend slope $($summary.LandmarkMinimumTrendEyeSlope)"
        }
        else {
            Write-Host "Synthetic full-frame dense lock is sustained, but this controlled clip's eyelid closure is validated by the bottom-right eye inset. Full-frame eye slope: $($summary.EyeOpeningSlopePerSecond); inset slope: $($summary.EyeInsetOpeningSlopePerSecond)."
        }

        if ($null -eq $summary.MouthOpeningSlopePerSecond -or $summary.MouthOpeningSlopePerSecond -le 0.008) {
            throw "Synthetic full-frame mouth opening trend was not detected. Slope: $($summary.MouthOpeningSlopePerSecond)"
        }
    }
    elseif ($mediaPipeSidecarReady) {
        Write-Host "MediaPipe sidecar is ready and contributed dense frames, but sustained dense lock rate is $($summary.MediaPipeDenseLockRate); strict dense trend gates remain deferred until dense lock coverage reaches 25%."
    }
    else {
        Write-Host "Dense full-face landmark runtime is not ready yet; strict full-face eye and mouth slope gates are deferred to the eye-inset trend gate and synthetic metric smoke tests."
    }

    if ($null -eq $summary.FaceCenterXRange -or $summary.FaceCenterXRange -lt 0.05) {
        throw "Synthetic full-frame face tracker did not cover enough horizontal motion. Range: $($summary.FaceCenterXRange)"
    }

    if ($null -eq $summary.FaceCenterYRange -or $summary.FaceCenterYRange -lt 0.02) {
        throw "Synthetic full-frame face tracker did not cover enough vertical motion. Range: $($summary.FaceCenterYRange)"
    }

    if ($null -eq $summary.FaceHeightRange -or $summary.FaceHeightRange -lt 0.04) {
        throw "Synthetic full-frame face tracker did not cover enough scale change. Range: $($summary.FaceHeightRange)"
    }

    if ($null -eq $summary.FaceCenterXMinimum -or $summary.FaceCenterXMinimum -lt 0.30) {
        throw "Synthetic full-frame face tracker drifted too far left. Minimum center X: $($summary.FaceCenterXMinimum)"
    }

    if ($null -eq $summary.FaceCenterXMaximum -or $summary.FaceCenterXMaximum -gt 0.65) {
        throw "Synthetic full-frame face tracker drifted too far right. Maximum center X: $($summary.FaceCenterXMaximum)"
    }

    if ($null -eq $summary.FaceCenterXAverage -or $summary.FaceCenterXAverage -lt 0.38 -or $summary.FaceCenterXAverage -gt 0.55) {
        throw "Synthetic full-frame face tracker did not stay centered on the moving subject. Average center X: $($summary.FaceCenterXAverage)"
    }

    if ($summary.EyeInsetMeasurementRate -lt 0.80) {
        throw "Synthetic eye inset measurement rate too low: $($summary.EyeInsetMeasurementRate)"
    }

    if ([string]::IsNullOrWhiteSpace($summary.EyeInsetDominantRegion) -or
        $summary.EyeInsetDominantRegion -notmatch "bottom-right") {
        throw "Synthetic auto eye inset did not lock the expected bottom-right region: $($summary.EyeInsetDominantRegion)"
    }

    if ($summary.EyeInsetImageQualityRate -lt 0.80) {
        throw "Synthetic eye inset image diagnostic rate too low: $($summary.EyeInsetImageQualityRate)"
    }

    if ($null -eq $summary.EyeInsetAverageContrast -or $summary.EyeInsetAverageContrast -lt 20) {
        throw "Synthetic eye inset contrast diagnostics too weak: $($summary.EyeInsetAverageContrast)"
    }

    if ($null -eq $summary.EyeInsetAverageSharpness -or $summary.EyeInsetAverageSharpness -lt 20) {
        throw "Synthetic eye inset sharpness diagnostics too weak: $($summary.EyeInsetAverageSharpness)"
    }

    if ($null -eq $summary.EyeInsetAverageDarkCoverage -or $summary.EyeInsetAverageDarkCoverage -lt 5) {
        throw "Synthetic eye inset dark-aperture coverage diagnostics too weak: $($summary.EyeInsetAverageDarkCoverage)"
    }

    if ($null -eq $summary.EyeInsetOpeningSlopePerSecond -or $summary.EyeInsetOpeningSlopePerSecond -ge -0.002) {
        throw "Synthetic eye inset closure trend was not detected. Slope: $($summary.EyeInsetOpeningSlopePerSecond)"
    }

    if ($summary.EyeInsetCueBaselineReadyRate -lt 0.20) {
        throw "Synthetic eye inset cue baseline was not ready often enough: $($summary.EyeInsetCueBaselineReadyRate)"
    }

    if ($null -eq $summary.EyeInsetCueMaximumClosure -or $summary.EyeInsetCueMaximumClosure -lt 18) {
        throw "Synthetic eye inset cue closure was too weak: $($summary.EyeInsetCueMaximumClosure)"
    }

    if ($null -eq $summary.EyeInsetCueMaximumScore -or $summary.EyeInsetCueMaximumScore -lt 15) {
        throw "Synthetic eye inset cue score was too weak: $($summary.EyeInsetCueMaximumScore)"
    }

    if ($null -eq $summary.EyeInsetFullFrameAgreementTrustPercent -or
        $summary.EyeInsetFullFrameAgreementTrustPercent -lt 0 -or
        $summary.EyeInsetFullFrameAgreementTrustPercent -gt 100) {
        throw "Synthetic full-frame/eye-inset agreement trust was not exported as a bounded score: $($summary.EyeInsetFullFrameAgreementTrustPercent)"
    }

    Write-Host "Synthetic eye inset trend verified: region $($summary.EyeInsetDominantRegion), measurement rate $($summary.EyeInsetMeasurementRate), diagnostic rate $($summary.EyeInsetImageQualityRate), slope $($summary.EyeInsetOpeningSlopePerSecond), full/inset trust $($summary.EyeInsetFullFrameAgreementTrustPercent)"
    Write-Host "Synthetic full-frame face trend verified: face rate $($summary.FaceDetectionRate), usable eyes $($summary.LandmarkEyeUsableRate), cue usable $($summary.LandmarkCueUsableRate), quality $($summary.LandmarkAverageOverallQuality), diagnostic eyes $($summary.LandmarkEyeImageQualityRate), rolling eye trend $($summary.LandmarkMaximumEyeClosingTrend), eye slope $($summary.EyeOpeningSlopePerSecond), mouth slope $($summary.MouthOpeningSlopePerSecond)"
}

Invoke-Step "Run synthetic no-inset auto rejection evaluation" {
    $syntheticRoot = Join-Path $repoRoot "output\vision-synthetic"
    $syntheticVideo = Join-Path $syntheticRoot "sleepy_no_eye_inset.avi"
    $syntheticEval = Join-Path $syntheticRoot "no_inset_eval"
    New-Item -ItemType Directory -Force -Path $syntheticRoot | Out-Null
    if (Test-Path -LiteralPath $syntheticEval) {
        Remove-Item -LiteralPath $syntheticEval -Recurse -Force
    }

    Invoke-Checked dotnet $smokeAssemblyPath --write-synthetic-no-inset-video $syntheticVideo
    Invoke-Checked dotnet $evalAssemblyPath $syntheticVideo $syntheticEval 6 --eye-inset auto --write-overlays

    $summaryPath = Join-Path $syntheticEval "vision_eval_summary.json"
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        throw "Synthetic no-inset vision evaluation summary was not written: $summaryPath"
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if ($summary.FaceDetectionRate -lt 0.85) {
        throw "Synthetic no-inset full-frame face detection rate too low: $($summary.FaceDetectionRate)"
    }

    if ($summary.LandmarkEyeMeasurementRate -lt 0.85 -or $summary.LandmarkMouthMeasurementRate -lt 0.85) {
        throw "Synthetic no-inset full-frame landmark measurements were too weak: eyes=$($summary.LandmarkEyeMeasurementRate), mouth=$($summary.LandmarkMouthMeasurementRate)"
    }

    if ($summary.EyeInsetEnabled -or $summary.EyeInsetMeasurementRate -gt 0 -or -not [string]::IsNullOrWhiteSpace($summary.EyeInsetDominantRegion)) {
        throw "Synthetic no-inset clip falsely accepted an automatic eye inset: enabled=$($summary.EyeInsetEnabled), rate=$($summary.EyeInsetMeasurementRate), region=$($summary.EyeInsetDominantRegion)"
    }

    Write-Host "Synthetic no-inset auto rejection verified: face rate $($summary.FaceDetectionRate), eye rate $($summary.LandmarkEyeMeasurementRate), mouth rate $($summary.LandmarkMouthMeasurementRate), inset enabled $($summary.EyeInsetEnabled)."
}

Invoke-Step "Run synthetic landmark stress evaluation" {
    $syntheticRoot = Join-Path $repoRoot "output\vision-synthetic"
    $stressJson = Join-Path $syntheticRoot "landmark_stress.json"
    New-Item -ItemType Directory -Force -Path $syntheticRoot | Out-Null
    if (Test-Path -LiteralPath $stressJson) {
        Remove-Item -LiteralPath $stressJson -Force
    }

    Invoke-Checked dotnet $smokeAssemblyPath --write-landmark-stress $stressJson
    if (-not (Test-Path -LiteralPath $stressJson)) {
        throw "Synthetic landmark stress JSON was not written: $stressJson"
    }

    $stress = Get-Content -LiteralPath $stressJson -Raw | ConvertFrom-Json
    if ($stress.FrameCount -lt 60) {
        throw "Synthetic landmark stress sampled too few frames: $($stress.FrameCount)"
    }

    if ($stress.AggregateSamples -ne $stress.FrameCount -or $stress.TimelineSampleCount -ne $stress.FrameCount) {
        throw "Synthetic landmark stress did not route every frame through aggregate/timeline evidence. Frames: $($stress.FrameCount), aggregate: $($stress.AggregateSamples), timeline: $($stress.TimelineSampleCount)"
    }

    if (-not (Test-Path -LiteralPath $stress.TimelineJsonPath) -or -not (Test-Path -LiteralPath $stress.TimelineCsvPath)) {
        throw "Synthetic landmark stress timeline files were not written: JSON=$($stress.TimelineJsonPath), CSV=$($stress.TimelineCsvPath)"
    }

    if ([string]::IsNullOrWhiteSpace($stress.PersonalModelPath) -or -not (Test-Path -LiteralPath $stress.PersonalModelPath)) {
        throw "Synthetic landmark stress personal face model was not written: $($stress.PersonalModelPath)"
    }

    $personalModel = Get-Content -LiteralPath $stress.PersonalModelPath -Raw | ConvertFrom-Json
    foreach ($requiredProperty in @("ModelVersion", "ObservedSamples", "AcceptedSamples", "RejectedSamples", "EventLikeRejectedSamples", "TrackingArtifactRejectedSamples", "AverageEyeOpeningRatio", "MouthOpeningRatio", "JawDroopRatio", "FaceCenterX", "FaceWidth", "ZApparentDistanceUnits", "ZConfidencePercent", "ZEstimateSamples", "NoseBridgeShape", "LeftCheekSurface", "ForeheadSurface")) {
        if ($personalModel.PSObject.Properties.Name -notcontains $requiredProperty) {
            throw "Synthetic personal face model is missing property: $requiredProperty"
        }
    }

    if ($stress.PersonalModelObservedSamples -ne $stress.FrameCount -or $personalModel.ObservedSamples -ne $stress.FrameCount) {
        throw "Synthetic personal face model did not observe every stress frame. Frames=$($stress.FrameCount), summary=$($stress.PersonalModelObservedSamples), model=$($personalModel.ObservedSamples)"
    }

    foreach ($requiredProperty in @("PersonalFaceCorpusDataAuditHealthPercent", "PersonalFaceCorpusPoseEstimationHealthPercent", "PersonalFaceCorpusFeatureAnchoringHealthPercent", "PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent", "PersonalFaceCorpusEyeApertureReliabilityHealthPercent", "PersonalFaceCorpusMouthVerticalAnchorHealthPercent", "PersonalFaceCorpusJawDroopScaleHealthPercent", "PersonalFaceCorpusSurfaceShapeCoveragePercent", "PersonalFaceCorpusSurfaceDepthProfileHealthPercent", "PersonalFaceCorpusContourDepthProfileHealthPercent", "PersonalFaceCorpusXYZABCCoveragePercent", "PersonalFaceCorpusZDistanceEvidenceHealthPercent", "PersonalModelSurfaceShapeCoveragePercent", "PersonalModelXYZABCCoveragePercent", "PersonalFaceCorpusDataAuditFindings")) {
        if ($stress.PSObject.Properties.Name -notcontains $requiredProperty) {
            throw "Synthetic landmark stress summary is missing data-audit property: $requiredProperty"
        }
    }

    if ($stress.PersonalFaceCorpusDataAuditHealthPercent -lt 70) {
        throw "Synthetic landmark stress data audit health is too low: $($stress.PersonalFaceCorpusDataAuditHealthPercent)%"
    }

    if ($stress.PersonalFaceCorpusPoseEstimationHealthPercent -lt 50) {
        throw "Synthetic landmark stress pose audit health is too low: $($stress.PersonalFaceCorpusPoseEstimationHealthPercent)%"
    }

    if ($stress.PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent -lt 60) {
        throw "Synthetic landmark stress pose-explained feature motion health is too low: $($stress.PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent)%"
    }

    if ($stress.PersonalFaceCorpusEyeApertureReliabilityHealthPercent -lt 70) {
        throw "Synthetic landmark stress eye aperture reliability health is too low: $($stress.PersonalFaceCorpusEyeApertureReliabilityHealthPercent)%"
    }

    if ($stress.PersonalFaceCorpusMouthVerticalAnchorHealthPercent -lt 70) {
        throw "Synthetic landmark stress mouth vertical anchor health is too low: $($stress.PersonalFaceCorpusMouthVerticalAnchorHealthPercent)%"
    }

    if ($stress.PersonalFaceCorpusJawDroopScaleHealthPercent -lt 90) {
        throw "Synthetic landmark stress jaw scale audit health is too low: $($stress.PersonalFaceCorpusJawDroopScaleHealthPercent)%"
    }

    if ([string]::IsNullOrWhiteSpace($stress.MeasurementAvatarTrainingPackageJsonPath) -or
        -not (Test-Path -LiteralPath $stress.MeasurementAvatarTrainingPackageJsonPath)) {
        throw "Synthetic landmark stress avatar training package was not written: $($stress.MeasurementAvatarTrainingPackageJsonPath)"
    }

    $avatarPackage = Get-Content -LiteralPath $stress.MeasurementAvatarTrainingPackageJsonPath -Raw | ConvertFrom-Json
    foreach ($requiredReadinessProperty in @("PoseEstimationHealthPercent", "FeatureAnchoringHealthPercent", "PoseExplainedFeatureMotionHealthPercent", "EyeApertureReliabilityHealthPercent", "MouthVerticalAnchorHealthPercent", "JawDroopScaleHealthPercent", "SurfaceShapeCoveragePercent", "SurfaceDepthProfileHealthPercent", "ContourDepthProfileHealthPercent", "ZDistanceCoveragePercent", "ZDistanceEvidenceHealthPercent", "ARotationAroundXCoveragePercent", "BRotationAroundYCoveragePercent", "CRotationAroundZCoveragePercent", "XYZABCCoveragePercent", "MeasurementJournalCoveragePercent")) {
        if ($avatarPackage.Readiness.PSObject.Properties.Name -notcontains $requiredReadinessProperty) {
            throw "Synthetic avatar package readiness is missing audit property: $requiredReadinessProperty"
        }
    }

    foreach ($requiredQualityProperty in @("BRotationAroundYRangeDegrees", "ARotationAroundXRangeDegrees", "CRotationAroundZRangeDegrees", "ZDistanceEvidenceHealthPercent", "ZApparentDistanceRange", "AverageZConfidencePercent", "ContourDepthProfileHealthPercent", "SurfaceDepthProfileHealthPercent", "InterEyeDistanceToFaceWidthRange", "MouthWidthToFaceWidthRange")) {
        if ($avatarPackage.QualityProfile.PSObject.Properties.Name -notcontains $requiredQualityProperty) {
            throw "Synthetic avatar package quality profile is missing drift-audit metric: $requiredQualityProperty"
        }
    }

    if ($stress.PersonalModelAcceptedSamples -lt 18 -or $personalModel.AcceptedSamples -lt 18) {
        throw "Synthetic personal face model accepted too few stable samples. Summary=$($stress.PersonalModelAcceptedSamples), model=$($personalModel.AcceptedSamples)"
    }

    if ($null -eq $stress.PersonalModelAcceptedSampleWeight -or
        $stress.PersonalModelAcceptedSampleWeight -le 0 -or
        $personalModel.AcceptedSampleWeight -le 0) {
        throw "Synthetic personal face model did not retain accepted sample weights. Summary=$($stress.PersonalModelAcceptedSampleWeight), model=$($personalModel.AcceptedSampleWeight)"
    }

    if ($stress.PersonalModelEventLikeRejectedSamples -lt 8 -or $personalModel.EventLikeRejectedSamples -lt 8) {
        throw "Synthetic personal face model did not reject enough event-like samples. Summary=$($stress.PersonalModelEventLikeRejectedSamples), model=$($personalModel.EventLikeRejectedSamples)"
    }

    if ($null -eq $stress.PersonalModelAverageEyeOpening -or
        $null -eq $stress.PersonalModelAverageMouthOpening -or
        $null -eq $stress.PersonalModelAverageJawDroop) {
        throw "Synthetic personal face model did not export learned eye/mouth/jaw baselines."
    }

    if ($null -eq $stress.PersonalModelEyeOpeningWeight -or
        $null -eq $stress.PersonalModelMouthOpeningWeight -or
        $stress.PersonalModelEyeOpeningWeight -le 0 -or
        $stress.PersonalModelMouthOpeningWeight -le 0) {
        throw "Synthetic personal face model did not export weighted eye/mouth distributions."
    }

    if ($null -eq $stress.PersonalModelFaceCenterXRange -or
        $null -eq $stress.PersonalModelFaceCenterYRange -or
        $null -eq $stress.PersonalModelFaceWidthRange) {
        throw "Synthetic personal face model did not export face pose/scale ranges."
    }

    $stressTimelineHeader = Get-Content -LiteralPath $stress.TimelineCsvPath -First 1
    foreach ($requiredHeader in @("FaceReliabilityStatus", "FaceReliabilitySamples", "FaceReliability", "FaceContinuity", "EyeReliability", "MouthReliability", "FaceBoundsRate", "EyeUsableRate", "MouthUsableRate")) {
        if ($stressTimelineHeader -notmatch "(^|,)$requiredHeader(,|$)") {
            throw "Synthetic landmark stress timeline CSV is missing reliability column: $requiredHeader"
        }
    }

    $stressTimelineSamples = Get-Content -LiteralPath $stress.TimelineJsonPath -Raw | ConvertFrom-Json
    if ($stressTimelineSamples.Count -ne $stress.FrameCount) {
        throw "Synthetic landmark stress timeline JSON sample count mismatch. Frames: $($stress.FrameCount), timeline JSON: $($stressTimelineSamples.Count)"
    }

    $firstReliabilitySample = $stressTimelineSamples | Select-Object -First 1
    foreach ($requiredProperty in @("FaceReliabilityStatus", "FaceReliabilitySamples", "FaceReliability", "FaceContinuity", "EyeReliability", "MouthReliability", "FaceBoundsRate", "EyeUsableRate", "MouthUsableRate")) {
        if ($firstReliabilitySample.PSObject.Properties.Name -notcontains $requiredProperty) {
            throw "Synthetic landmark stress timeline JSON is missing reliability property: $requiredProperty"
        }
    }

    if ($stress.AggregateFaceReliabilitySamples -ne $stress.FrameCount) {
        throw "Synthetic landmark stress aggregate did not retain face reliability for every frame. Frames: $($stress.FrameCount), reliability samples: $($stress.AggregateFaceReliabilitySamples)"
    }

    if ($stress.AggregateFaceReliabilityUsableSamples -lt [Math]::Floor($stress.FrameCount * 0.80)) {
        throw "Synthetic landmark stress aggregate face reliability usable count too low: $($stress.AggregateFaceReliabilityUsableSamples) of $($stress.FrameCount)"
    }

    if ($null -eq $stress.AggregateAverageFaceReliability -or $stress.AggregateAverageFaceReliability -lt 75) {
        throw "Synthetic landmark stress aggregate face reliability average too low: $($stress.AggregateAverageFaceReliability)"
    }

    if ($null -eq $stress.AggregateAverageFaceContinuity -or $stress.AggregateAverageFaceContinuity -lt 75) {
        throw "Synthetic landmark stress aggregate face continuity average too low: $($stress.AggregateAverageFaceContinuity)"
    }

    if ($null -eq $stress.AggregateAverageEyeReliability -or $stress.AggregateAverageEyeReliability -lt 70) {
        throw "Synthetic landmark stress aggregate eye reliability average too low: $($stress.AggregateAverageEyeReliability)"
    }

    if ($null -eq $stress.AggregateAverageMouthReliability -or $stress.AggregateAverageMouthReliability -lt 70) {
        throw "Synthetic landmark stress aggregate mouth reliability average too low: $($stress.AggregateAverageMouthReliability)"
    }

    if ($null -eq $stress.FirstAverageEyeOpening -or
        $null -eq $stress.LastAverageEyeOpening -or
        ($stress.FirstAverageEyeOpening - $stress.LastAverageEyeOpening) -lt 0.12) {
        throw "Synthetic landmark stress did not preserve eyelid closure magnitude. First=$($stress.FirstAverageEyeOpening), last=$($stress.LastAverageEyeOpening)"
    }

    if ($null -eq $stress.EyeOpeningSlopePerSecond -or $stress.EyeOpeningSlopePerSecond -ge -0.02) {
        throw "Synthetic landmark stress eyelid slope was not negative enough: $($stress.EyeOpeningSlopePerSecond)"
    }

    if ($null -eq $stress.FirstAverageMouthOpening -or
        $null -eq $stress.LastAverageMouthOpening -or
        ($stress.LastAverageMouthOpening - $stress.FirstAverageMouthOpening) -lt 0.14) {
        throw "Synthetic landmark stress did not preserve mouth opening growth. First=$($stress.FirstAverageMouthOpening), last=$($stress.LastAverageMouthOpening)"
    }

    if ($null -eq $stress.MouthOpeningSlopePerSecond -or $stress.MouthOpeningSlopePerSecond -le 0.025) {
        throw "Synthetic landmark stress mouth opening slope was too weak: $($stress.MouthOpeningSlopePerSecond)"
    }

    if ($null -eq $stress.FirstAverageJawDroop -or
        $null -eq $stress.LastAverageJawDroop -or
        ($stress.LastAverageJawDroop - $stress.FirstAverageJawDroop) -lt 0.16) {
        throw "Synthetic landmark stress did not preserve structural jaw droop growth. First=$($stress.FirstAverageJawDroop), last=$($stress.LastAverageJawDroop)"
    }

    if ($null -eq $stress.JawDroopSlopePerSecond -or $stress.JawDroopSlopePerSecond -le 0.03) {
        throw "Synthetic landmark stress jaw droop slope was too weak: $($stress.JawDroopSlopePerSecond)"
    }

    if ($null -eq $stress.MaximumEyeClosureCue -or $stress.MaximumEyeClosureCue -lt 45) {
        throw "Synthetic landmark stress eye-closure cue was too weak: $($stress.MaximumEyeClosureCue)"
    }

    if ($null -eq $stress.MaximumMouthOpeningCue -or $stress.MaximumMouthOpeningCue -lt 80) {
        throw "Synthetic landmark stress mouth-opening cue was too weak: $($stress.MaximumMouthOpeningCue)"
    }

    if ($null -eq $stress.MaximumJawDroopCue -or $stress.MaximumJawDroopCue -lt 20) {
        throw "Synthetic landmark stress jaw-droop cue was too weak: $($stress.MaximumJawDroopCue)"
    }

    if ($null -eq $stress.MaximumCompositeCue -or $stress.MaximumCompositeCue -lt 45) {
        throw "Synthetic landmark stress composite cue was too weak: $($stress.MaximumCompositeCue)"
    }

    if ($stress.BaselineReadyRate -lt 0.50 -or $stress.EyeCueEligibleRate -lt 0.70 -or $stress.MouthCueEligibleRate -lt 0.70) {
        throw "Synthetic landmark stress cue eligibility/baseline rates too low. Baseline=$($stress.BaselineReadyRate), eye=$($stress.EyeCueEligibleRate), mouth=$($stress.MouthCueEligibleRate)"
    }

    if ($stress.ReconstructedEyeFrameCount -lt 8) {
        throw "Synthetic landmark stress did not exercise enough eye reconstruction frames: $($stress.ReconstructedEyeFrameCount)"
    }

    if ($stress.EyeArtifactSuppressedFrameCount -lt 4) {
        throw "Synthetic landmark stress did not exercise enough glasses artifact suppression frames: $($stress.EyeArtifactSuppressedFrameCount)"
    }

    if ($stress.MouthReconstructedFrameCount -lt 2) {
        throw "Synthetic landmark stress did not exercise enough mouth reconstruction frames: $($stress.MouthReconstructedFrameCount)"
    }

    if ($stress.EdgeStressFrameCount -lt 12) {
        throw "Synthetic landmark stress did not exercise enough off-center/edge face frames: $($stress.EdgeStressFrameCount)"
    }

    if ($null -eq $stress.MinimumFaceLeft -or
        $null -eq $stress.MaximumFaceRight -or
        $stress.MinimumFaceLeft -gt 0.10 -or
        $stress.MaximumFaceRight -lt 0.90) {
        throw "Synthetic landmark stress did not move the tracked face far enough across the frame. Left=$($stress.MinimumFaceLeft), right=$($stress.MaximumFaceRight)"
    }

    if ($null -eq $stress.MinimumFaceTop -or
        $null -eq $stress.MaximumFaceBottom -or
        $stress.MinimumFaceTop -gt 0.16 -or
        $stress.MaximumFaceBottom -lt 0.86) {
        throw "Synthetic landmark stress did not move the tracked face vertically enough across the frame. Top=$($stress.MinimumFaceTop), bottom=$($stress.MaximumFaceBottom)"
    }

    if ($stress.EdgeStressEyeMeasurementRate -lt 0.85 -or $stress.EdgeStressMouthMeasurementRate -lt 0.85) {
        throw "Synthetic landmark stress lost too much eye/mouth evidence near frame edges. Eye=$($stress.EdgeStressEyeMeasurementRate), mouth=$($stress.EdgeStressMouthMeasurementRate)"
    }

    if ($null -eq $stress.AverageOverallQuality -or $stress.AverageOverallQuality -lt 45) {
        throw "Synthetic landmark stress average quality too low: $($stress.AverageOverallQuality)"
    }

    if ($null -eq $stress.AggregateMaximumMouthOpeningChange -or $stress.AggregateMaximumMouthOpeningChange -lt 80) {
        throw "Synthetic landmark stress aggregate did not keep mouth change evidence: $($stress.AggregateMaximumMouthOpeningChange)"
    }

    if ($null -eq $stress.AggregateMaximumJawDroopChange -or $stress.AggregateMaximumJawDroopChange -lt 20) {
        throw "Synthetic landmark stress aggregate did not keep jaw droop evidence: $($stress.AggregateMaximumJawDroopChange)"
    }

    Write-Host "Synthetic landmark stress verified: eye slope $($stress.EyeOpeningSlopePerSecond), mouth slope $($stress.MouthOpeningSlopePerSecond), jaw slope $($stress.JawDroopSlopePerSecond), face reliability $($stress.AggregateAverageFaceReliability), continuity $($stress.AggregateAverageFaceContinuity), edge frames $($stress.EdgeStressFrameCount), reconstructed eyes $($stress.ReconstructedEyeFrameCount), artifact suppressed $($stress.EyeArtifactSuppressedFrameCount), personal model accepted $($stress.PersonalModelAcceptedSamples)/$($stress.PersonalModelObservedSamples)"
}

$samplePath = Get-ResolvedSamplePath
if (-not [string]::IsNullOrWhiteSpace($samplePath)) {
    Invoke-Step "Run offline sample media evaluation" {
        $resolvedSample = (Resolve-Path -LiteralPath $samplePath).Path
        $isSampleImage = Test-IsImagePath -Path $resolvedSample
        $effectiveEyeInset = if ($isSampleImage -and -not $PSBoundParameters.ContainsKey("EyeInset")) {
            "none"
        }
        else {
            $EyeInset
        }
        $output = if ([string]::IsNullOrWhiteSpace($EvaluationOutputFolder)) {
            Join-Path $repoRoot "output\vision-eval"
        }
        else {
            $EvaluationOutputFolder
        }

        $evaluationArgs = @(
            $evalAssemblyPath,
            $resolvedSample,
            $output,
            $SampleFramesPerSecond
        )

        if (-not [string]::IsNullOrWhiteSpace($effectiveEyeInset) -and
            -not $effectiveEyeInset.Equals("none", [StringComparison]::OrdinalIgnoreCase)) {
            $evaluationArgs += "--eye-inset"
            $evaluationArgs += $effectiveEyeInset
        }

        $evaluationArgs += "--write-overlays"
        Invoke-Checked dotnet @evaluationArgs

        $summaryPath = Join-Path $output "vision_eval_summary.json"
        if (-not (Test-Path -LiteralPath $summaryPath)) {
            throw "Sample media vision evaluation summary was not written: $summaryPath"
        }

        $csvPath = Join-Path $output "vision_eval.csv"
        if (-not (Test-Path -LiteralPath $csvPath)) {
            throw "Sample media vision evaluation CSV was not written: $csvPath"
        }

        $reportPath = Join-Path $output "vision_eval_report.html"
        if (-not (Test-Path -LiteralPath $reportPath)) {
            throw "Sample media vision evaluation HTML report was not written: $reportPath"
        }

        $reportText = Get-Content -LiteralPath $reportPath -Raw
        if ($reportText -notmatch "Avatar Builder Vision Evaluation" -or
            $reportText -notmatch "Selected Review Frames") {
            throw "Sample media vision evaluation HTML report did not include the expected review sections."
        }

        $csvHeader = Get-Content -LiteralPath $csvPath -TotalCount 1
        foreach ($requiredHeader in @("AverageEyeOpening", "MouthOpening", "JawDroop", "FaceReliabilityStatus", "FaceReliability", "FaceContinuity", "EyeReliability", "MouthReliability", "IdentityMeasurementAvailable", "EyeMidlineXToFaceWidth", "MouthCenterXToFaceWidth", "EyeToMouthXOffsetToFaceWidth", "MediaPipeEyeOpeningCorrection", "MediaPipeMouthOpeningCorrection", "MediaPipeEyeOpeningCorrected", "MediaPipeMouthOpeningCorrected", "CueEyeClosure", "CueMouthOpeningChange", "CueJawDroopChange", "BackendStatus")) {
            if ($csvHeader -notmatch "(^|,)$requiredHeader(,|$)") {
                throw "Sample media evaluation CSV is missing evidence column: $requiredHeader"
            }
        }

        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        if ($summary.FrameCount -lt 1) {
            throw "Sample media vision evaluation did not produce any frame records."
        }

        if ([string]::IsNullOrWhiteSpace($summary.ReportPath) -or -not (Test-Path -LiteralPath $summary.ReportPath)) {
            throw "Sample vision evaluation summary did not point to an existing HTML report: $($summary.ReportPath)"
        }

        if ($null -eq $summary.LandmarkCueUsableRate -or
            $null -eq $summary.LandmarkCueBaselineReadyRate -or
            $null -eq $summary.LandmarkMaximumCueScore) {
            throw "Sample vision evaluation did not export cue evidence summary fields."
        }

        if ($null -eq $summary.FaceReliabilityAverage -or
            $null -eq $summary.FaceContinuityAverage -or
            $null -eq $summary.EyeReliabilityAverage -or
            $null -eq $summary.MouthReliabilityAverage) {
            throw "Sample vision evaluation did not export face reliability evidence summary fields."
        }

        foreach ($requiredSummaryField in @("MediaPipeEyeOpeningCorrectedFrames", "MediaPipeMouthOpeningCorrectedFrames", "MediaPipeMaximumAbsoluteEyeOpeningCorrection", "MediaPipeMaximumAbsoluteMouthOpeningCorrection")) {
            if ($summary.PSObject.Properties.Name -notcontains $requiredSummaryField) {
                throw "Sample vision evaluation summary is missing MediaPipe correction field: $requiredSummaryField"
            }
        }

        if (-not $summary.OverlayFramesEnabled -or $summary.OverlayFrameCount -lt [Math]::Max(1, [Math]::Floor($summary.FrameCount * 0.90))) {
            throw "Sample media vision evaluation did not write overlay audit frames for most sampled frames. Overlay count: $($summary.OverlayFrameCount), frame count: $($summary.FrameCount)"
        }

        if ($summary.FaceDetectionRate -lt 0.50) {
            throw "Sample full-frame face detection rate too low for proof-of-concept validation: $($summary.FaceDetectionRate)"
        }

        if ($summary.LandmarkEyeMeasurementRate -lt 0.35) {
            throw "Sample full-frame eye measurement rate too low for proof-of-concept validation: $($summary.LandmarkEyeMeasurementRate)"
        }

        if ($summary.LandmarkCueUsableRate -lt 0.20) {
            throw "Sample cue usable rate too low for proof-of-concept validation: $($summary.LandmarkCueUsableRate)"
        }

        if (-not [string]::IsNullOrWhiteSpace($effectiveEyeInset) -and
            -not $effectiveEyeInset.Equals("none", [StringComparison]::OrdinalIgnoreCase)) {
            if ($summary.EyeInsetMeasurementRate -lt 0.60) {
                throw "Sample eye-inset measurement rate too low to use as glasses validation reference: $($summary.EyeInsetMeasurementRate)"
            }

            if ($summary.EyeInsetFullFramePairedRate -lt 0.30) {
                throw "Sample had too few paired full-frame/inset eye measurements: $($summary.EyeInsetFullFramePairedRate)"
            }

            if ($summary.FrameCount -ge 16 -and
                ($null -eq $summary.EyeInsetCueBaselineReadyRate -or $summary.EyeInsetCueBaselineReadyRate -lt 0.10)) {
                throw "Sample eye-inset cue baseline was not established often enough: $($summary.EyeInsetCueBaselineReadyRate)"
            }

            $insetShowsClosure = $null -ne $summary.EyeInsetOpeningSlopePerSecond -and $summary.EyeInsetOpeningSlopePerSecond -lt -0.002
            $fullFrameDisagrees = $null -eq $summary.EyeOpeningSlopePerSecond -or $summary.EyeOpeningSlopePerSecond -gt 0.001
            if ($insetShowsClosure -and $fullFrameDisagrees) {
                throw "Sample eye inset shows eyelid closure, but full-frame eye tracking did not agree. Full-frame slope: $($summary.EyeOpeningSlopePerSecond), inset slope: $($summary.EyeInsetOpeningSlopePerSecond)"
            }

            $hasDirectionAgreement = $null -ne $summary.EyeInsetFullFrameDirectionAgreement
            if ($hasDirectionAgreement -and $summary.EyeInsetFullFrameDirectionAgreement -lt 0.45) {
                throw "Sample full-frame eye measurements disagreed with the eye-inset reference too often. Direction agreement: $($summary.EyeInsetFullFrameDirectionAgreement)"
            }

            if ($null -eq $summary.EyeInsetFullFrameAgreementTrustPercent -or $summary.EyeInsetFullFrameAgreementTrustPercent -lt 35) {
                throw "Sample full-frame/eye-inset agreement trust too low: $($summary.EyeInsetFullFrameAgreementTrustPercent)"
            }
        }

        $sampleKind = if ($isSampleImage) { "image" } else { "video" }
        Write-Host "Sample $sampleKind evaluation verified: face rate $($summary.FaceDetectionRate), eye rate $($summary.LandmarkEyeMeasurementRate), mouth rate $($summary.LandmarkMouthMeasurementRate), jaw rate $($summary.LandmarkJawDroopMeasurementRate), cue usable $($summary.LandmarkCueUsableRate), cue baseline ready $($summary.LandmarkCueBaselineReadyRate), max cue $($summary.LandmarkMaximumCueScore), overlays $($summary.OverlayFrameCount), full/inset direction agreement $($summary.EyeInsetFullFrameDirectionAgreement), full/inset trust $($summary.EyeInsetFullFrameAgreementTrustPercent)"
    }
}
else {
    Write-Host ""
    Write-Host "No sample media path provided; skipped offline sample evaluation."
}

Write-Host ""
Write-Host "Avatar Builder verification completed."
