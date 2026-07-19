param(
    [string[]]$SampleMediaPath = @(),
    [int]$SoakIterations = 3,
    [double]$SampleFramesPerSecond = 1,
    [string]$EyeInset = "none",
    [string]$OutputRoot = "",
    [switch]$SkipSoak,
    [switch]$SkipRealClipBatch,
    [switch]$FailOnWarnings,
    [switch]$FailOnQualityGates
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($SoakIterations -lt 1) {
    throw "SoakIterations must be at least 1."
}

if ($SampleFramesPerSecond -le 0) {
    throw "SampleFramesPerSecond must be greater than zero."
}

$startedAt = Get-Date
$runStamp = $startedAt.ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "output\vision-overnight-audit\$runStamp"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$soakScript = Join-Path $PSScriptRoot "RunAvatarBuilderVisionSoak.ps1"
$batchScript = Join-Path $PSScriptRoot "RunAvatarBuilderRealClipBatch.ps1"

function Resolve-SampleMediaPaths {
    param([string[]]$Paths)

    $normalized = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        if ($path.Contains(",") -and -not (Test-Path -LiteralPath $path)) {
            foreach ($part in $path.Split(",", [StringSplitOptions]::RemoveEmptyEntries)) {
                $normalized.Add($part.Trim().Trim('"'))
            }
        }
        else {
            $normalized.Add($path.Trim().Trim('"'))
        }
    }

    if ($normalized.Count -eq 0) {
        foreach ($defaultPath in @(
            "C:\Users\clsor\Videos\Insta360\20260715-195317.mp4",
            "C:\Users\clsor\Videos\Insta360\20260715-195342.mp4",
            "C:\Users\clsor\Videos\Insta360\20260715-195400.mp4")) {
            if (Test-Path -LiteralPath $defaultPath) {
                $normalized.Add($defaultPath)
            }
        }
    }

    $resolved = New-Object System.Collections.Generic.List[string]
    foreach ($path in $normalized) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Sample media path was not found: $path"
        }

        $resolved.Add((Resolve-Path -LiteralPath $path).Path)
    }

    return @($resolved.ToArray())
}

function Read-JsonObject {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Read-Number {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $number = 0.0
    if ([double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        return $number
    }

    return $null
}

function Format-Percent {
    param($Value)

    $number = Read-Number $Value
    if ($null -eq $number) {
        return "--"
    }

    return "{0:P0}" -f $number
}

function Format-Number {
    param($Value)

    $number = Read-Number $Value
    if ($null -eq $number) {
        return "--"
    }

    return $number.ToString("0.#", [Globalization.CultureInfo]::InvariantCulture)
}

function HtmlEncode {
    param([string]$Value)

    return [Net.WebUtility]::HtmlEncode($Value)
}

function Invoke-AuditStep {
    param(
        [string]$Name,
        [string]$LogPath,
        [string[]]$Arguments
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    $stepStartedAt = Get-Date
    Write-Host ""
    Write-Host "== $Name =="
    Write-Host "Log: $LogPath"
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output = @($_.Exception.Message)
        $exitCode = 1
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $output | Tee-Object -FilePath $LogPath
    $stepEndedAt = Get-Date
    [pscustomobject]@{
        Name = $Name
        StartedAt = $stepStartedAt.ToUniversalTime().ToString("O")
        EndedAt = $stepEndedAt.ToUniversalTime().ToString("O")
        DurationSeconds = [Math]::Round(($stepEndedAt - $stepStartedAt).TotalSeconds, 3)
        ExitCode = $exitCode
        Passed = $exitCode -eq 0
        LogPath = $LogPath
    }
}

function Add-QualityCheck {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [bool]$Passed,
        $Actual,
        [string]$Expected
    )

    $Checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Actual = $Actual
        Expected = $Expected
    })
}

