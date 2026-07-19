param(
    [int]$Iterations = 6,
    [string]$SampleMediaPath = "",
    [string]$SampleImagePath = "",
    [string]$SampleVideoPath = "",
    [double]$SampleFramesPerSecond = 2,
    [string]$EyeInset = "auto",
    [string]$OutputRoot = "",
    [switch]$StopOnFailure
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($Iterations -lt 1) {
    throw "Iterations must be at least 1."
}

$startedAt = Get-Date
$runStamp = $startedAt.ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "output\vision-soak\$runStamp"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$verifierPath = Join-Path $PSScriptRoot "VerifyAvatarBuilder.ps1"
$results = New-Object System.Collections.Generic.List[object]

function Get-SamplePath {
    if (-not [string]::IsNullOrWhiteSpace($SampleMediaPath)) {
        return $SampleMediaPath
    }

    if (-not [string]::IsNullOrWhiteSpace($SampleImagePath)) {
        return $SampleImagePath
    }

    return $SampleVideoPath
}

function Read-JsonObject {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-JsonValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object -or $Object.PSObject.Properties.Name -notcontains $Name) {
        return $null
    }

    return $Object.$Name
}

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $iterationStartedAt = Get-Date
    $iterationFolder = Join-Path $OutputRoot ("iteration_{0:D3}" -f $iteration)
    New-Item -ItemType Directory -Force -Path $iterationFolder | Out-Null
    $logPath = Join-Path $iterationFolder "verify.log"
    $sampleOutput = Join-Path $iterationFolder "sample-eval"
    $buildOutput = Join-Path ([IO.Path]::GetTempPath()) ("AvatarBuilderVerify\soak_{0}\iteration_{1:D3}" -f $runStamp, $iteration)
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $verifierPath,
        "-BuildOutputRoot",
        $buildOutput
    )

    $samplePath = Get-SamplePath
    if (-not [string]::IsNullOrWhiteSpace($samplePath)) {
        $arguments += "-SampleMediaPath"
        $arguments += $samplePath
        $arguments += "-EvaluationOutputFolder"
        $arguments += $sampleOutput
        $arguments += "-SampleFramesPerSecond"
        $arguments += $SampleFramesPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture)
        $arguments += "-EyeInset"
        $arguments += $EyeInset
    }

    Write-Host ""
    Write-Host "== Vision soak iteration $iteration of $Iterations =="
    Write-Host "Log: $logPath"
    $output = & powershell @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath $logPath
    $iterationEndedAt = Get-Date
    $sampleSummaryPath = Join-Path $sampleOutput "vision_eval_summary.json"
    $stressSummaryPath = Join-Path $repoRoot "output\vision-synthetic\landmark_stress.json"
    $syntheticSummaryPath = Join-Path $repoRoot "output\vision-synthetic\eval\vision_eval_summary.json"
    $sampleSummary = Read-JsonObject -Path $sampleSummaryPath
    $syntheticSummary = Read-JsonObject -Path $syntheticSummaryPath
    $stressSummary = Read-JsonObject -Path $stressSummaryPath

    $result = [ordered]@{
        Iteration = $iteration
        StartedAt = $iterationStartedAt.ToUniversalTime().ToString("O")
        EndedAt = $iterationEndedAt.ToUniversalTime().ToString("O")
        DurationSeconds = [Math]::Round(($iterationEndedAt - $iterationStartedAt).TotalSeconds, 3)
        ExitCode = $exitCode
        Passed = $exitCode -eq 0
        LogPath = $logPath
        BuildOutputRoot = $buildOutput
        SampleEvaluationOutput = if (Test-Path -LiteralPath $sampleOutput) { $sampleOutput } else { "" }
        SampleSummaryPath = if (Test-Path -LiteralPath $sampleSummaryPath) { $sampleSummaryPath } else { "" }
        SyntheticSummaryPath = if (Test-Path -LiteralPath $syntheticSummaryPath) { $syntheticSummaryPath } else { "" }
        LandmarkStressSummaryPath = if (Test-Path -LiteralPath $stressSummaryPath) { $stressSummaryPath } else { "" }
        SampleFaceReliabilityAverage = Get-JsonValue -Object $sampleSummary -Name "FaceReliabilityAverage"
        SampleFaceReliabilityMinimum = Get-JsonValue -Object $sampleSummary -Name "FaceReliabilityMinimum"
        SampleFaceReliabilityUsableRate = Get-JsonValue -Object $sampleSummary -Name "FaceReliabilityUsableRate"
        SampleFaceContinuityAverage = Get-JsonValue -Object $sampleSummary -Name "FaceContinuityAverage"
        SampleEyeUsableRate = Get-JsonValue -Object $sampleSummary -Name "LandmarkEyeUsableRate"
        SampleMouthUsableRate = Get-JsonValue -Object $sampleSummary -Name "LandmarkMouthUsableRate"
        SampleOverallQualityAverage = Get-JsonValue -Object $sampleSummary -Name "LandmarkAverageOverallQuality"
        SyntheticFaceReliabilityAverage = Get-JsonValue -Object $syntheticSummary -Name "FaceReliabilityAverage"
        SyntheticFaceReliabilityMinimum = Get-JsonValue -Object $syntheticSummary -Name "FaceReliabilityMinimum"
        SyntheticFaceReliabilityUsableRate = Get-JsonValue -Object $syntheticSummary -Name "FaceReliabilityUsableRate"
        SyntheticEyeUsableRate = Get-JsonValue -Object $syntheticSummary -Name "LandmarkEyeUsableRate"
        SyntheticMouthUsableRate = Get-JsonValue -Object $syntheticSummary -Name "LandmarkMouthUsableRate"
        LandmarkStressFrameCount = Get-JsonValue -Object $stressSummary -Name "FrameCount"
        LandmarkStressFaceReliabilityAverage = Get-JsonValue -Object $stressSummary -Name "AggregateAverageFaceReliability"
        LandmarkStressFaceReliabilityMinimum = Get-JsonValue -Object $stressSummary -Name "AggregateMinimumFaceReliability"
        LandmarkStressFaceReliabilityUsableSamples = Get-JsonValue -Object $stressSummary -Name "AggregateFaceReliabilityUsableSamples"
        LandmarkStressFaceContinuityAverage = Get-JsonValue -Object $stressSummary -Name "AggregateAverageFaceContinuity"
        LandmarkStressEyeReliabilityAverage = Get-JsonValue -Object $stressSummary -Name "AggregateAverageEyeReliability"
        LandmarkStressMouthReliabilityAverage = Get-JsonValue -Object $stressSummary -Name "AggregateAverageMouthReliability"
    }
    $results.Add([pscustomobject]$result)

    if ($exitCode -ne 0) {
        Write-Host "Iteration $iteration failed with exit code $exitCode."
        if ($StopOnFailure) {
            break
        }
    }
}

$endedAt = Get-Date
$summary = [ordered]@{
    StartedAt = $startedAt.ToUniversalTime().ToString("O")
    EndedAt = $endedAt.ToUniversalTime().ToString("O")
    DurationSeconds = [Math]::Round(($endedAt - $startedAt).TotalSeconds, 3)
    RequestedIterations = $Iterations
    CompletedIterations = $results.Count
    PassedIterations = @($results | Where-Object { $_.Passed }).Count
    FailedIterations = @($results | Where-Object { -not $_.Passed }).Count
    SampleMediaPath = Get-SamplePath
    EyeInset = $EyeInset
    OutputRoot = $OutputRoot
    Results = $results
}

$summaryPath = Join-Path $OutputRoot "vision_soak_summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host ""
Write-Host "Vision soak summary: $summaryPath"

if ($summary.FailedIterations -gt 0) {
    throw "Vision soak completed with $($summary.FailedIterations) failed iteration(s). See $summaryPath"
}