function Write-HtmlReport {
    param(
        [string]$ReportPath,
        $Summary
    )

    $status = if ($Summary.Passed) { "Passed" } else { "Needs review" }
    $statusClass = if ($Summary.Passed) { "good" } else { "bad" }
    $html = New-Object System.Text.StringBuilder
    [void]$html.AppendLine("<!doctype html><html><head><meta charset=""utf-8""><title>Avatar Builder Overnight Vision Audit</title>")
    [void]$html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#091016;color:#e8f3ff;margin:24px}a{color:#8cc8ff}.panel{border:1px solid #2c4054;padding:16px;margin:16px 0;background:#0f1a23}table{border-collapse:collapse;width:100%;margin-top:10px}th,td{border-bottom:1px solid #263747;padding:8px;text-align:left}th{color:#bfe1ff}.good{color:#7ee6a1}.bad{color:#ff9b9b}.muted{color:#9fb2c1}</style></head><body>")
    [void]$html.AppendLine("<h1>Avatar Builder Overnight Vision Audit</h1>")
    [void]$html.AppendLine("<p class=""$statusClass""><strong>$status</strong></p>")
    [void]$html.AppendLine("<div class=""panel""><h2>Summary</h2><table><tbody>")
    [void]$html.AppendLine("<tr><th>Started</th><td>$(HtmlEncode $Summary.StartedAt)</td></tr>")
    [void]$html.AppendLine("<tr><th>Ended</th><td>$(HtmlEncode $Summary.EndedAt)</td></tr>")
    [void]$html.AppendLine("<tr><th>Sample clips</th><td>$($Summary.SampleMediaPath.Count)</td></tr>")
    [void]$html.AppendLine("<tr><th>Soak</th><td>$($Summary.Soak.PassedIterations)/$($Summary.Soak.CompletedIterations) iteration(s) passed</td></tr>")
    $averageCorpusReadiness = Format-Number $Summary.RealClipBatch.AverageCorpusReadinessPercent
    if ($averageCorpusReadiness -ne "--") {
        $averageCorpusReadiness = "$averageCorpusReadiness%"
    }

    $averageLearningStability = Format-Number $Summary.RealClipBatch.AverageLearningStabilityCoveragePercent
    if ($averageLearningStability -ne "--") {
        $averageLearningStability = "$averageLearningStability%"
    }

    $maximumNextSampleInfluence = Format-Number $Summary.RealClipBatch.MaximumNextSampleInfluencePercent
    if ($maximumNextSampleInfluence -ne "--") {
        $maximumNextSampleInfluence = "$maximumNextSampleInfluence%"
    }

    $averageIdentityCoverage = Format-Number $Summary.RealClipBatch.AverageIdentityCoveragePercent
    if ($averageIdentityCoverage -ne "--") {
        $averageIdentityCoverage = "$averageIdentityCoverage%"
    }

    $averageContourShapeCoverage = Format-Number $Summary.RealClipBatch.AverageContourShapeCoveragePercent
    if ($averageContourShapeCoverage -ne "--") {
        $averageContourShapeCoverage = "$averageContourShapeCoverage%"
    }

    $averageDirectFeatureTrust = Format-Number $Summary.RealClipBatch.AverageDirectFeatureMeasurementTrustPercent
    if ($averageDirectFeatureTrust -ne "--") {
        $averageDirectFeatureTrust = "$averageDirectFeatureTrust%"
    }

    $averageEyeInsetAgreementTrust = Format-Number $Summary.RealClipBatch.AverageEyeInsetFullFrameAgreementTrustPercent
    $eyeInsetAgreementSummary = if ($averageEyeInsetAgreementTrust -ne "--") { ", eye-inset agreement trust $averageEyeInsetAgreementTrust%" } else { "" }

    $averageCaptureQuality = Format-Number $Summary.RealClipBatch.AverageCaptureQualityScore
    if ($averageCaptureQuality -ne "--") {
        $averageCaptureQuality = "$averageCaptureQuality%"
    }

    [void]$html.AppendLine("<tr><th>Real clip batch</th><td>$($Summary.RealClipBatch.ClipCount) clip(s), $($Summary.RealClipBatch.TotalWarningCount) warning(s), average capture quality $averageCaptureQuality, avatar-grade rate $(Format-Percent $Summary.RealClipBatch.MinimumCaptureQualityAvatarGradeRate), average corpus readiness $averageCorpusReadiness, learning stability $averageLearningStability, max next-sample influence $maximumNextSampleInfluence, average identity coverage $averageIdentityCoverage, average contour shape coverage $averageContourShapeCoverage, average direct feature trust $averageDirectFeatureTrust$eyeInsetAgreementSummary, capture plan items $($Summary.RealClipBatch.TotalAvatarCapturePlanItemCount) across $($Summary.RealClipBatch.TotalAvatarCapturePlanTargetMinutes) min, tracking artifact rejects $($Summary.RealClipBatch.TotalTrackingArtifactRejectedSamples), subject mismatch rejects $($Summary.RealClipBatch.TotalSubjectMismatchRejectedSamples)</td></tr>")
    [void]$html.AppendLine("</tbody></table></div>")

    [void]$html.AppendLine("<div class=""panel""><h2>Quality Gates</h2><table><thead><tr><th>Status</th><th>Gate</th><th>Actual</th><th>Expected</th></tr></thead><tbody>")
    foreach ($check in $Summary.QualityChecks) {
        $class = if ($check.Passed) { "good" } else { "bad" }
        $label = if ($check.Passed) { "Pass" } else { "Review" }
        [void]$html.AppendLine("<tr><td class=""$class"">$label</td><td>$(HtmlEncode $check.Name)</td><td>$(HtmlEncode ([string]$check.Actual))</td><td>$(HtmlEncode $check.Expected)</td></tr>")
    }
    [void]$html.AppendLine("</tbody></table></div>")

    [void]$html.AppendLine("<div class=""panel""><h2>Real Clips</h2><table><thead><tr><th>Clip</th><th>Face</th><th>Eye usable</th><th>Mouth usable</th><th>Reliability</th><th>Continuity</th><th>Capture quality</th><th>Corpus</th><th>Identity</th><th>Capture plan</th><th>Warnings</th><th>Report</th></tr></thead><tbody>")
    foreach ($clip in $Summary.RealClipBatch.ClipResults) {
        $report = if ([string]::IsNullOrWhiteSpace($clip.ReportPath)) { "" } else { "<a href=""$(HtmlEncode $clip.ReportPath)"">open</a>" }
        $corpusReport = if ([string]::IsNullOrWhiteSpace($clip.CorpusReadinessHtmlPath)) { "" } else { "<br><a href=""$(HtmlEncode $clip.CorpusReadinessHtmlPath)"">readiness</a>" }
        $corpusReadiness = Format-Number $clip.CorpusReadinessPercent
        if ($corpusReadiness -ne "--") {
            $corpusReadiness = "$corpusReadiness%"
        }

        $identityCoverage = Format-Number $clip.PersonalModelIdentityCoveragePercent
        if ($identityCoverage -ne "--") {
            $identityCoverage = "$identityCoverage%"
        }

        $contourShapeCoverage = Format-Number $clip.PersonalModelContourShapeCoveragePercent
        if ($contourShapeCoverage -ne "--") {
            $contourShapeCoverage = "$contourShapeCoverage%"
        }

        $directFeatureTrust = Format-Number $clip.PersonalModelDirectFeatureMeasurementTrustPercent
        if ($directFeatureTrust -ne "--") {
            $directFeatureTrust = "$directFeatureTrust%"
        }

        $eyeInsetAgreementTrust = Format-Number $clip.EyeInsetFullFrameAgreementTrustPercent
        $eyeInsetAgreementDetail = if ($eyeInsetAgreementTrust -ne "--") { ", inset agreement $eyeInsetAgreementTrust%" } else { "" }

        $capturePlanReport = if ([string]::IsNullOrWhiteSpace($clip.AvatarCapturePlanHtmlPath)) { "" } else { "<br><a href=""$(HtmlEncode $clip.AvatarCapturePlanHtmlPath)"">plan</a>" }
        $capturePlanTitle = if ([string]::IsNullOrWhiteSpace($clip.AvatarCapturePlanFirstItemTitle)) { "none" } else { HtmlEncode $clip.AvatarCapturePlanFirstItemTitle }
        [void]$html.AppendLine("<tr><td>$(HtmlEncode $clip.Name)</td><td>$(Format-Percent $clip.FaceDetectionRate)</td><td>$(Format-Percent $clip.EyeUsableRate)</td><td>$(Format-Percent $clip.MouthUsableRate)</td><td>$(Format-Number $clip.FaceReliabilityAverage)</td><td>$(Format-Number $clip.FaceContinuityAverage)</td><td>$(Format-Number $clip.CaptureQualityAverageScore)%<br><span class=""muted"">collect $(Format-Percent $clip.CaptureQualityCanCollectRate), avatar $(Format-Percent $clip.CaptureQualityAvatarGradeRate)</span></td><td>$corpusReadiness<br><span class=""muted"">stability $(Format-Number $clip.LearningStabilityCoveragePercent)%, shape $contourShapeCoverage, trust $directFeatureTrust, influence $(Format-Number $clip.PersonalModelMaxNextSampleInfluencePercent)%</span><br><span class=""muted"">eye trust $(Format-Number $clip.PersonalModelEyeBehindGlassesTrustPercent)%, mouth/jaw $(Format-Number $clip.PersonalModelMouthJawTrustPercent)%$eyeInsetAgreementDetail, motion pairs $($clip.PersonalFaceMotionModelMotionPairs)</span>$corpusReport</td><td>$identityCoverage<br><span class=""muted"">signature $($clip.PersonalModelIdentitySignatureSamples), shape L/R eye $($clip.PersonalFaceCorpusLeftEyeShapeSamples)/$($clip.PersonalFaceCorpusRightEyeShapeSamples), lip $($clip.PersonalFaceCorpusInnerLipShapeSamples), jaw $($clip.PersonalFaceCorpusJawShapeSamples), mismatch $($clip.PersonalModelSubjectMismatchRejectedSamples)</span></td><td>$capturePlanTitle<br><span class=""muted"">$($clip.AvatarCapturePlanItemCount) item(s), $($clip.AvatarCapturePlanTargetMinutes) min</span>$capturePlanReport</td><td>$($clip.WarningCount)</td><td>$report</td></tr>")
    }
    [void]$html.AppendLine("</tbody></table></div>")

    [void]$html.AppendLine("<div class=""panel""><h2>Artifacts</h2><table><tbody>")
    [void]$html.AppendLine("<tr><th>Soak summary</th><td>$(HtmlEncode $Summary.SoakSummaryPath)</td></tr>")
    [void]$html.AppendLine("<tr><th>Real clip batch summary</th><td>$(HtmlEncode $Summary.RealClipBatchSummaryPath)</td></tr>")
    [void]$html.AppendLine("<tr><th>Real clip batch report</th><td>$(HtmlEncode $Summary.RealClipBatchReportPath)</td></tr>")
    [void]$html.AppendLine("</tbody></table></div>")
    [void]$html.AppendLine("</body></html>")
    $html.ToString() | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}

$resolvedSamplePaths = @(Resolve-SampleMediaPaths -Paths $SampleMediaPath)
if (-not $SkipSoak -and $resolvedSamplePaths.Count -eq 0) {
    throw "At least one sample clip is required for the soak verifier."
}

if (-not $SkipRealClipBatch -and $resolvedSamplePaths.Count -eq 0) {
    throw "At least one sample clip is required for the real clip batch."
}

$steps = New-Object System.Collections.Generic.List[object]
$soakOutput = Join-Path $OutputRoot "soak"
$batchOutput = Join-Path $OutputRoot "real-clips"
$soakSummaryPath = Join-Path $soakOutput "vision_soak_summary.json"
$batchSummaryPath = Join-Path $batchOutput "real_clip_batch_summary.json"
$batchReportPath = Join-Path $batchOutput "real_clip_batch_report.html"

if (-not $SkipSoak) {
    $soakArgs = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $soakScript,
        "-Iterations",
        $SoakIterations.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-SampleMediaPath",
        $resolvedSamplePaths[0],
        "-SampleFramesPerSecond",
        $SampleFramesPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-EyeInset",
        $EyeInset,
        "-OutputRoot",
        $soakOutput,
        "-StopOnFailure"
    )
    $steps.Add((Invoke-AuditStep -Name "Vision soak" -LogPath (Join-Path $OutputRoot "logs\vision_soak.log") -Arguments $soakArgs))
}

if (-not $SkipRealClipBatch) {
    $batchArgs = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $batchScript,
        "-SampleMediaPath",
        ($resolvedSamplePaths -join ","),
        "-SampleFramesPerSecond",
        $SampleFramesPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture),
        "-EyeInset",
        $EyeInset,
        "-OutputRoot",
        $batchOutput
    )
    if ($FailOnWarnings) {
        $batchArgs += "-FailOnWarnings"
    }
    if ($FailOnQualityGates) {
        $batchArgs += "-FailOnQualityGates"
    }

    $steps.Add((Invoke-AuditStep -Name "Real clip batch" -LogPath (Join-Path $OutputRoot "logs\real_clip_batch.log") -Arguments $batchArgs))
}

$soakSummary = Read-JsonObject -Path $soakSummaryPath
$batchSummary = Read-JsonObject -Path $batchSummaryPath
$checks = New-Object System.Collections.Generic.List[object]
if ($null -ne $soakSummary) {
    Add-QualityCheck -Checks $checks -Name "Soak iterations passed" -Passed ($soakSummary.FailedIterations -eq 0 -and $soakSummary.PassedIterations -eq $soakSummary.CompletedIterations) -Actual "$($soakSummary.PassedIterations)/$($soakSummary.CompletedIterations)" -Expected "all iterations pass"
}

if ($null -ne $batchSummary) {
    if ($null -ne $batchSummary.QualityChecks) {
        foreach ($batchCheck in @($batchSummary.QualityChecks)) {
            Add-QualityCheck -Checks $checks -Name "Real clip batch: $($batchCheck.Name)" -Passed ([bool]$batchCheck.Passed) -Actual ([string]$batchCheck.Actual) -Expected ([string]$batchCheck.Expected)
        }
    }

    Add-QualityCheck -Checks $checks -Name "Real clip batch quality gates" -Passed ([bool]$batchSummary.Passed) -Actual "$($batchSummary.FailedQualityCheckCount) failed" -Expected "0 failed"
    Add-QualityCheck -Checks $checks -Name "Real clip face detection" -Passed ($batchSummary.MinimumFaceDetectionRate -ge 0.90) -Actual (Format-Percent $batchSummary.MinimumFaceDetectionRate) -Expected "minimum >= 90%"
    Add-QualityCheck -Checks $checks -Name "Real clip eye usable rate" -Passed ($batchSummary.MinimumEyeUsableRate -ge 0.70) -Actual (Format-Percent $batchSummary.MinimumEyeUsableRate) -Expected "minimum >= 70%"
    Add-QualityCheck -Checks $checks -Name "Real clip mouth usable rate" -Passed ($batchSummary.MinimumMouthUsableRate -ge 0.70) -Actual (Format-Percent $batchSummary.MinimumMouthUsableRate) -Expected "minimum >= 70%"
    Add-QualityCheck -Checks $checks -Name "Real clip average face reliability" -Passed ($batchSummary.AverageFaceReliability -ge 75) -Actual (Format-Number $batchSummary.AverageFaceReliability) -Expected "average >= 75"
    Add-QualityCheck -Checks $checks -Name "Real clip warmed reliability usable rate" -Passed ($batchSummary.MinimumFaceReliabilityUsableRate -ge 0.70) -Actual (Format-Percent $batchSummary.MinimumFaceReliabilityUsableRate) -Expected "minimum >= 70%"
    Add-QualityCheck -Checks $checks -Name "Real clip corpus readiness recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageCorpusReadinessPercent)) -Actual (Format-Number $batchSummary.AverageCorpusReadinessPercent) -Expected "recorded"
    Add-QualityCheck -Checks $checks -Name "Real clip learning stability recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageLearningStabilityCoveragePercent) -and $null -ne (Read-Number $batchSummary.MaximumNextSampleInfluencePercent)) -Actual "stability $(Format-Number $batchSummary.AverageLearningStabilityCoveragePercent), influence $(Format-Number $batchSummary.MaximumNextSampleInfluencePercent)" -Expected "recorded"
    Add-QualityCheck -Checks $checks -Name "Real clip identity coverage recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageIdentityCoveragePercent)) -Actual (Format-Number $batchSummary.AverageIdentityCoveragePercent) -Expected "recorded"
    Add-QualityCheck -Checks $checks -Name "Real clip contour shape coverage recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageContourShapeCoveragePercent)) -Actual (Format-Number $batchSummary.AverageContourShapeCoveragePercent) -Expected "recorded"
    Add-QualityCheck -Checks $checks -Name "Real clip direct feature trust recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageDirectFeatureMeasurementTrustPercent)) -Actual (Format-Number $batchSummary.AverageDirectFeatureMeasurementTrustPercent) -Expected "recorded"
    if ($null -ne (Read-Number $batchSummary.AverageEyeInsetFullFrameAgreementTrustPercent)) {
        Add-QualityCheck -Checks $checks -Name "Real clip eye-inset agreement trust recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageEyeInsetFullFrameAgreementTrustPercent)) -Actual (Format-Number $batchSummary.AverageEyeInsetFullFrameAgreementTrustPercent) -Expected "recorded"
    }
    Add-QualityCheck -Checks $checks -Name "Real clip capture quality recorded" -Passed ($null -ne (Read-Number $batchSummary.AverageCaptureQualityScore)) -Actual (Format-Number $batchSummary.AverageCaptureQualityScore) -Expected "recorded for avatar-grade ranking"
    Add-QualityCheck -Checks $checks -Name "Real clip capture plans recorded" -Passed ($batchSummary.CapturePlanClipCount -eq $batchSummary.ClipCount -and $batchSummary.TotalAvatarCapturePlanItemCount -gt 0) -Actual "$($batchSummary.CapturePlanClipCount)/$($batchSummary.ClipCount) clip(s), $($batchSummary.TotalAvatarCapturePlanItemCount) item(s)" -Expected "one capture plan per clip"
    Add-QualityCheck -Checks $checks -Name "Real clip subject mismatch rejects" -Passed ($batchSummary.TotalSubjectMismatchRejectedSamples -eq 0) -Actual $batchSummary.TotalSubjectMismatchRejectedSamples -Expected "0 for same-subject test clips"
    $warningExpectation = "record only"
    if ($FailOnWarnings) {
        $warningExpectation = "0 warnings"
    }

    Add-QualityCheck -Checks $checks -Name "Real clip warnings" -Passed (-not $FailOnWarnings -or $batchSummary.TotalWarningCount -eq 0) -Actual $batchSummary.TotalWarningCount -Expected $warningExpectation
}

$endedAt = Get-Date
$passed = @($steps | Where-Object { -not $_.Passed }).Count -eq 0 -and @($checks | Where-Object { -not $_.Passed }).Count -eq 0
$summary = [pscustomobject]@{
    StartedAt = $startedAt.ToUniversalTime().ToString("O")
    EndedAt = $endedAt.ToUniversalTime().ToString("O")
    DurationSeconds = [Math]::Round(($endedAt - $startedAt).TotalSeconds, 3)
    Passed = $passed
    SampleMediaPath = $resolvedSamplePaths
    SoakIterations = if ($null -eq $soakSummary) { $null } else { $soakSummary.RequestedIterations }
    SampleFramesPerSecond = $SampleFramesPerSecond
    EyeInset = $EyeInset
    OutputRoot = $OutputRoot
    Steps = @($steps.ToArray())
    QualityChecks = @($checks.ToArray())
    SoakSummaryPath = if (Test-Path -LiteralPath $soakSummaryPath) { $soakSummaryPath } else { "" }
    RealClipBatchSummaryPath = if (Test-Path -LiteralPath $batchSummaryPath) { $batchSummaryPath } else { "" }
    RealClipBatchReportPath = if (Test-Path -LiteralPath $batchReportPath) { $batchReportPath } else { "" }
    Soak = if ($null -eq $soakSummary) { [pscustomobject]@{} } else { $soakSummary }
    RealClipBatch = if ($null -eq $batchSummary) { [pscustomobject]@{} } else { $batchSummary }
}

$summaryPath = Join-Path $OutputRoot "overnight_vision_audit_summary.json"
$reportPath = Join-Path $OutputRoot "overnight_vision_audit_report.html"
$summary | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-HtmlReport -ReportPath $reportPath -Summary $summary

Write-Host ""
Write-Host "Overnight vision audit summary: $summaryPath"
Write-Host "Overnight vision audit report:  $reportPath"

if (-not $summary.Passed) {
    throw "Overnight vision audit found issues. See $reportPath"
}
