param(
    [Parameter(Mandatory = $true)]
    [string[]]$SampleMediaPath,
    [double]$SampleFramesPerSecond = 2,
    [string]$EyeInset = "none",
    [string]$OutputRoot = "",
    [switch]$SkipBuild,
    [switch]$FailOnWarnings,
    [switch]$FailOnQualityGates
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($SampleMediaPath.Count -eq 0) {
    throw "Provide at least one sample media path."
}

$normalizedSamplePaths = New-Object System.Collections.Generic.List[string]
foreach ($path in $SampleMediaPath) {
    if ($path.Contains(",") -and -not (Test-Path -LiteralPath $path)) {
        foreach ($part in $path.Split(",", [StringSplitOptions]::RemoveEmptyEntries)) {
            $normalizedSamplePaths.Add($part.Trim().Trim('"'))
        }
    }
    else {
        $normalizedSamplePaths.Add($path.Trim().Trim('"'))
    }
}

$SampleMediaPath = @($normalizedSamplePaths)
if ($SampleMediaPath.Count -eq 0) {
    throw "Provide at least one sample media path."
}

if ($SampleFramesPerSecond -le 0) {
    throw "SampleFramesPerSecond must be greater than zero."
}

$startedAt = Get-Date
$runStamp = $startedAt.ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "output\vision-real-clips\$runStamp"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) "AvatarBuilderRealClipBatch\$runStamp"
$buildOutputRoot = Join-Path $buildRoot "bin\"
$evaluatorDll = Join-Path $buildOutputRoot "Debug\net10.0-windows\AvatarBuilderVisionEval.dll"

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

function ConvertTo-SafeName {
    param([string]$Value)

    $name = [IO.Path]::GetFileNameWithoutExtension($Value)
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = "clip"
    }

    foreach ($character in [IO.Path]::GetInvalidFileNameChars()) {
        $name = $name.Replace($character, "_")
    }

    return $name
}

function Get-UniqueClipOutputFolder {
    param(
        [string]$Root,
        [string]$BaseName,
        [int]$Index
    )

    $candidate = Join-Path $Root ("{0:D2}_{1}" -f $Index, $BaseName)
    if (-not (Test-Path -LiteralPath $candidate)) {
        return $candidate
    }

    for ($suffix = 2; $suffix -lt 1000; $suffix++) {
        $candidate = Join-Path $Root ("{0:D2}_{1}_{2}" -f $Index, $BaseName, $suffix)
        if (-not (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Could not find a unique output folder for $BaseName."
}

function Read-Number {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $number = 0.0
    if ([double]::TryParse($text, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        return $number
    }

    return $null
}

function Read-Integer {
    param($Value)

    $number = Read-Number $Value
    if ($null -eq $number) {
        return $null
    }

    return [int][Math]::Round($number)
}

function Read-Boolean {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }

    return [string]$Value -match "^(true|1|yes)$"
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
    param(
        $Value,
        [string]$Pattern = "0.###"
    )

    $number = Read-Number $Value
    if ($null -eq $number) {
        return "--"
    }

    return $number.ToString($Pattern, [Globalization.CultureInfo]::InvariantCulture)
}

function Test-Minimum {
    param(
        $Value,
        [double]$Minimum
    )

    $number = Read-Number $Value
    return $null -ne $number -and $number -ge $Minimum
}

function Test-Maximum {
    param(
        $Value,
        [double]$Maximum
    )

    $number = Read-Number $Value
    return $null -ne $number -and $number -le $Maximum
}

function Add-QualityCheck {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [bool]$Passed,
        [string]$Actual,
        [string]$Expected
    )

    $Checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Actual = $Actual
        Expected = $Expected
    })
}

function ConvertTo-RelativePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    $rootUri = [Uri](([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = [Uri]([IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}

function HtmlEncode {
    param([string]$Value)

    return [Net.WebUtility]::HtmlEncode($Value)
}

function Read-JsonObject {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-CapturePlanDigest {
    param(
        [string]$JsonPath,
        [string]$HtmlPath
    )

    $plan = Read-JsonObject -Path $JsonPath
    $items = if ($null -eq $plan) { @() } else { @($plan.Items) }
    $firstItem = $items | Sort-Object { Read-Integer $_.Priority }, { [string]$_.Category }, { [string]$_.Title } | Select-Object -First 1

    [pscustomobject]@{
        JsonPath = if (Test-Path -LiteralPath $JsonPath) { $JsonPath } else { "" }
        HtmlPath = if (Test-Path -LiteralPath $HtmlPath) { $HtmlPath } else { "" }
        CollectionDecision = if ($null -eq $plan) { "" } else { [string]$plan.CollectionDecision }
        CanCollectMeasurements = if ($null -eq $plan) { $false } else { Read-Boolean $plan.CanCollectMeasurements }
        ItemCount = $items.Count
        TotalTargetMinutes = if ($null -eq $plan) { $null } else { Read-Integer $plan.TotalTargetMinutes }
        EstimatedMeasurementBytes = if ($null -eq $plan) { $null } else { Read-Integer $plan.EstimatedMeasurementBytes }
        FirstItemTitle = if ($null -eq $firstItem) { "" } else { [string]$firstItem.Title }
        FirstItemCategory = if ($null -eq $firstItem) { "" } else { [string]$firstItem.Category }
        FirstItemPriority = if ($null -eq $firstItem) { $null } else { Read-Integer $firstItem.Priority }
        FirstItemInstructions = if ($null -eq $firstItem) { "" } else { [string]$firstItem.Instructions }
        FirstItemRelatedScoreName = if ($null -eq $firstItem) { "" } else { [string]$firstItem.RelatedScoreName }
        FirstItemRelatedScorePercent = if ($null -eq $firstItem) { $null } else { Read-Number $firstItem.RelatedScorePercent }
    }
}

function Get-OverlayPath {
    param(
        [string]$EvaluationFolder,
        $Record
    )

    $frameIndex = Read-Integer $Record.FrameIndex
    if ($null -eq $frameIndex) {
        return ""
    }

    $overlayPath = Join-Path $EvaluationFolder ("overlay_frames\frame_{0:D6}.png" -f $frameIndex)
    if (Test-Path -LiteralPath $overlayPath) {
        return $overlayPath
    }

    return ""
}

function New-ReviewFrame {
    param(
        [string]$Label,
        $Record,
        [string]$EvaluationFolder
    )

    if ($null -eq $Record) {
        return $null
    }

    $faceWidth = Read-Number $Record.FaceWidth
    $faceHeight = Read-Number $Record.FaceHeight
    $faceScale = if ($null -ne $faceWidth -and $null -ne $faceHeight) { $faceWidth * $faceHeight } else { $null }

    [pscustomobject]@{
        Label = $Label
        FrameIndex = Read-Integer $Record.FrameIndex
        TimestampSeconds = Read-Number $Record.TimestampSeconds
        OverallQuality = Read-Number $Record.OverallQuality
        EyeQuality = Read-Number $Record.EyeQuality
        MouthQuality = Read-Number $Record.MouthQuality
        CaptureQualityLabel = [string]$Record.CaptureQualityLabel
        CaptureQualityScore = Read-Number $Record.CaptureQualityScore
        CaptureQualityReason = [string]$Record.CaptureQualityReason
        FaceReliabilitySamples = Read-Integer $Record.FaceReliabilitySamples
        FaceReliability = Read-Number $Record.FaceReliability
        FaceContinuity = Read-Number $Record.FaceContinuity
        EyeReliability = Read-Number $Record.EyeReliability
        MouthReliability = Read-Number $Record.MouthReliability
        AverageEyeOpening = Read-Number $Record.AverageEyeOpening
        MouthOpening = Read-Number $Record.MouthOpening
        JawDroop = Read-Number $Record.JawDroop
        ARotationAroundXDegrees = Read-Number $Record.ARotationAroundXDegrees
        BRotationAroundYDegrees = Read-Number $Record.BRotationAroundYDegrees
        CRotationAroundZDegrees = Read-Number $Record.CRotationAroundZDegrees
        FaceLeft = Read-Number $Record.FaceLeft
        FaceTop = Read-Number $Record.FaceTop
        FaceWidth = $faceWidth
        FaceHeight = $faceHeight
        FaceScale = $faceScale
        IdentityMeasurementAvailable = Read-Boolean $Record.IdentityMeasurementAvailable
        FaceAspectRatio = Read-Number $Record.FaceAspectRatio
        EyeMidlineXToFaceWidth = Read-Number $Record.EyeMidlineXToFaceWidth
        MouthCenterXToFaceWidth = Read-Number $Record.MouthCenterXToFaceWidth
        EyeToMouthXOffsetToFaceWidth = Read-Number $Record.EyeToMouthXOffsetToFaceWidth
        InterEyeDistanceToFaceWidth = Read-Number $Record.InterEyeDistanceToFaceWidth
        MouthWidthToFaceWidth = Read-Number $Record.MouthWidthToFaceWidth
        EyeMidlineYToFaceHeight = Read-Number $Record.EyeMidlineYToFaceHeight
        MouthCenterYToFaceHeight = Read-Number $Record.MouthCenterYToFaceHeight
        EyeInsetRegion = [string]$Record.EyeInsetRegion
        EyeInsetStatus = [string]$Record.EyeInsetStatus
        EyeInsetHasMeasurement = Read-Boolean $Record.EyeInsetHasMeasurement
        EyeInsetAverageOpening = Read-Number $Record.EyeInsetAverageOpening
        EyeInsetConfidence = Read-Number $Record.EyeInsetConfidence
        EyeInsetImageQualityAvailable = Read-Boolean $Record.EyeInsetImageQualityAvailable
        EyeInsetGlare = Read-Number $Record.EyeInsetGlare
        EyeInsetContrast = Read-Number $Record.EyeInsetContrast
        EyeInsetSharpness = Read-Number $Record.EyeInsetSharpness
        EyeInsetDarkCoverage = Read-Number $Record.EyeInsetDarkCoverage
        EyeInsetRegionLeft = Read-Number $Record.EyeInsetRegionLeft
        EyeInsetRegionTop = Read-Number $Record.EyeInsetRegionTop
        EyeInsetRegionWidth = Read-Number $Record.EyeInsetRegionWidth
        EyeInsetRegionHeight = Read-Number $Record.EyeInsetRegionHeight
        EyeInsetCueStatus = [string]$Record.EyeInsetCueStatus
        EyeInsetCueHasMeasurement = Read-Boolean $Record.EyeInsetCueHasMeasurement
        EyeInsetCueBaselineReady = Read-Boolean $Record.EyeInsetCueBaselineReady
        EyeInsetCueBaselineSamples = Read-Integer $Record.EyeInsetCueBaselineSamples
        EyeInsetCueEligible = Read-Boolean $Record.EyeInsetCueEligible
        EyeInsetCueQuality = Read-Number $Record.EyeInsetCueQuality
        EyeInsetCueOpening = Read-Number $Record.EyeInsetCueOpening
        EyeInsetCueBaselineOpening = Read-Number $Record.EyeInsetCueBaselineOpening
        EyeInsetCueClosure = Read-Number $Record.EyeInsetCueClosure
        EyeInsetCueScore = Read-Number $Record.EyeInsetCueScore
        PossibleOneEyeArtifact = Read-Boolean $Record.PossibleOneEyeArtifact
        EyeArtifactSuppressed = Read-Boolean $Record.EyeArtifactSuppressed
        OverlayPath = Get-OverlayPath -EvaluationFolder $EvaluationFolder -Record $Record
    }
}

function Select-FirstSorted {
    param(
        [object[]]$Rows,
        [string]$Property,
        [switch]$Descending
    )

    $withValues = @($Rows | Where-Object { $null -ne (Read-Number $_.$Property) })
    if ($withValues.Count -eq 0) {
        return $null
    }

    if ($Descending) {
        return $withValues | Sort-Object { Read-Number $_.$Property } -Descending | Select-Object -First 1
    }

    return $withValues | Sort-Object { Read-Number $_.$Property } | Select-Object -First 1
}

function Select-FirstByValue {
    param(
        [object[]]$Rows,
        [scriptblock]$ValueSelector,
        [switch]$Descending
    )

    $withValues = @(
        $Rows | ForEach-Object {
            $row = $_
            $number = Read-Number (& $ValueSelector $row)
            if ($null -ne $number) {
                [pscustomobject]@{
                    Row = $row
                    Value = $number
                }
            }
        }
    )
    if ($withValues.Count -eq 0) {
        return $null
    }

    if ($Descending) {
        return ($withValues | Sort-Object Value -Descending | Select-Object -First 1).Row
    }

    return ($withValues | Sort-Object Value | Select-Object -First 1).Row
}

function Get-ReviewFrames {
    param(
        [string]$EvaluationFolder,
        [object[]]$Rows
    )

    $frames = New-Object System.Collections.Generic.List[object]
    $byFrame = @{}
    $warmedReliabilityRows = @($Rows | Where-Object { (Read-Integer $_.FaceReliabilitySamples) -ge 3 })
    $candidates = @(
        [pscustomobject]@{ Label = "lowest overall quality"; Record = (Select-FirstSorted $Rows "OverallQuality") },
        [pscustomobject]@{ Label = "lowest capture quality"; Record = (Select-FirstSorted $Rows "CaptureQualityScore") },
        [pscustomobject]@{ Label = "lowest warmed face reliability"; Record = (Select-FirstSorted $warmedReliabilityRows "FaceReliability") },
        [pscustomobject]@{ Label = "weakest eye quality"; Record = (Select-FirstSorted $Rows "EyeQuality") },
        [pscustomobject]@{ Label = "weakest mouth quality"; Record = (Select-FirstSorted $Rows "MouthQuality") },
        [pscustomobject]@{ Label = "minimum eye opening"; Record = (Select-FirstSorted $Rows "AverageEyeOpening") },
        [pscustomobject]@{ Label = "maximum mouth opening"; Record = (Select-FirstSorted $Rows "MouthOpening" -Descending) },
        [pscustomobject]@{ Label = "maximum jaw droop"; Record = (Select-FirstSorted $Rows "JawDroop" -Descending) },
        [pscustomobject]@{ Label = "leftmost face lock"; Record = (Select-FirstSorted $Rows "FaceLeft") },
        [pscustomobject]@{ Label = "rightmost face lock"; Record = (Select-FirstByValue $Rows { param($row) (Read-Number $row.FaceLeft) + (Read-Number $row.FaceWidth) } -Descending) },
        [pscustomobject]@{ Label = "highest face lock"; Record = (Select-FirstSorted $Rows "FaceTop") },
        [pscustomobject]@{ Label = "lowest face lock"; Record = (Select-FirstByValue $Rows { param($row) (Read-Number $row.FaceTop) + (Read-Number $row.FaceHeight) } -Descending) },
        [pscustomobject]@{ Label = "Farthest Z/apparent scale"; Record = (Select-FirstByValue $Rows {
            param($row)

            $faceWidth = Read-Number $row.FaceWidth
            $faceHeight = Read-Number $row.FaceHeight
            if ($null -ne $faceWidth -and $null -ne $faceHeight) {
                $faceWidth * $faceHeight
            }
        }) },
        [pscustomobject]@{ Label = "Closest Z/apparent scale"; Record = (Select-FirstByValue $Rows {
            param($row)

            $faceWidth = Read-Number $row.FaceWidth
            $faceHeight = Read-Number $row.FaceHeight
            if ($null -ne $faceWidth -and $null -ne $faceHeight) {
                $faceWidth * $faceHeight
            }
        } -Descending) },
        [pscustomobject]@{ Label = "Lowest A rotation around X"; Record = (Select-FirstSorted $Rows "ARotationAroundXDegrees") },
        [pscustomobject]@{ Label = "Highest A rotation around X"; Record = (Select-FirstSorted $Rows "ARotationAroundXDegrees" -Descending) },
        [pscustomobject]@{ Label = "Lowest B rotation around Y"; Record = (Select-FirstSorted $Rows "BRotationAroundYDegrees") },
        [pscustomobject]@{ Label = "Highest B rotation around Y"; Record = (Select-FirstSorted $Rows "BRotationAroundYDegrees" -Descending) },
        [pscustomobject]@{ Label = "Lowest C rotation around Z"; Record = (Select-FirstSorted $Rows "CRotationAroundZDegrees") },
        [pscustomobject]@{ Label = "Highest C rotation around Z"; Record = (Select-FirstSorted $Rows "CRotationAroundZDegrees" -Descending) },
        [pscustomobject]@{ Label = "leftmost eye anchor"; Record = (Select-FirstSorted $Rows "EyeMidlineXToFaceWidth") },
        [pscustomobject]@{ Label = "rightmost eye anchor"; Record = (Select-FirstSorted $Rows "EyeMidlineXToFaceWidth" -Descending) },
        [pscustomobject]@{ Label = "leftmost mouth anchor"; Record = (Select-FirstSorted $Rows "MouthCenterXToFaceWidth") },
        [pscustomobject]@{ Label = "rightmost mouth anchor"; Record = (Select-FirstSorted $Rows "MouthCenterXToFaceWidth" -Descending) },
        [pscustomobject]@{ Label = "largest eye-mouth horizontal offset"; Record = (Select-FirstSorted $Rows "EyeToMouthXOffsetToFaceWidth" -Descending) },
        [pscustomobject]@{ Label = "largest eye-inset/full-frame opening gap"; Record = (Select-FirstByValue $Rows {
            param($row)

            $eyeInsetOpening = Read-Number $row.EyeInsetAverageOpening
            $fullFrameOpening = Read-Number $row.AverageEyeOpening
            if ($null -ne $eyeInsetOpening -and $null -ne $fullFrameOpening) {
                [Math]::Abs($eyeInsetOpening - $fullFrameOpening)
            }
        } -Descending) },
        [pscustomobject]@{ Label = "minimum eye-inset opening"; Record = (Select-FirstSorted $Rows "EyeInsetAverageOpening") },
        [pscustomobject]@{ Label = "strongest eye-inset closure"; Record = (Select-FirstSorted $Rows "EyeInsetCueClosure" -Descending) },
        [pscustomobject]@{ Label = "strongest eye-inset score"; Record = (Select-FirstSorted $Rows "EyeInsetCueScore" -Descending) },
        [pscustomobject]@{ Label = "weakest eye-inset confidence"; Record = (Select-FirstSorted $Rows "EyeInsetConfidence") },
        [pscustomobject]@{ Label = "highest eye-inset glare"; Record = (Select-FirstSorted $Rows "EyeInsetGlare" -Descending) },
        [pscustomobject]@{ Label = "weakest eye-inset contrast"; Record = (Select-FirstSorted $Rows "EyeInsetContrast") },
        [pscustomobject]@{ Label = "weakest eye-inset sharpness"; Record = (Select-FirstSorted $Rows "EyeInsetSharpness") },
        [pscustomobject]@{ Label = "one-eye artifact flag"; Record = (@($Rows | Where-Object { Read-Boolean $_.PossibleOneEyeArtifact } | Select-Object -First 1)) }
    )

    foreach ($candidate in $candidates) {
        $reviewFrame = New-ReviewFrame $candidate.Label $candidate.Record $EvaluationFolder
        if ($null -eq $reviewFrame -or $null -eq $reviewFrame.FrameIndex) {
            continue
        }

        $key = [string]$reviewFrame.FrameIndex
        if ($byFrame.ContainsKey($key)) {
            $existing = $byFrame[$key]
            $existing.Label = "$($existing.Label); $($reviewFrame.Label)"
        }
        else {
            $byFrame[$key] = $reviewFrame
            $frames.Add($reviewFrame)
        }
    }

    return $frames
}

function Get-BatchMetricSummary {
    param([object[]]$ClipResults)

    $minimumFaceRate = @($ClipResults | ForEach-Object { Read-Number $_.FaceDetectionRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumEyeUsableRate = @($ClipResults | ForEach-Object { Read-Number $_.EyeUsableRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumMouthUsableRate = @($ClipResults | ForEach-Object { Read-Number $_.MouthUsableRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumOverallQuality = @($ClipResults | ForEach-Object { Read-Number $_.MinimumOverallQuality } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumFaceReliability = @($ClipResults | ForEach-Object { Read-Number $_.FaceReliabilityMinimum } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumFaceReliabilityUsableRate = @($ClipResults | ForEach-Object { Read-Number $_.FaceReliabilityUsableRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumEyeInsetMeasurementRate = @($ClipResults | ForEach-Object { Read-Number $_.EyeInsetMeasurementRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumEyeInsetImageQualityRate = @($ClipResults | ForEach-Object { Read-Number $_.EyeInsetImageQualityRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumEyeInsetAgreementTrust = @($ClipResults | ForEach-Object { Read-Number $_.EyeInsetFullFrameAgreementTrustPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $eyeInsetMaximumOpenings = @($ClipResults | ForEach-Object { Read-Number $_.EyeInsetMaximumOpening } | Where-Object { $null -ne $_ })
    $eyeInsetDetectedClipCount = @($ClipResults | Where-Object { (Read-Number $_.EyeInsetMeasurementRate) -gt 0 }).Count
    $minimumIdentityMeasurementRate = @($ClipResults | ForEach-Object { Read-Number $_.IdentityMeasurementRate } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumIdentitySessionHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusIdentitySessionHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumDataAuditHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusDataAuditHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumFeatureAnchoringHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusFeatureAnchoringHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumPoseExplainedFeatureMotionHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumEyeApertureReliabilityHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusEyeApertureReliabilityHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumMouthVerticalAnchorHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusMouthVerticalAnchorHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumCorpusReadiness = @($ClipResults | ForEach-Object { Read-Number $_.CorpusReadinessPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumLearningStability = @($ClipResults | ForEach-Object { Read-Number $_.LearningStabilityCoveragePercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumLearningAnchor = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelLearningAnchorPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumIdentityCoverage = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelIdentityCoveragePercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumContourShapeCoverage = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelContourShapeCoveragePercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumContourDepthProfileHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelContourDepthProfileHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumSurfaceShapeCoverage = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelSurfaceShapeCoveragePercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumSurfaceDepthProfileHealth = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelSurfaceDepthProfileHealthPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumXYZABCCoverage = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelXYZABCCoveragePercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumDirectFeatureTrust = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelDirectFeatureMeasurementTrustPercent } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $minimumCaptureQualityScore = @($ClipResults | ForEach-Object { Read-Number $_.CaptureQualityMinimumScore } | Where-Object { $null -ne $_ } | Sort-Object | Select-Object -First 1)
    $faceCenterXRanges = @($ClipResults | ForEach-Object { Read-Number $_.FaceCenterXRange } | Where-Object { $null -ne $_ })
    $faceCenterYRanges = @($ClipResults | ForEach-Object { Read-Number $_.FaceCenterYRange } | Where-Object { $null -ne $_ })
    $faceHeightRanges = @($ClipResults | ForEach-Object { Read-Number $_.FaceHeightRange } | Where-Object { $null -ne $_ })
    $faceAnchorRanges = @(
        $ClipResults | ForEach-Object {
            $eyeX = Read-Number $_.EyeMidlineXToFaceWidthRange
            $mouthX = Read-Number $_.MouthCenterXToFaceWidthRange
            $eyeMouthX = Read-Number $_.EyeToMouthXOffsetToFaceWidthRange
            @($eyeX, $mouthX, $eyeMouthX) | Where-Object { $null -ne $_ } | Sort-Object -Descending | Select-Object -First 1
        } | Where-Object { $null -ne $_ }
    )
    $eyeHorizontalRanges = @($ClipResults | ForEach-Object { Read-Number $_.EyeMidlineXToFaceWidthRange } | Where-Object { $null -ne $_ })
    $mouthHorizontalRanges = @($ClipResults | ForEach-Object { Read-Number $_.MouthCenterXToFaceWidthRange } | Where-Object { $null -ne $_ })
    $eyeMouthHorizontalRanges = @($ClipResults | ForEach-Object { Read-Number $_.EyeToMouthXOffsetToFaceWidthRange } | Where-Object { $null -ne $_ })
    $interEyeRanges = @($ClipResults | ForEach-Object { Read-Number $_.InterEyeDistanceToFaceWidthRange } | Where-Object { $null -ne $_ })
    $mouthWidthRanges = @($ClipResults | ForEach-Object { Read-Number $_.MouthWidthToFaceWidthRange } | Where-Object { $null -ne $_ })
    $dataAuditHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusDataAuditHealthPercent } | Where-Object { $null -ne $_ })
    $identitySessionHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusIdentitySessionHealthPercent } | Where-Object { $null -ne $_ })
    $recentIdentityConfidenceAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusAverageRecentIdentityConfidencePercent } | Where-Object { $null -ne $_ })
    $recentIdentityOutlierRates = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusRecentIdentityOutlierFrameRate } | Where-Object { $null -ne $_ })
    $totalRecentIdentityMeasurements = @($ClipResults | ForEach-Object { Read-Integer $_.PersonalFaceCorpusRecentIdentityMeasurementSamples } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $featureAnchoringHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusFeatureAnchoringHealthPercent } | Where-Object { $null -ne $_ })
    $poseExplainedFeatureMotionHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent } | Where-Object { $null -ne $_ })
    $eyeApertureReliabilityHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusEyeApertureReliabilityHealthPercent } | Where-Object { $null -ne $_ })
    $mouthVerticalAnchorHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusMouthVerticalAnchorHealthPercent } | Where-Object { $null -ne $_ })
    $faceReliabilityAverages = @($ClipResults | ForEach-Object { Read-Number $_.FaceReliabilityAverage } | Where-Object { $null -ne $_ })
    $faceContinuityAverages = @($ClipResults | ForEach-Object { Read-Number $_.FaceContinuityAverage } | Where-Object { $null -ne $_ })
    $eyeInsetAgreementTrustAverages = @($ClipResults | ForEach-Object { Read-Number $_.EyeInsetFullFrameAgreementTrustPercent } | Where-Object { $null -ne $_ })
    $corpusReadinessAverages = @($ClipResults | ForEach-Object { Read-Number $_.CorpusReadinessPercent } | Where-Object { $null -ne $_ })
    $learningStabilityAverages = @($ClipResults | ForEach-Object { Read-Number $_.LearningStabilityCoveragePercent } | Where-Object { $null -ne $_ })
    $nextSampleInfluences = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelMaxNextSampleInfluencePercent } | Where-Object { $null -ne $_ })
    $eventLikeSampleInfluences = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelMaxEventLikeNextSampleInfluencePercent } | Where-Object { $null -ne $_ })
    $identityCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelIdentityCoveragePercent } | Where-Object { $null -ne $_ })
    $contourShapeCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelContourShapeCoveragePercent } | Where-Object { $null -ne $_ })
    $contourDepthProfileHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelContourDepthProfileHealthPercent } | Where-Object { $null -ne $_ })
    $surfaceShapeCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelSurfaceShapeCoveragePercent } | Where-Object { $null -ne $_ })
    $surfaceDepthProfileHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelSurfaceDepthProfileHealthPercent } | Where-Object { $null -ne $_ })
    $xyzabcCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelXYZABCCoveragePercent } | Where-Object { $null -ne $_ })
    $zDistanceCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelZDistanceCoveragePercent } | Where-Object { $null -ne $_ })
    $zDistanceEvidenceHealthAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelZDistanceEvidenceHealthPercent } | Where-Object { $null -ne $_ })
    $zConfidenceAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalFaceCorpusAverageZConfidencePercent } | Where-Object { $null -ne $_ })
    $totalZEstimateSamples = @($ClipResults | ForEach-Object { Read-Integer $_.PersonalModelZEstimateSamples } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $aRotationCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelARotationAroundXCoveragePercent } | Where-Object { $null -ne $_ })
    $bRotationCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelBRotationAroundYCoveragePercent } | Where-Object { $null -ne $_ })
    $cRotationCoverageAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelCRotationAroundZCoveragePercent } | Where-Object { $null -ne $_ })
    $eyeTrustAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelEyeBehindGlassesTrustPercent } | Where-Object { $null -ne $_ })
    $mouthTrustAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelMouthJawTrustPercent } | Where-Object { $null -ne $_ })
    $directFeatureTrustAverages = @($ClipResults | ForEach-Object { Read-Number $_.PersonalModelDirectFeatureMeasurementTrustPercent } | Where-Object { $null -ne $_ })
    $captureQualityAverages = @($ClipResults | ForEach-Object { Read-Number $_.CaptureQualityAverageScore } | Where-Object { $null -ne $_ })
    $captureQualityCanCollectRates = @($ClipResults | ForEach-Object { Read-Number $_.CaptureQualityCanCollectRate } | Where-Object { $null -ne $_ })
    $captureQualityAvatarGradeRates = @($ClipResults | ForEach-Object { Read-Number $_.CaptureQualityAvatarGradeRate } | Where-Object { $null -ne $_ })
    $totalWarningCount = @($ClipResults | ForEach-Object { Read-Integer $_.WarningCount } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalOneEyeArtifacts = @($ClipResults | ForEach-Object { Read-Integer $_.PossibleOneEyeArtifactFrames } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalSuppressedEyeArtifacts = @($ClipResults | ForEach-Object { Read-Integer $_.EyeArtifactSuppressedFrames } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalTrackingArtifactRejected = @($ClipResults | ForEach-Object { Read-Integer $_.PersonalModelTrackingArtifactRejectedSamples } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalSubjectMismatchRejected = @($ClipResults | ForEach-Object { Read-Integer $_.PersonalModelSubjectMismatchRejectedSamples } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalIdentitySignatureSamples = @($ClipResults | ForEach-Object { Read-Integer $_.PersonalModelIdentitySignatureSamples } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalCapturePlanItems = @($ClipResults | ForEach-Object { Read-Integer $_.AvatarCapturePlanItemCount } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalCapturePlanMinutes = @($ClipResults | ForEach-Object { Read-Integer $_.AvatarCapturePlanTargetMinutes } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $totalCapturePlanBytes = @($ClipResults | ForEach-Object { Read-Integer $_.AvatarCapturePlanEstimatedMeasurementBytes } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    $capturePlanClipCount = @($ClipResults | Where-Object { (Read-Integer $_.AvatarCapturePlanItemCount) -gt 0 }).Count

    [pscustomobject]@{
        MinimumFaceDetectionRate = if ($minimumFaceRate.Count -gt 0) { $minimumFaceRate[0] } else { $null }
        MinimumEyeUsableRate = if ($minimumEyeUsableRate.Count -gt 0) { $minimumEyeUsableRate[0] } else { $null }
        MinimumMouthUsableRate = if ($minimumMouthUsableRate.Count -gt 0) { $minimumMouthUsableRate[0] } else { $null }
        MinimumOverallQuality = if ($minimumOverallQuality.Count -gt 0) { $minimumOverallQuality[0] } else { $null }
        MinimumFaceReliability = if ($minimumFaceReliability.Count -gt 0) { $minimumFaceReliability[0] } else { $null }
        MinimumFaceReliabilityUsableRate = if ($minimumFaceReliabilityUsableRate.Count -gt 0) { $minimumFaceReliabilityUsableRate[0] } else { $null }
        MinimumEyeInsetMeasurementRate = if ($minimumEyeInsetMeasurementRate.Count -gt 0) { $minimumEyeInsetMeasurementRate[0] } else { $null }
        MinimumEyeInsetImageQualityRate = if ($minimumEyeInsetImageQualityRate.Count -gt 0) { $minimumEyeInsetImageQualityRate[0] } else { $null }
        MinimumEyeInsetFullFrameAgreementTrustPercent = if ($minimumEyeInsetAgreementTrust.Count -gt 0) { $minimumEyeInsetAgreementTrust[0] } else { $null }
        MaximumEyeInsetOpening = if ($eyeInsetMaximumOpenings.Count -gt 0) { ($eyeInsetMaximumOpenings | Measure-Object -Maximum).Maximum } else { $null }
        EyeInsetDetectedClipCount = $eyeInsetDetectedClipCount
        MinimumIdentityMeasurementRate = if ($minimumIdentityMeasurementRate.Count -gt 0) { $minimumIdentityMeasurementRate[0] } else { $null }
        MinimumIdentitySessionHealthPercent = if ($minimumIdentitySessionHealth.Count -gt 0) { $minimumIdentitySessionHealth[0] } else { $null }
        MinimumDataAuditHealthPercent = if ($minimumDataAuditHealth.Count -gt 0) { $minimumDataAuditHealth[0] } else { $null }
        MinimumFeatureAnchoringHealthPercent = if ($minimumFeatureAnchoringHealth.Count -gt 0) { $minimumFeatureAnchoringHealth[0] } else { $null }
        MinimumPoseExplainedFeatureMotionHealthPercent = if ($minimumPoseExplainedFeatureMotionHealth.Count -gt 0) { $minimumPoseExplainedFeatureMotionHealth[0] } else { $null }
        MinimumEyeApertureReliabilityHealthPercent = if ($minimumEyeApertureReliabilityHealth.Count -gt 0) { $minimumEyeApertureReliabilityHealth[0] } else { $null }
        MinimumMouthVerticalAnchorHealthPercent = if ($minimumMouthVerticalAnchorHealth.Count -gt 0) { $minimumMouthVerticalAnchorHealth[0] } else { $null }
        MinimumCorpusReadinessPercent = if ($minimumCorpusReadiness.Count -gt 0) { $minimumCorpusReadiness[0] } else { $null }
        MinimumLearningStabilityCoveragePercent = if ($minimumLearningStability.Count -gt 0) { $minimumLearningStability[0] } else { $null }
        MinimumLearningAnchorPercent = if ($minimumLearningAnchor.Count -gt 0) { $minimumLearningAnchor[0] } else { $null }
        MinimumIdentityCoveragePercent = if ($minimumIdentityCoverage.Count -gt 0) { $minimumIdentityCoverage[0] } else { $null }
        MinimumContourShapeCoveragePercent = if ($minimumContourShapeCoverage.Count -gt 0) { $minimumContourShapeCoverage[0] } else { $null }
        MinimumContourDepthProfileHealthPercent = if ($minimumContourDepthProfileHealth.Count -gt 0) { $minimumContourDepthProfileHealth[0] } else { $null }
        MinimumSurfaceShapeCoveragePercent = if ($minimumSurfaceShapeCoverage.Count -gt 0) { $minimumSurfaceShapeCoverage[0] } else { $null }
        MinimumSurfaceDepthProfileHealthPercent = if ($minimumSurfaceDepthProfileHealth.Count -gt 0) { $minimumSurfaceDepthProfileHealth[0] } else { $null }
        MinimumXYZABCCoveragePercent = if ($minimumXYZABCCoverage.Count -gt 0) { $minimumXYZABCCoverage[0] } else { $null }
        MinimumDirectFeatureMeasurementTrustPercent = if ($minimumDirectFeatureTrust.Count -gt 0) { $minimumDirectFeatureTrust[0] } else { $null }
        MinimumCaptureQualityScore = if ($minimumCaptureQualityScore.Count -gt 0) { $minimumCaptureQualityScore[0] } else { $null }
        MaximumFaceCenterXRange = if ($faceCenterXRanges.Count -gt 0) { ($faceCenterXRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumFaceCenterYRange = if ($faceCenterYRanges.Count -gt 0) { ($faceCenterYRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumFaceHeightRange = if ($faceHeightRanges.Count -gt 0) { ($faceHeightRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumFeatureAnchorXRange = if ($faceAnchorRanges.Count -gt 0) { ($faceAnchorRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumEyeMidlineXToFaceWidthRange = if ($eyeHorizontalRanges.Count -gt 0) { ($eyeHorizontalRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumMouthCenterXToFaceWidthRange = if ($mouthHorizontalRanges.Count -gt 0) { ($mouthHorizontalRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumEyeToMouthXOffsetToFaceWidthRange = if ($eyeMouthHorizontalRanges.Count -gt 0) { ($eyeMouthHorizontalRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumInterEyeDistanceToFaceWidthRange = if ($interEyeRanges.Count -gt 0) { ($interEyeRanges | Measure-Object -Maximum).Maximum } else { $null }
        MaximumMouthWidthToFaceWidthRange = if ($mouthWidthRanges.Count -gt 0) { ($mouthWidthRanges | Measure-Object -Maximum).Maximum } else { $null }
        DynamicFaceHorizontalCoverageClipCount = @($ClipResults | Where-Object { (Read-Number $_.FaceCenterXRange) -ge 0.08 }).Count
        DynamicFaceVerticalCoverageClipCount = @($ClipResults | Where-Object { (Read-Number $_.FaceCenterYRange) -ge 0.05 }).Count
        DynamicFaceScaleCoverageClipCount = @($ClipResults | Where-Object { (Read-Number $_.FaceHeightRange) -ge 0.08 }).Count
        AverageFaceReliability = if ($faceReliabilityAverages.Count -gt 0) { ($faceReliabilityAverages | Measure-Object -Average).Average } else { $null }
        AverageFaceContinuity = if ($faceContinuityAverages.Count -gt 0) { ($faceContinuityAverages | Measure-Object -Average).Average } else { $null }
        AverageDataAuditHealthPercent = if ($dataAuditHealthAverages.Count -gt 0) { ($dataAuditHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageIdentitySessionHealthPercent = if ($identitySessionHealthAverages.Count -gt 0) { ($identitySessionHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageRecentIdentityConfidencePercent = if ($recentIdentityConfidenceAverages.Count -gt 0) { ($recentIdentityConfidenceAverages | Measure-Object -Average).Average } else { $null }
        MaximumRecentIdentityOutlierFrameRate = if ($recentIdentityOutlierRates.Count -gt 0) { ($recentIdentityOutlierRates | Measure-Object -Maximum).Maximum } else { $null }
        TotalRecentIdentityMeasurementSamples = if ($null -ne $totalRecentIdentityMeasurements) { [int]$totalRecentIdentityMeasurements } else { 0 }
        AverageFeatureAnchoringHealthPercent = if ($featureAnchoringHealthAverages.Count -gt 0) { ($featureAnchoringHealthAverages | Measure-Object -Average).Average } else { $null }
        AveragePoseExplainedFeatureMotionHealthPercent = if ($poseExplainedFeatureMotionHealthAverages.Count -gt 0) { ($poseExplainedFeatureMotionHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageEyeApertureReliabilityHealthPercent = if ($eyeApertureReliabilityHealthAverages.Count -gt 0) { ($eyeApertureReliabilityHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageMouthVerticalAnchorHealthPercent = if ($mouthVerticalAnchorHealthAverages.Count -gt 0) { ($mouthVerticalAnchorHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageEyeInsetFullFrameAgreementTrustPercent = if ($eyeInsetAgreementTrustAverages.Count -gt 0) { ($eyeInsetAgreementTrustAverages | Measure-Object -Average).Average } else { $null }
        AverageCorpusReadinessPercent = if ($corpusReadinessAverages.Count -gt 0) { ($corpusReadinessAverages | Measure-Object -Average).Average } else { $null }
        AverageLearningStabilityCoveragePercent = if ($learningStabilityAverages.Count -gt 0) { ($learningStabilityAverages | Measure-Object -Average).Average } else { $null }
        MaximumNextSampleInfluencePercent = if ($nextSampleInfluences.Count -gt 0) { ($nextSampleInfluences | Measure-Object -Maximum).Maximum } else { $null }
        MaximumEventLikeNextSampleInfluencePercent = if ($eventLikeSampleInfluences.Count -gt 0) { ($eventLikeSampleInfluences | Measure-Object -Maximum).Maximum } else { $null }
        AverageIdentityCoveragePercent = if ($identityCoverageAverages.Count -gt 0) { ($identityCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageContourShapeCoveragePercent = if ($contourShapeCoverageAverages.Count -gt 0) { ($contourShapeCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageContourDepthProfileHealthPercent = if ($contourDepthProfileHealthAverages.Count -gt 0) { ($contourDepthProfileHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageSurfaceShapeCoveragePercent = if ($surfaceShapeCoverageAverages.Count -gt 0) { ($surfaceShapeCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageSurfaceDepthProfileHealthPercent = if ($surfaceDepthProfileHealthAverages.Count -gt 0) { ($surfaceDepthProfileHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageXYZABCCoveragePercent = if ($xyzabcCoverageAverages.Count -gt 0) { ($xyzabcCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageZDistanceCoveragePercent = if ($zDistanceCoverageAverages.Count -gt 0) { ($zDistanceCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageZDistanceEvidenceHealthPercent = if ($zDistanceEvidenceHealthAverages.Count -gt 0) { ($zDistanceEvidenceHealthAverages | Measure-Object -Average).Average } else { $null }
        AverageZConfidencePercent = if ($zConfidenceAverages.Count -gt 0) { ($zConfidenceAverages | Measure-Object -Average).Average } else { $null }
        TotalZEstimateSamples = if ($null -ne $totalZEstimateSamples) { [int]$totalZEstimateSamples } else { 0 }
        AverageARotationAroundXCoveragePercent = if ($aRotationCoverageAverages.Count -gt 0) { ($aRotationCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageBRotationAroundYCoveragePercent = if ($bRotationCoverageAverages.Count -gt 0) { ($bRotationCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageCRotationAroundZCoveragePercent = if ($cRotationCoverageAverages.Count -gt 0) { ($cRotationCoverageAverages | Measure-Object -Average).Average } else { $null }
        AverageEyeBehindGlassesTrustPercent = if ($eyeTrustAverages.Count -gt 0) { ($eyeTrustAverages | Measure-Object -Average).Average } else { $null }
        AverageMouthJawTrustPercent = if ($mouthTrustAverages.Count -gt 0) { ($mouthTrustAverages | Measure-Object -Average).Average } else { $null }
        AverageDirectFeatureMeasurementTrustPercent = if ($directFeatureTrustAverages.Count -gt 0) { ($directFeatureTrustAverages | Measure-Object -Average).Average } else { $null }
        AverageCaptureQualityScore = if ($captureQualityAverages.Count -gt 0) { ($captureQualityAverages | Measure-Object -Average).Average } else { $null }
        MinimumCaptureQualityCanCollectRate = if ($captureQualityCanCollectRates.Count -gt 0) { ($captureQualityCanCollectRates | Sort-Object | Select-Object -First 1) } else { $null }
        MinimumCaptureQualityAvatarGradeRate = if ($captureQualityAvatarGradeRates.Count -gt 0) { ($captureQualityAvatarGradeRates | Sort-Object | Select-Object -First 1) } else { $null }
        TotalWarningCount = if ($null -ne $totalWarningCount) { [int]$totalWarningCount } else { 0 }
        TotalOneEyeArtifactFrames = if ($null -ne $totalOneEyeArtifacts) { [int]$totalOneEyeArtifacts } else { 0 }
        TotalEyeArtifactSuppressedFrames = if ($null -ne $totalSuppressedEyeArtifacts) { [int]$totalSuppressedEyeArtifacts } else { 0 }
        TotalTrackingArtifactRejectedSamples = if ($null -ne $totalTrackingArtifactRejected) { [int]$totalTrackingArtifactRejected } else { 0 }
        TotalSubjectMismatchRejectedSamples = if ($null -ne $totalSubjectMismatchRejected) { [int]$totalSubjectMismatchRejected } else { 0 }
        TotalIdentitySignatureSamples = if ($null -ne $totalIdentitySignatureSamples) { [int]$totalIdentitySignatureSamples } else { 0 }
        CapturePlanClipCount = $capturePlanClipCount
        TotalAvatarCapturePlanItemCount = if ($null -ne $totalCapturePlanItems) { [int]$totalCapturePlanItems } else { 0 }
        TotalAvatarCapturePlanTargetMinutes = if ($null -ne $totalCapturePlanMinutes) { [int]$totalCapturePlanMinutes } else { 0 }
        TotalAvatarCapturePlanEstimatedMeasurementBytes = if ($null -ne $totalCapturePlanBytes) { [int64]$totalCapturePlanBytes } else { 0L }
    }
}

function Get-Warnings {
    param($Summary)

    $warnings = New-Object System.Collections.Generic.List[string]
    if ((Read-Number $Summary.FaceDetectionRate) -lt 0.90) {
        $warnings.Add("Face lock below 90%.")
    }

    if ((Read-Number $Summary.LandmarkEyeMeasurementRate) -lt 0.85) {
        $warnings.Add("Eye measurement rate below 85%.")
    }

    if ((Read-Number $Summary.LandmarkMouthMeasurementRate) -lt 0.85) {
        $warnings.Add("Mouth measurement rate below 85%.")
    }

    if ((Read-Number $Summary.LandmarkEyeUsableRate) -lt 0.75) {
        $warnings.Add("Usable eye rate below 75%.")
    }

    if ((Read-Number $Summary.LandmarkMouthUsableRate) -lt 0.75) {
        $warnings.Add("Usable mouth rate below 75%.")
    }

    if ((Read-Number $Summary.LandmarkAverageOverallQuality) -lt 60) {
        $warnings.Add("Average overall landmark quality below 60%.")
    }

    if ((Read-Number $Summary.FaceReliabilityUsableRate) -lt 0.70) {
        $warnings.Add("Face reliability usable rate below 70%.")
    }

    if ((Read-Number $Summary.FaceReliabilityAverage) -lt 70) {
        $warnings.Add("Average face reliability below 70%.")
    }

    if ((Read-Number $Summary.FaceContinuityAverage) -lt 70) {
        $warnings.Add("Average face continuity below 70%.")
    }

    if ((Read-Number $Summary.IdentityMeasurementRate) -lt 0.85) {
        $warnings.Add("Identity/face-local anchor measurement rate below 85%.")
    }

    $anchorRangeValues = @(
        @(
            (Read-Number $Summary.EyeMidlineXToFaceWidthRange)
            (Read-Number $Summary.MouthCenterXToFaceWidthRange)
            (Read-Number $Summary.EyeToMouthXOffsetToFaceWidthRange)
        ) | Where-Object { $null -ne $_ }
    )
    $anchorRange = if ($anchorRangeValues.Count -gt 0) { ($anchorRangeValues | Measure-Object -Maximum).Maximum } else { $null }
    $featureAnchoringHealth = Read-Number $Summary.PersonalFaceCorpusFeatureAnchoringHealthPercent
    $hasAnchorFinding = @(@($Summary.PersonalFaceCorpusDataAuditFindings) -match "face-local feature anchors")
    if ($null -ne $anchorRange -and $anchorRange -ge 0.16 -and (($null -ne $featureAnchoringHealth -and $featureAnchoringHealth -lt 60) -or $hasAnchorFinding.Count -gt 0)) {
        $warnings.Add("Face-local eye/mouth anchors drifted more than expected; review overlays for features sliding on the head.")
    }

    if ((Read-Number $Summary.PersonalFaceCorpusFeatureAnchoringHealthPercent) -lt 60) {
        $warnings.Add("Feature anchoring health below 60%; avatar learning should treat this clip as review-needed.")
    }

    if ((Read-Number $Summary.PersonalFaceCorpusEyeApertureReliabilityHealthPercent) -lt 70) {
        $warnings.Add("Eye aperture reliability below 70%; review glasses glare, one-eye artifacts, and reconstructed eye evidence.")
    }

    if ((Read-Number $Summary.PersonalFaceCorpusDataAuditHealthPercent) -lt 70) {
        $warnings.Add("Data-audit health below 70%.")
    }

    if ($null -eq (Read-Number $Summary.CaptureQualityAverageScore)) {
        $warnings.Add("Capture quality score was not recorded.")
    }
    elseif ((Read-Number $Summary.CaptureQualityAverageScore) -lt 62) {
        $warnings.Add("Average capture quality below measurement-collection target.")
    }

    if ($null -ne (Read-Number $Summary.CaptureQualityAvatarGradeRate) -and (Read-Number $Summary.CaptureQualityAvatarGradeRate) -le 0) {
        $warnings.Add("No avatar-grade capture-quality frames in this clip.")
    }

    if ((Read-Number $Summary.LandmarkPossibleOneEyeArtifactFrames) -gt 0) {
        $warnings.Add("One-eye artifact frames need review.")
    }

    if ((Read-Number $Summary.LandmarkEyeArtifactSuppressedFrames) -gt 0) {
        $warnings.Add("Eye artifact suppression was used; review dashed amber eye overlays.")
    }

    return $warnings
}

function Write-HtmlReport {
    param(
        [string]$ReportPath,
        [object[]]$ClipResults,
        [object]$BatchSummary
    )

    $html = New-Object System.Text.StringBuilder
    [void]$html.AppendLine("<!doctype html>")
    [void]$html.AppendLine("<html><head><meta charset=""utf-8""><title>Avatar Builder Real Clip Batch</title>")
    [void]$html.AppendLine("<style>")
    [void]$html.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;background:#0b1117;color:#e7eef7;margin:24px;}a{color:#8ac7ff}.clip{border:1px solid #294055;padding:16px;margin:18px 0;background:#101a23}.warn{color:#ffd18a}.ok{color:#8ee0a1}table{border-collapse:collapse;width:100%;margin:10px 0}th,td{border-bottom:1px solid #263948;padding:6px 8px;text-align:left;vertical-align:top}img{max-width:360px;border:1px solid #294055;margin:6px 10px 12px 0}.frames{display:flex;flex-wrap:wrap}.frame{max-width:380px}.small{color:#aebed0;font-size:12px}")
    [void]$html.AppendLine("</style></head><body>")
    [void]$html.AppendLine("<h1>Avatar Builder Real Clip Batch</h1>")
    [void]$html.AppendLine("<p class=""small"">Started $(HtmlEncode $BatchSummary.StartedAt). This report is for data-gathering and clinician review only; it is not diagnostic.</p>")
    $eyeInsetEnabled = -not [string]::IsNullOrWhiteSpace([string]$BatchSummary.EyeInset) -and -not ([string]$BatchSummary.EyeInset).Equals("none", [StringComparison]::OrdinalIgnoreCase)
    $eyeInsetSummary = if ($eyeInsetEnabled) { ", eye-inset detected clips $($BatchSummary.EyeInsetDetectedClipCount)/$($BatchSummary.ClipCount), eye-inset measurement $(Format-Percent $BatchSummary.MinimumEyeInsetMeasurementRate), eye-inset diagnostics $(Format-Percent $BatchSummary.MinimumEyeInsetImageQualityRate), max eye-inset opening $(Format-Number $BatchSummary.MaximumEyeInsetOpening ""0.###""), eye-inset agreement trust $(Format-Number $BatchSummary.AverageEyeInsetFullFrameAgreementTrustPercent ""0.0"")%" } else { "" }
    [void]$html.AppendLine("<p>Across $($BatchSummary.ClipCount) clip(s): minimum face lock $(Format-Percent $BatchSummary.MinimumFaceDetectionRate), minimum usable eye rate $(Format-Percent $BatchSummary.MinimumEyeUsableRate), minimum usable mouth rate $(Format-Percent $BatchSummary.MinimumMouthUsableRate), average face reliability $(Format-Number $BatchSummary.AverageFaceReliability ""0.0"")%, minimum reliable-lock rate $(Format-Percent $BatchSummary.MinimumFaceReliabilityUsableRate), dynamic face ranges x=$(Format-Number $BatchSummary.MaximumFaceCenterXRange ""0.###""), y=$(Format-Number $BatchSummary.MaximumFaceCenterYRange ""0.###""), scale=$(Format-Number $BatchSummary.MaximumFaceHeightRange ""0.###""), feature anchor x drift $(Format-Number $BatchSummary.MaximumFeatureAnchorXRange ""0.###""), feature anchoring health $(Format-Number $BatchSummary.AverageFeatureAnchoringHealthPercent ""0.0"")%, pose-explained feature health $(Format-Number $BatchSummary.AveragePoseExplainedFeatureMotionHealthPercent ""0.0"")%, mouth anchor health $(Format-Number $BatchSummary.AverageMouthVerticalAnchorHealthPercent ""0.0"")%, data-audit health $(Format-Number $BatchSummary.AverageDataAuditHealthPercent ""0.0"")%, identity-session health $(Format-Number $BatchSummary.AverageIdentitySessionHealthPercent ""0.0"")%, identity confidence $(Format-Number $BatchSummary.AverageRecentIdentityConfidencePercent ""0.0"")%, identity outlier max $(Format-Percent $BatchSummary.MaximumRecentIdentityOutlierFrameRate), minimum frame quality $(Format-Number $BatchSummary.MinimumOverallQuality ""0.0"")%, average capture quality $(Format-Number $BatchSummary.AverageCaptureQualityScore ""0.0"")%, avatar-grade rate $(Format-Percent $BatchSummary.MinimumCaptureQualityAvatarGradeRate), average learning-data readiness $(Format-Number $BatchSummary.AverageCorpusReadinessPercent ""0.0"")%, average learning stability $(Format-Number $BatchSummary.AverageLearningStabilityCoveragePercent ""0.0"")%, average XYZABC coverage $(Format-Number $BatchSummary.AverageXYZABCCoveragePercent ""0.0"")%, Z evidence $(Format-Number $BatchSummary.AverageZDistanceEvidenceHealthPercent ""0.0"")%, Z confidence $(Format-Number $BatchSummary.AverageZConfidencePercent ""0.0"")% from $($BatchSummary.TotalZEstimateSamples) sample(s), max next-sample influence $(Format-Number $BatchSummary.MaximumNextSampleInfluencePercent ""0.0"")%, average identity coverage $(Format-Number $BatchSummary.AverageIdentityCoveragePercent ""0.0"")%, average contour shape coverage $(Format-Number $BatchSummary.AverageContourShapeCoveragePercent ""0.0"")%, contour Z profile $(Format-Number $BatchSummary.AverageContourDepthProfileHealthPercent ""0.0"")%, average surface shape coverage $(Format-Number $BatchSummary.AverageSurfaceShapeCoveragePercent ""0.0"")%, surface Z profile $(Format-Number $BatchSummary.AverageSurfaceDepthProfileHealthPercent ""0.0"")%, average direct feature trust $(Format-Number $BatchSummary.AverageDirectFeatureMeasurementTrustPercent ""0.0"")%, tracking artifact rejects $($BatchSummary.TotalTrackingArtifactRejectedSamples), subject mismatch rejects $($BatchSummary.TotalSubjectMismatchRejectedSamples), capture plan items $($BatchSummary.TotalAvatarCapturePlanItemCount) across $($BatchSummary.TotalAvatarCapturePlanTargetMinutes) min, warning count $($BatchSummary.TotalWarningCount)$eyeInsetSummary.</p>")
    $batchStatusClass = if ($BatchSummary.Passed) { "ok" } else { "warn" }
    $batchStatus = if ($BatchSummary.Passed) { "Passed" } else { "Needs review" }
    [void]$html.AppendLine("<h2 class=""$batchStatusClass"">Quality Gates: $batchStatus</h2>")
    [void]$html.AppendLine("<table><tr><th>Gate</th><th>Status</th><th>Actual</th><th>Expected</th></tr>")
    foreach ($check in @($BatchSummary.QualityChecks)) {
        $class = if ($check.Passed) { "ok" } else { "warn" }
        $label = if ($check.Passed) { "Pass" } else { "Review" }
        [void]$html.AppendLine("<tr><td>$(HtmlEncode $check.Name)</td><td class=""$class"">$label</td><td>$(HtmlEncode $check.Actual)</td><td>$(HtmlEncode $check.Expected)</td></tr>")
    }

    [void]$html.AppendLine("</table>")
    [void]$html.AppendLine("<table><tr><th>Clip</th><th>Face</th><th>Reliability</th><th>Eye</th><th>Mouth</th><th>Quality</th><th>Capture quality</th><th>Learning data</th><th>Identity</th><th>Capture plan</th><th>Artifacts</th><th>Report</th></tr>")

    foreach ($clip in $ClipResults) {
        $warningClass = if ($clip.WarningCount -gt 0) { "warn" } else { "ok" }
        [void]$html.AppendLine("<tr>")
        [void]$html.AppendLine("<td>$(HtmlEncode $clip.Name)</td>")
        [void]$html.AppendLine("<td>$(Format-Percent $clip.FaceDetectionRate)<br><span class=""small"">x range $(Format-Number $clip.FaceCenterXRange), y range $(Format-Number $clip.FaceCenterYRange), Z scale $(Format-Number $clip.FaceHeightRange)</span><br><span class=""small"">A range $(Format-Number $clip.ARotationAroundXRange ""0.0"") deg, B range $(Format-Number $clip.BRotationAroundYRange ""0.0"") deg, C range $(Format-Number $clip.CRotationAroundZRange ""0.0"") deg</span><br><span class=""small"">anchor x $(Format-Number $clip.FeatureAnchorXRange), eye x $(Format-Number $clip.EyeMidlineXToFaceWidthRange), mouth x $(Format-Number $clip.MouthCenterXToFaceWidthRange), eye-mouth x $(Format-Number $clip.EyeToMouthXOffsetToFaceWidthRange)</span></td>")
        [void]$html.AppendLine("<td>avg $(Format-Number $clip.FaceReliabilityAverage ""0.0"")%<br>min $(Format-Number $clip.FaceReliabilityMinimum ""0.0"")%<br>usable $(Format-Percent $clip.FaceReliabilityUsableRate)<br><span class=""small"">continuity $(Format-Number $clip.FaceContinuityAverage ""0.0"")%</span></td>")
        $eyeInsetClipSummary = if ($eyeInsetEnabled) { "<br><span class=""small"">inset $(HtmlEncode $clip.EyeInsetDominantRegion), measure $(Format-Percent $clip.EyeInsetMeasurementRate), diagnostics $(Format-Percent $clip.EyeInsetImageQualityRate), agreement $(Format-Number $clip.EyeInsetFullFrameAgreementTrustPercent ""0.0"")%, min open $(Format-Number $clip.EyeInsetMinimumOpening), max open $(Format-Number $clip.EyeInsetMaximumOpening), closure $(Format-Number $clip.EyeInsetCueMaximumClosure), score $(Format-Number $clip.EyeInsetCueMaximumScore ""0.0"")</span>" } else { "" }
        [void]$html.AppendLine("<td>measure $(Format-Percent $clip.EyeMeasurementRate)<br>usable $(Format-Percent $clip.EyeUsableRate)<br>min open $(Format-Number $clip.MinimumEyeOpening)$eyeInsetClipSummary</td>")
        [void]$html.AppendLine("<td>measure $(Format-Percent $clip.MouthMeasurementRate)<br>usable $(Format-Percent $clip.MouthUsableRate)<br>max open $(Format-Number $clip.MaximumMouthOpening)</td>")
        [void]$html.AppendLine("<td>avg $(Format-Number $clip.AverageOverallQuality ""0.0"")%<br>min $(Format-Number $clip.MinimumOverallQuality ""0.0"")%</td>")
        [void]$html.AppendLine("<td>avg $(Format-Number $clip.CaptureQualityAverageScore ""0.0"")%<br>min $(Format-Number $clip.CaptureQualityMinimumScore ""0.0"")%<br><span class=""small"">collect $(Format-Percent $clip.CaptureQualityCanCollectRate), avatar $(Format-Percent $clip.CaptureQualityAvatarGradeRate)</span></td>")
        $relativeCorpus = ConvertTo-RelativePath -Root (Split-Path -Parent $ReportPath) -Path $clip.CorpusReadinessHtmlPath
        $corpusLink = if ([string]::IsNullOrWhiteSpace($relativeCorpus)) { "" } else { "<br><a href=""$(HtmlEncode $relativeCorpus)"">learning-data health</a>" }
        [void]$html.AppendLine("<td>ready $(Format-Number $clip.CorpusReadinessPercent ""0.0"")%<br><span class=""small"">data audit $(Format-Number $clip.PersonalFaceCorpusDataAuditHealthPercent ""0.0"")%, feature anchor $(Format-Number $clip.PersonalFaceCorpusFeatureAnchoringHealthPercent ""0.0"")%, pose-explained $(Format-Number $clip.PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent ""0.0"")%, eye aperture $(Format-Number $clip.PersonalFaceCorpusEyeApertureReliabilityHealthPercent ""0.0"")%, mouth anchor $(Format-Number $clip.PersonalFaceCorpusMouthVerticalAnchorHealthPercent ""0.0"")%</span><br><span class=""small"">stability $(Format-Number $clip.LearningStabilityCoveragePercent ""0.0"")%, XYZABC $(Format-Number $clip.PersonalModelXYZABCCoveragePercent ""0.0"")% (Z $(Format-Number $clip.PersonalModelZDistanceCoveragePercent ""0.0"")%, A $(Format-Number $clip.PersonalModelARotationAroundXCoveragePercent ""0.0"")%, B $(Format-Number $clip.PersonalModelBRotationAroundYCoveragePercent ""0.0"")%, C $(Format-Number $clip.PersonalModelCRotationAroundZCoveragePercent ""0.0"")%)</span><br><span class=""small"">Z evidence $(Format-Number $clip.PersonalModelZDistanceEvidenceHealthPercent ""0.0"")%, Z confidence $(Format-Number $clip.PersonalModelAverageZConfidencePercent ""0.0"")%, Z samples $($clip.PersonalModelZEstimateSamples), apparent-only $(Format-Percent $clip.PersonalFaceCorpusZApparentOnlyRate)</span><br><span class=""small"">contour $(Format-Number $clip.PersonalModelContourShapeCoveragePercent ""0.0"")% / Z $(Format-Number $clip.PersonalModelContourDepthProfileHealthPercent ""0.0"")%, surface $(Format-Number $clip.PersonalModelSurfaceShapeCoveragePercent ""0.0"")% / Z $(Format-Number $clip.PersonalModelSurfaceDepthProfileHealthPercent ""0.0"")%, trust $(Format-Number $clip.PersonalModelDirectFeatureMeasurementTrustPercent ""0.0"")%, anchor $(Format-Number $clip.PersonalModelLearningAnchorPercent ""0.0"")%, next sample $(Format-Number $clip.PersonalModelMaxNextSampleInfluencePercent ""0.0"")%</span><br><span class=""small"">eye trust $(Format-Number $clip.PersonalModelEyeBehindGlassesTrustPercent ""0.0"")%, mouth/jaw $(Format-Number $clip.PersonalModelMouthJawTrustPercent ""0.0"")%, motion pairs $($clip.PersonalFaceMotionModelMotionPairs)</span>$corpusLink</td>")
        [void]$html.AppendLine("<td>coverage $(Format-Number $clip.PersonalModelIdentityCoveragePercent ""0.0"")%<br><span class=""small"">session $(Format-Number $clip.PersonalFaceCorpusIdentitySessionHealthPercent ""0.0"")% $(HtmlEncode $clip.PersonalFaceCorpusIdentitySessionAuditStage), comparable $($clip.PersonalFaceCorpusRecentIdentityMeasurementSamples), confidence $(Format-Number $clip.PersonalFaceCorpusAverageRecentIdentityConfidencePercent ""0.0"")%, outliers $(Format-Percent $clip.PersonalFaceCorpusRecentIdentityOutlierFrameRate)</span><br><span class=""small"">$(HtmlEncode $clip.PersonalFaceCorpusIdentitySessionAuditStatus)</span><br><span class=""small"">measure $(Format-Percent $clip.IdentityMeasurementRate), signature $($clip.PersonalModelIdentitySignatureSamples), shape L/R eye $($clip.PersonalFaceCorpusLeftEyeShapeSamples)/$($clip.PersonalFaceCorpusRightEyeShapeSamples), lip $($clip.PersonalFaceCorpusInnerLipShapeSamples), jaw $($clip.PersonalFaceCorpusJawShapeSamples)</span><br><span class=""small"">surface brow $($clip.PersonalFaceCorpusLeftBrowShapeSamples)/$($clip.PersonalFaceCorpusRightBrowShapeSamples), nose $($clip.PersonalFaceCorpusNoseBridgeShapeSamples)/$($clip.PersonalFaceCorpusNoseBaseShapeSamples), cheek $($clip.PersonalFaceCorpusLeftCheekSurfaceSamples)/$($clip.PersonalFaceCorpusRightCheekSurfaceSamples), forehead $($clip.PersonalFaceCorpusForeheadSurfaceSamples), artifact rejects $($clip.PersonalModelTrackingArtifactRejectedSamples), mismatch rejects $($clip.PersonalModelSubjectMismatchRejectedSamples)</span></td>")
        $relativeCapturePlan = ConvertTo-RelativePath -Root (Split-Path -Parent $ReportPath) -Path $clip.AvatarCapturePlanHtmlPath
        $capturePlanLink = if ([string]::IsNullOrWhiteSpace($relativeCapturePlan)) { "" } else { "<br><a href=""$(HtmlEncode $relativeCapturePlan)"">plan</a>" }
        [void]$html.AppendLine("<td>$(HtmlEncode $clip.AvatarCapturePlanFirstItemTitle)<br><span class=""small"">$(HtmlEncode $clip.AvatarCapturePlanFirstItemCategory); $($clip.AvatarCapturePlanItemCount) item(s), $($clip.AvatarCapturePlanTargetMinutes) min</span>$capturePlanLink</td>")
        [void]$html.AppendLine("<td class=""$warningClass"">warnings $($clip.WarningCount)<br>one-eye $($clip.PossibleOneEyeArtifactFrames)<br>suppressed $($clip.EyeArtifactSuppressedFrames)</td>")
        $relativeReport = ConvertTo-RelativePath -Root (Split-Path -Parent $ReportPath) -Path $clip.ReportPath
        [void]$html.AppendLine("<td><a href=""$(HtmlEncode $relativeReport)"">clip report</a></td>")
        [void]$html.AppendLine("</tr>")
    }

    [void]$html.AppendLine("</table>")
    foreach ($clip in $ClipResults) {
        [void]$html.AppendLine("<section class=""clip"">")
        [void]$html.AppendLine("<h2>$(HtmlEncode $clip.Name)</h2>")
        if ($clip.WarningCount -eq 0) {
            [void]$html.AppendLine("<p class=""ok"">No batch warning thresholds tripped.</p>")
        }
        else {
            [void]$html.AppendLine("<ul class=""warn"">")
            foreach ($warning in $clip.Warnings) {
                [void]$html.AppendLine("<li>$(HtmlEncode $warning)</li>")
            }

            [void]$html.AppendLine("</ul>")
        }

        if ($clip.CorpusReadinessNextCaptureSuggestions.Count -gt 0) {
            [void]$html.AppendLine("<h3>Learning-data suggestions</h3>")
            [void]$html.AppendLine("<ul>")
            foreach ($suggestion in $clip.CorpusReadinessNextCaptureSuggestions) {
                [void]$html.AppendLine("<li>$(HtmlEncode $suggestion)</li>")
            }

            [void]$html.AppendLine("</ul>")
        }

        if ($clip.PersonalFaceCorpusDataAuditFindings.Count -gt 0) {
            [void]$html.AppendLine("<h3>Data audit findings</h3>")
            [void]$html.AppendLine("<ul>")
            foreach ($finding in $clip.PersonalFaceCorpusDataAuditFindings) {
                [void]$html.AppendLine("<li>$(HtmlEncode $finding)</li>")
            }

            [void]$html.AppendLine("</ul>")
        }

        if (-not [string]::IsNullOrWhiteSpace($clip.AvatarCapturePlanFirstItemTitle)) {
            [void]$html.AppendLine("<h3>Next capture plan item</h3>")
            [void]$html.AppendLine("<p><strong>$(HtmlEncode $clip.AvatarCapturePlanFirstItemTitle)</strong> <span class=""small"">$(HtmlEncode $clip.AvatarCapturePlanFirstItemCategory), priority $($clip.AvatarCapturePlanFirstItemPriority), $(Format-Number $clip.AvatarCapturePlanFirstItemRelatedScorePercent ""0.0"")% $(HtmlEncode $clip.AvatarCapturePlanFirstItemRelatedScoreName)</span></p>")
            [void]$html.AppendLine("<p>$(HtmlEncode $clip.AvatarCapturePlanFirstItemInstructions)</p>")
        }

        [void]$html.AppendLine("<div class=""frames"">")
        foreach ($frame in $clip.ReviewFrames) {
            $overlayRelative = ConvertTo-RelativePath -Root (Split-Path -Parent $ReportPath) -Path $frame.OverlayPath
            [void]$html.AppendLine("<div class=""frame"">")
            [void]$html.AppendLine("<h3>$(HtmlEncode $frame.Label)</h3>")
            [void]$html.AppendLine("<p class=""small"">frame $($frame.FrameIndex), t=$(Format-Number $frame.TimestampSeconds ""0.00"")s, q=$(Format-Number $frame.OverallQuality ""0.0"")%, capture $(HtmlEncode $frame.CaptureQualityLabel) $(Format-Number $frame.CaptureQualityScore ""0.0"")%, reliability $(Format-Number $frame.FaceReliability ""0.0"")% over $($frame.FaceReliabilitySamples) samples, continuity $(Format-Number $frame.FaceContinuity ""0.0"")%, face box l=$(Format-Number $frame.FaceLeft ""0.###""), t=$(Format-Number $frame.FaceTop ""0.###""), w=$(Format-Number $frame.FaceWidth ""0.###""), h=$(Format-Number $frame.FaceHeight ""0.###""), Z scale $(Format-Number $frame.FaceScale ""0.###""), A $(Format-Number $frame.ARotationAroundXDegrees ""0.0"") deg, B $(Format-Number $frame.BRotationAroundYDegrees ""0.0"") deg, C $(Format-Number $frame.CRotationAroundZDegrees ""0.0"") deg, eye q=$(Format-Number $frame.EyeQuality ""0.0"")%, mouth q=$(Format-Number $frame.MouthQuality ""0.0"")%, full-frame eye open $(Format-Number $frame.AverageEyeOpening), mouth open $(Format-Number $frame.MouthOpening), jaw $(Format-Number $frame.JawDroop)</p>")
            [void]$html.AppendLine("<p class=""small"">face-local anchors: identity measurement $($frame.IdentityMeasurementAvailable), aspect $(Format-Number $frame.FaceAspectRatio), eye x $(Format-Number $frame.EyeMidlineXToFaceWidth), mouth x $(Format-Number $frame.MouthCenterXToFaceWidth), eye-mouth x $(Format-Number $frame.EyeToMouthXOffsetToFaceWidth), eye spacing $(Format-Number $frame.InterEyeDistanceToFaceWidth), mouth width $(Format-Number $frame.MouthWidthToFaceWidth), eye y $(Format-Number $frame.EyeMidlineYToFaceHeight), mouth y $(Format-Number $frame.MouthCenterYToFaceHeight)</p>")
            if (-not [string]::IsNullOrWhiteSpace($frame.CaptureQualityReason)) {
                [void]$html.AppendLine("<p class=""small"">capture quality: $(HtmlEncode $frame.CaptureQualityReason)</p>")
            }
            $hasEyeInsetDetail = $frame.EyeInsetHasMeasurement -or $frame.EyeInsetCueHasMeasurement -or -not [string]::IsNullOrWhiteSpace($frame.EyeInsetStatus) -or -not [string]::IsNullOrWhiteSpace($frame.EyeInsetRegion)
            if ($hasEyeInsetDetail) {
                [void]$html.AppendLine("<p class=""small"">eye inset $(HtmlEncode $frame.EyeInsetRegion): $(HtmlEncode $frame.EyeInsetStatus); open $(Format-Number $frame.EyeInsetAverageOpening), confidence $(Format-Number $frame.EyeInsetConfidence ""0.0"")%, glare $(Format-Number $frame.EyeInsetGlare ""0.0"")%, contrast $(Format-Number $frame.EyeInsetContrast ""0.0"")%, sharpness $(Format-Number $frame.EyeInsetSharpness ""0.0"")%, dark $(Format-Number $frame.EyeInsetDarkCoverage ""0.0"")%; cue $(HtmlEncode $frame.EyeInsetCueStatus), closure $(Format-Number $frame.EyeInsetCueClosure), score $(Format-Number $frame.EyeInsetCueScore ""0.0""), baseline ready $($frame.EyeInsetCueBaselineReady), baseline samples $($frame.EyeInsetCueBaselineSamples)</p>")
                [void]$html.AppendLine("<p class=""small"">eye inset region: l=$(Format-Number $frame.EyeInsetRegionLeft ""0.###""), t=$(Format-Number $frame.EyeInsetRegionTop ""0.###""), w=$(Format-Number $frame.EyeInsetRegionWidth ""0.###""), h=$(Format-Number $frame.EyeInsetRegionHeight ""0.###"")</p>")
            }
            if (-not [string]::IsNullOrWhiteSpace($overlayRelative)) {
                [void]$html.AppendLine("<a href=""$(HtmlEncode $overlayRelative)""><img src=""$(HtmlEncode $overlayRelative)"" alt=""$(HtmlEncode $frame.Label)""></a>")
            }

            [void]$html.AppendLine("</div>")
        }

        [void]$html.AppendLine("</div>")
        [void]$html.AppendLine("</section>")
    }

    [void]$html.AppendLine("</body></html>")
    $html.ToString() | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}

if (-not $SkipBuild) {
    Write-Host "Building offline vision evaluator..."
    Invoke-Checked dotnet build .\tools\AvatarBuilderVisionEval\AvatarBuilderVisionEval.csproj --no-restore /p:UseSharedCompilation=false /p:UseAppHost=false "/p:BaseOutputPath=$buildOutputRoot"
}
else {
    $evaluatorDll = Join-Path $repoRoot "tools\AvatarBuilderVisionEval\bin\Debug\net10.0-windows\AvatarBuilderVisionEval.dll"
}

if (-not (Test-Path -LiteralPath $evaluatorDll)) {
    throw "Offline vision evaluator DLL was not found: $evaluatorDll"
}

$clipResults = New-Object System.Collections.Generic.List[object]
$clipIndex = 0
foreach ($samplePath in $SampleMediaPath) {
    $clipIndex++
    $resolvedSample = (Resolve-Path -LiteralPath $samplePath).Path
    $clipName = ConvertTo-SafeName $resolvedSample
    $clipOutput = Get-UniqueClipOutputFolder -Root $OutputRoot -BaseName $clipName -Index $clipIndex
    New-Item -ItemType Directory -Force -Path $clipOutput | Out-Null

    Write-Host ""
    Write-Host "== Evaluating $resolvedSample =="
    $arguments = @(
        $evaluatorDll,
        $resolvedSample,
        $clipOutput,
        $SampleFramesPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    if (-not [string]::IsNullOrWhiteSpace($EyeInset) -and
        -not $EyeInset.Equals("none", [StringComparison]::OrdinalIgnoreCase)) {
        $arguments += "--eye-inset"
        $arguments += $EyeInset
    }

    $arguments += "--write-overlays"
    Invoke-Checked dotnet @arguments

    $summaryPath = Join-Path $clipOutput "vision_eval_summary.json"
    $csvPath = Join-Path $clipOutput "vision_eval.csv"
    $reportPath = Join-Path $clipOutput "vision_eval_report.html"
    $corpusReadinessPath = Join-Path $clipOutput "personal_face_corpus_readiness.json"
    $corpusReadinessHtmlPath = Join-Path $clipOutput "personal_face_corpus_readiness.html"
    $avatarCapturePlanPath = Join-Path $clipOutput "measurement_avatar_capture_plan.json"
    $avatarCapturePlanHtmlPath = Join-Path $clipOutput "measurement_avatar_capture_plan.html"
    foreach ($path in @($summaryPath, $csvPath, $reportPath, $corpusReadinessPath, $corpusReadinessHtmlPath, $avatarCapturePlanPath, $avatarCapturePlanHtmlPath)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Expected evaluation artifact was not written: $path"
        }
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $rows = @(Import-Csv -LiteralPath $csvPath)
    $warnings = @(Get-Warnings $summary)
    $reviewFrames = @(Get-ReviewFrames -EvaluationFolder $clipOutput -Rows $rows)
    $capturePlanJsonPath = if ([string]::IsNullOrWhiteSpace($summary.MeasurementAvatarCapturePlanJsonPath)) { $avatarCapturePlanPath } else { [string]$summary.MeasurementAvatarCapturePlanJsonPath }
    $capturePlanHtmlPath = if ([string]::IsNullOrWhiteSpace($summary.MeasurementAvatarCapturePlanHtmlPath)) { $avatarCapturePlanHtmlPath } else { [string]$summary.MeasurementAvatarCapturePlanHtmlPath }
    $capturePlan = Get-CapturePlanDigest -JsonPath $capturePlanJsonPath -HtmlPath $capturePlanHtmlPath
    $featureAnchorRangeValues = @(
        @(
            (Read-Number $summary.EyeMidlineXToFaceWidthRange)
            (Read-Number $summary.MouthCenterXToFaceWidthRange)
            (Read-Number $summary.EyeToMouthXOffsetToFaceWidthRange)
        ) | Where-Object { $null -ne $_ }
    )
    $featureAnchorXRange = if ($featureAnchorRangeValues.Count -gt 0) { ($featureAnchorRangeValues | Measure-Object -Maximum).Maximum } else { $null }

    $clipResults.Add([pscustomobject]@{
        Name = [IO.Path]::GetFileName($resolvedSample)
        InputPath = $resolvedSample
        OutputFolder = $clipOutput
        SummaryPath = $summaryPath
        CsvPath = $csvPath
        ReportPath = $reportPath
        FrameCount = Read-Integer $summary.FrameCount
        FaceDetectionRate = Read-Number $summary.FaceDetectionRate
        FaceCenterXRange = Read-Number $summary.FaceCenterXRange
        FaceCenterYRange = Read-Number $summary.FaceCenterYRange
        FaceHeightRange = Read-Number $summary.FaceHeightRange
        ARotationAroundXRange = Read-Number $summary.ARotationAroundXRange
        BRotationAroundYRange = Read-Number $summary.BRotationAroundYRange
        CRotationAroundZRange = Read-Number $summary.CRotationAroundZRange
        IdentityMeasurementRate = Read-Number $summary.IdentityMeasurementRate
        FaceAspectRatioRange = Read-Number $summary.FaceAspectRatioRange
        FeatureAnchorXRange = $featureAnchorXRange
        EyeMidlineXToFaceWidthRange = Read-Number $summary.EyeMidlineXToFaceWidthRange
        MouthCenterXToFaceWidthRange = Read-Number $summary.MouthCenterXToFaceWidthRange
        EyeToMouthXOffsetToFaceWidthRange = Read-Number $summary.EyeToMouthXOffsetToFaceWidthRange
        InterEyeDistanceToFaceWidthRange = Read-Number $summary.InterEyeDistanceToFaceWidthRange
        MouthWidthToFaceWidthRange = Read-Number $summary.MouthWidthToFaceWidthRange
        EyeMeasurementRate = Read-Number $summary.LandmarkEyeMeasurementRate
        MouthMeasurementRate = Read-Number $summary.LandmarkMouthMeasurementRate
        JawDroopMeasurementRate = Read-Number $summary.LandmarkJawDroopMeasurementRate
        EyeUsableRate = Read-Number $summary.LandmarkEyeUsableRate
        MouthUsableRate = Read-Number $summary.LandmarkMouthUsableRate
        AverageOverallQuality = Read-Number $summary.LandmarkAverageOverallQuality
        MinimumOverallQuality = Read-Number $summary.LandmarkMinimumOverallQuality
        CaptureQualityAverageScore = Read-Number $summary.CaptureQualityAverageScore
        CaptureQualityMinimumScore = Read-Number $summary.CaptureQualityMinimumScore
        CaptureQualityCanCollectFrames = Read-Integer $summary.CaptureQualityCanCollectFrames
        CaptureQualityCanCollectRate = Read-Number $summary.CaptureQualityCanCollectRate
        CaptureQualityAvatarGradeFrames = Read-Integer $summary.CaptureQualityAvatarGradeFrames
        CaptureQualityAvatarGradeRate = Read-Number $summary.CaptureQualityAvatarGradeRate
        CaptureQualityAverageEyeScore = Read-Number $summary.CaptureQualityAverageEyeScore
        CaptureQualityAverageMouthScore = Read-Number $summary.CaptureQualityAverageMouthScore
        CaptureQualityAverageGlassesScore = Read-Number $summary.CaptureQualityAverageGlassesScore
        CaptureQualityAverageFaceScaleScore = Read-Number $summary.CaptureQualityAverageFaceScaleScore
        CaptureQualityLabels = @($summary.CaptureQualityLabels)
        CaptureQualityTopIssues = @($summary.CaptureQualityTopIssues)
        CaptureQualityTopSuggestions = @($summary.CaptureQualityTopSuggestions)
        FaceReliabilityUsableRate = Read-Number $summary.FaceReliabilityUsableRate
        FaceReliabilityAverage = Read-Number $summary.FaceReliabilityAverage
        FaceReliabilityMinimum = Read-Number $summary.FaceReliabilityMinimum
        FaceContinuityAverage = Read-Number $summary.FaceContinuityAverage
        EyeReliabilityAverage = Read-Number $summary.EyeReliabilityAverage
        MouthReliabilityAverage = Read-Number $summary.MouthReliabilityAverage
        MinimumEyeOpening = Read-Number $summary.MinimumEyeOpening
        MaximumMouthOpening = Read-Number $summary.MaximumMouthOpening
        MaximumJawDroop = Read-Number $summary.MaximumJawDroop
        EyeOpeningSlopePerSecond = Read-Number $summary.EyeOpeningSlopePerSecond
        MouthOpeningSlopePerSecond = Read-Number $summary.MouthOpeningSlopePerSecond
        EyeInsetMeasurementRate = Read-Number $summary.EyeInsetMeasurementRate
        EyeInsetImageQualityRate = Read-Number $summary.EyeInsetImageQualityRate
        EyeInsetEnabled = Read-Boolean $summary.EyeInsetEnabled
        EyeInsetDominantRegion = [string]$summary.EyeInsetDominantRegion
        EyeInsetAverageOpening = Read-Number $summary.EyeInsetAverageOpening
        EyeInsetMinimumOpening = Read-Number $summary.EyeInsetMinimumOpening
        EyeInsetMaximumOpening = Read-Number $summary.EyeInsetMaximumOpening
        EyeInsetOpeningSlopePerSecond = Read-Number $summary.EyeInsetOpeningSlopePerSecond
        EyeInsetCueBaselineReadyRate = Read-Number $summary.EyeInsetCueBaselineReadyRate
        EyeInsetCueMaximumClosure = Read-Number $summary.EyeInsetCueMaximumClosure
        EyeInsetCueMaximumScore = Read-Number $summary.EyeInsetCueMaximumScore
        EyeInsetFullFramePairedRate = Read-Number $summary.EyeInsetFullFramePairedRate
        EyeInsetFullFrameOpeningCorrelation = Read-Number $summary.EyeInsetFullFrameOpeningCorrelation
        EyeInsetFullFrameNormalizedMeanAbsoluteError = Read-Number $summary.EyeInsetFullFrameNormalizedMeanAbsoluteError
        EyeInsetFullFrameDirectionAgreement = Read-Number $summary.EyeInsetFullFrameDirectionAgreement
        EyeInsetFullFrameSlopeDirectionAgreement = Read-Number $summary.EyeInsetFullFrameSlopeDirectionAgreement
        EyeInsetFullFrameAgreementTrustPercent = Read-Number $summary.EyeInsetFullFrameAgreementTrustPercent
        EyeInsetAverageConfidence = Read-Number $summary.EyeInsetAverageConfidence
        EyeInsetMaximumGlare = Read-Number $summary.EyeInsetMaximumGlare
        EyeInsetAverageContrast = Read-Number $summary.EyeInsetAverageContrast
        EyeInsetAverageSharpness = Read-Number $summary.EyeInsetAverageSharpness
        EyeInsetAverageDarkCoverage = Read-Number $summary.EyeInsetAverageDarkCoverage
        MediaPipeDenseLockRate = Read-Number $summary.MediaPipeDenseLockRate
        MediaPipeBlendshapeFrameRate = Read-Number $summary.MediaPipeBlendshapeFrameRate
        PossibleOneEyeArtifactFrames = Read-Integer $summary.LandmarkPossibleOneEyeArtifactFrames
        EyeArtifactSuppressedFrames = Read-Integer $summary.LandmarkEyeArtifactSuppressedFrames
        LeftEyeReconstructedFrames = Read-Integer $summary.LandmarkLeftEyeReconstructedFrames
        RightEyeReconstructedFrames = Read-Integer $summary.LandmarkRightEyeReconstructedFrames
        MouthReconstructedFrames = Read-Integer $summary.LandmarkMouthReconstructedFrames
        PersonalFaceMotionModelPath = $summary.PersonalFaceMotionModelPath
        PersonalFaceMotionModelStatus = $summary.PersonalFaceMotionModelStatus
        PersonalFaceMotionModelUsableObservations = Read-Integer $summary.PersonalFaceMotionModelUsableObservations
        PersonalFaceMotionModelMotionPairs = Read-Integer $summary.PersonalFaceMotionModelMotionPairs
        CorpusReadinessPath = $corpusReadinessPath
        CorpusReadinessHtmlPath = $corpusReadinessHtmlPath
        CorpusReadinessStatus = $summary.PersonalFaceCorpusReadinessStatus
        CorpusReadinessPercent = Read-Number $summary.PersonalFaceCorpusReadinessPercent
        LearningStabilityCoveragePercent = Read-Number $summary.PersonalFaceCorpusLearningStabilityCoveragePercent
        CorpusReadinessWarnings = @($summary.PersonalFaceCorpusReadinessWarnings)
        CorpusReadinessNextCaptureSuggestions = @($summary.PersonalFaceCorpusReadinessNextCaptureSuggestions)
        PersonalFaceCorpusDataAuditHealthPercent = Read-Number $summary.PersonalFaceCorpusDataAuditHealthPercent
        PersonalFaceCorpusFeatureAnchoringHealthPercent = Read-Number $summary.PersonalFaceCorpusFeatureAnchoringHealthPercent
        PersonalFaceCorpusIdentitySessionHealthPercent = Read-Number $summary.PersonalFaceCorpusIdentitySessionHealthPercent
        PersonalFaceCorpusIdentitySessionAuditStage = $summary.PersonalFaceCorpusIdentitySessionAuditStage
        PersonalFaceCorpusIdentitySessionAuditStatus = $summary.PersonalFaceCorpusIdentitySessionAuditStatus
        PersonalFaceCorpusRecentIdentityMeasurementSamples = Read-Integer $summary.PersonalFaceCorpusRecentIdentityMeasurementSamples
        PersonalFaceCorpusAverageRecentIdentityConfidencePercent = Read-Number $summary.PersonalFaceCorpusAverageRecentIdentityConfidencePercent
        PersonalFaceCorpusMinimumRecentIdentityConfidencePercent = Read-Number $summary.PersonalFaceCorpusMinimumRecentIdentityConfidencePercent
        PersonalFaceCorpusRecentIdentityOutlierFrameRate = Read-Number $summary.PersonalFaceCorpusRecentIdentityOutlierFrameRate
        PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent = Read-Number $summary.PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent
        PersonalFaceCorpusPoseExplainedFeatureObservedRange = Read-Number $summary.PersonalFaceCorpusPoseExplainedFeatureObservedRange
        PersonalFaceCorpusPoseExplainedFeatureExpectedRange = Read-Number $summary.PersonalFaceCorpusPoseExplainedFeatureExpectedRange
        PersonalFaceCorpusEyeApertureReliabilityHealthPercent = Read-Number $summary.PersonalFaceCorpusEyeApertureReliabilityHealthPercent
        PersonalFaceCorpusPossibleOneEyeArtifactRate = Read-Number $summary.PersonalFaceCorpusPossibleOneEyeArtifactRate
        PersonalFaceCorpusEyeAgreementAveragePercent = Read-Number $summary.PersonalFaceCorpusEyeAgreementAveragePercent
        PersonalFaceCorpusEyeAgreementMinimumPercent = Read-Number $summary.PersonalFaceCorpusEyeAgreementMinimumPercent
        PersonalFaceCorpusMouthVerticalAnchorHealthPercent = Read-Number $summary.PersonalFaceCorpusMouthVerticalAnchorHealthPercent
        PersonalFaceCorpusMouthVerticalAnchorSamplesReviewed = Read-Integer $summary.PersonalFaceCorpusMouthVerticalAnchorSamplesReviewed
        PersonalFaceCorpusMouthVerticalAnchorSuspiciousSampleRate = Read-Number $summary.PersonalFaceCorpusMouthVerticalAnchorSuspiciousSampleRate
        PersonalFaceCorpusDataAuditFindings = @($summary.PersonalFaceCorpusDataAuditFindings | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        PersonalModelIdentityGatePolicy = $summary.PersonalModelIdentityGatePolicy
        PersonalModelIdentitySignatureSamples = Read-Integer $summary.PersonalModelIdentitySignatureSamples
        PersonalModelIdentityCoveragePercent = Read-Number $summary.PersonalModelIdentityCoveragePercent
        PersonalModelContourShapeCoveragePercent = Read-Number $summary.PersonalModelContourShapeCoveragePercent
        PersonalModelContourDepthProfileHealthPercent = Read-Number $summary.PersonalModelContourDepthProfileHealthPercent
        PersonalModelSurfaceShapeCoveragePercent = Read-Number $summary.PersonalModelSurfaceShapeCoveragePercent
        PersonalModelSurfaceDepthProfileHealthPercent = Read-Number $summary.PersonalModelSurfaceDepthProfileHealthPercent
        PersonalModelZDistanceCoveragePercent = Read-Number $summary.PersonalModelZDistanceCoveragePercent
        PersonalModelZDistanceEvidenceHealthPercent = Read-Number $summary.PersonalModelZDistanceEvidenceHealthPercent
        PersonalModelZEstimateSamples = Read-Integer $summary.PersonalModelZEstimateSamples
        PersonalModelZApparentDistanceRange = Read-Number $summary.PersonalModelZApparentDistanceRange
        PersonalModelAverageZConfidencePercent = Read-Number $summary.PersonalModelAverageZConfidencePercent
        PersonalFaceCorpusZDistanceEvidenceHealthPercent = Read-Number $summary.PersonalFaceCorpusZDistanceEvidenceHealthPercent
        PersonalFaceCorpusAverageZConfidencePercent = Read-Number $summary.PersonalFaceCorpusAverageZConfidencePercent
        PersonalFaceCorpusZApparentOnlyRate = Read-Number $summary.PersonalFaceCorpusZApparentOnlyRate
        PersonalModelARotationAroundXCoveragePercent = Read-Number $summary.PersonalModelARotationAroundXCoveragePercent
        PersonalModelBRotationAroundYCoveragePercent = Read-Number $summary.PersonalModelBRotationAroundYCoveragePercent
        PersonalModelCRotationAroundZCoveragePercent = Read-Number $summary.PersonalModelCRotationAroundZCoveragePercent
        PersonalModelXYZABCCoveragePercent = Read-Number $summary.PersonalModelXYZABCCoveragePercent
        PersonalModelEyeBehindGlassesTrustPercent = Read-Number $summary.PersonalModelEyeBehindGlassesTrustPercent
        PersonalModelMouthJawTrustPercent = Read-Number $summary.PersonalModelMouthJawTrustPercent
        PersonalModelDirectFeatureMeasurementTrustPercent = Read-Number $summary.PersonalModelDirectFeatureMeasurementTrustPercent
        PersonalFaceCorpusLeftEyeShapeSamples = Read-Integer $summary.PersonalFaceCorpusLeftEyeShapeSamples
        PersonalFaceCorpusRightEyeShapeSamples = Read-Integer $summary.PersonalFaceCorpusRightEyeShapeSamples
        PersonalFaceCorpusOuterLipShapeSamples = Read-Integer $summary.PersonalFaceCorpusOuterLipShapeSamples
        PersonalFaceCorpusInnerLipShapeSamples = Read-Integer $summary.PersonalFaceCorpusInnerLipShapeSamples
        PersonalFaceCorpusJawShapeSamples = Read-Integer $summary.PersonalFaceCorpusJawShapeSamples
        PersonalFaceCorpusLeftBrowShapeSamples = Read-Integer $summary.PersonalFaceCorpusLeftBrowShapeSamples
        PersonalFaceCorpusRightBrowShapeSamples = Read-Integer $summary.PersonalFaceCorpusRightBrowShapeSamples
        PersonalFaceCorpusNoseBridgeShapeSamples = Read-Integer $summary.PersonalFaceCorpusNoseBridgeShapeSamples
        PersonalFaceCorpusNoseBaseShapeSamples = Read-Integer $summary.PersonalFaceCorpusNoseBaseShapeSamples
        PersonalFaceCorpusLeftCheekSurfaceSamples = Read-Integer $summary.PersonalFaceCorpusLeftCheekSurfaceSamples
        PersonalFaceCorpusRightCheekSurfaceSamples = Read-Integer $summary.PersonalFaceCorpusRightCheekSurfaceSamples
        PersonalFaceCorpusForeheadSurfaceSamples = Read-Integer $summary.PersonalFaceCorpusForeheadSurfaceSamples
        PersonalModelLearningAnchorPercent = Read-Number $summary.PersonalModelLearningAnchorPercent
        PersonalModelLearningAnchorStatus = $summary.PersonalModelLearningAnchorStatus
        PersonalModelMaxNextSampleInfluencePercent = Read-Number $summary.PersonalModelMaxNextSampleInfluencePercent
        PersonalModelMaxEventLikeNextSampleInfluencePercent = Read-Number $summary.PersonalModelMaxEventLikeNextSampleInfluencePercent
        PersonalModelTrackingArtifactRejectedSamples = Read-Integer $summary.PersonalModelTrackingArtifactRejectedSamples
        PersonalModelSubjectMismatchRejectedSamples = Read-Integer $summary.PersonalModelSubjectMismatchRejectedSamples
        PersonalModelFaceAspectAverage = Read-Number $summary.PersonalModelFaceAspectAverage
        PersonalModelInterEyeDistanceToFaceWidthAverage = Read-Number $summary.PersonalModelInterEyeDistanceToFaceWidthAverage
        PersonalModelMouthWidthToFaceWidthAverage = Read-Number $summary.PersonalModelMouthWidthToFaceWidthAverage
        AvatarCapturePlanJsonPath = $capturePlan.JsonPath
        AvatarCapturePlanHtmlPath = $capturePlan.HtmlPath
        AvatarCapturePlanDecision = $capturePlan.CollectionDecision
        AvatarCapturePlanCanCollect = $capturePlan.CanCollectMeasurements
        AvatarCapturePlanItemCount = $capturePlan.ItemCount
        AvatarCapturePlanTargetMinutes = $capturePlan.TotalTargetMinutes
        AvatarCapturePlanEstimatedMeasurementBytes = $capturePlan.EstimatedMeasurementBytes
        AvatarCapturePlanFirstItemTitle = $capturePlan.FirstItemTitle
        AvatarCapturePlanFirstItemCategory = $capturePlan.FirstItemCategory
        AvatarCapturePlanFirstItemPriority = $capturePlan.FirstItemPriority
        AvatarCapturePlanFirstItemInstructions = $capturePlan.FirstItemInstructions
        AvatarCapturePlanFirstItemRelatedScoreName = $capturePlan.FirstItemRelatedScoreName
        AvatarCapturePlanFirstItemRelatedScorePercent = $capturePlan.FirstItemRelatedScorePercent
        OverlayFrameCount = Read-Integer $summary.OverlayFrameCount
        WarningCount = $warnings.Count
        Warnings = $warnings
        ReviewFrames = $reviewFrames
    })
}

$endedAt = Get-Date
$clipResultArray = @($clipResults.ToArray())
$batchMetrics = Get-BatchMetricSummary $clipResultArray
$qualityChecks = New-Object System.Collections.Generic.List[object]
Add-QualityCheck -Checks $qualityChecks -Name "Face lock" -Passed (Test-Minimum $batchMetrics.MinimumFaceDetectionRate 0.90) -Actual (Format-Percent $batchMetrics.MinimumFaceDetectionRate) -Expected "minimum >= 90%"
Add-QualityCheck -Checks $qualityChecks -Name "Eye usable rate" -Passed (Test-Minimum $batchMetrics.MinimumEyeUsableRate 0.75) -Actual (Format-Percent $batchMetrics.MinimumEyeUsableRate) -Expected "minimum >= 75%"
Add-QualityCheck -Checks $qualityChecks -Name "Mouth usable rate" -Passed (Test-Minimum $batchMetrics.MinimumMouthUsableRate 0.75) -Actual (Format-Percent $batchMetrics.MinimumMouthUsableRate) -Expected "minimum >= 75%"
Add-QualityCheck -Checks $qualityChecks -Name "Average face reliability" -Passed (Test-Minimum $batchMetrics.AverageFaceReliability 75) -Actual (Format-Number $batchMetrics.AverageFaceReliability "0.0") -Expected "average >= 75%"
Add-QualityCheck -Checks $qualityChecks -Name "Reliable-lock usable rate" -Passed (Test-Minimum $batchMetrics.MinimumFaceReliabilityUsableRate 0.70) -Actual (Format-Percent $batchMetrics.MinimumFaceReliabilityUsableRate) -Expected "minimum >= 70%"
Add-QualityCheck -Checks $qualityChecks -Name "Capture quality recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageCaptureQualityScore)) -Actual (Format-Number $batchMetrics.AverageCaptureQualityScore "0.0") -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Average capture quality" -Passed (Test-Minimum $batchMetrics.AverageCaptureQualityScore 62) -Actual (Format-Number $batchMetrics.AverageCaptureQualityScore "0.0") -Expected "average >= 62%"
Add-QualityCheck -Checks $qualityChecks -Name "Avatar-grade evidence" -Passed (Test-Minimum $batchMetrics.MinimumCaptureQualityAvatarGradeRate 0.05) -Actual (Format-Percent $batchMetrics.MinimumCaptureQualityAvatarGradeRate) -Expected "minimum >= 5%"
Add-QualityCheck -Checks $qualityChecks -Name "Capture plans written" -Passed ($batchMetrics.CapturePlanClipCount -eq $clipResultArray.Count -and $batchMetrics.TotalAvatarCapturePlanItemCount -gt 0) -Actual "$($batchMetrics.CapturePlanClipCount)/$($clipResultArray.Count) clip(s), $($batchMetrics.TotalAvatarCapturePlanItemCount) item(s)" -Expected "one capture plan per clip"
Add-QualityCheck -Checks $qualityChecks -Name "Learning stability recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageLearningStabilityCoveragePercent) -and $null -ne (Read-Number $batchMetrics.MaximumNextSampleInfluencePercent)) -Actual "stability $(Format-Number $batchMetrics.AverageLearningStabilityCoveragePercent "0.0")%, max influence $(Format-Number $batchMetrics.MaximumNextSampleInfluencePercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Contour shape coverage recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageContourShapeCoveragePercent)) -Actual "shape $(Format-Number $batchMetrics.AverageContourShapeCoveragePercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Surface shape coverage recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageSurfaceShapeCoveragePercent)) -Actual "surface $(Format-Number $batchMetrics.AverageSurfaceShapeCoveragePercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Contour Z profile recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageContourDepthProfileHealthPercent)) -Actual "contour Z $(Format-Number $batchMetrics.AverageContourDepthProfileHealthPercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Surface Z profile recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageSurfaceDepthProfileHealthPercent)) -Actual "surface Z $(Format-Number $batchMetrics.AverageSurfaceDepthProfileHealthPercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "XYZABC coverage recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageXYZABCCoveragePercent)) -Actual "XYZABC $(Format-Number $batchMetrics.AverageXYZABCCoveragePercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Explicit Z evidence recorded" -Passed ((Read-Integer $batchMetrics.TotalZEstimateSamples) -gt 0 -and $null -ne (Read-Number $batchMetrics.AverageZDistanceEvidenceHealthPercent)) -Actual "Z samples $($batchMetrics.TotalZEstimateSamples), health $(Format-Number $batchMetrics.AverageZDistanceEvidenceHealthPercent "0.0")%, confidence $(Format-Number $batchMetrics.AverageZConfidencePercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Direct feature trust recorded" -Passed ($null -ne (Read-Number $batchMetrics.AverageDirectFeatureMeasurementTrustPercent)) -Actual "trust $(Format-Number $batchMetrics.AverageDirectFeatureMeasurementTrustPercent "0.0")%" -Expected "recorded"
Add-QualityCheck -Checks $qualityChecks -Name "Identity/anchor measurement" -Passed (Test-Minimum $batchMetrics.MinimumIdentityMeasurementRate 0.85) -Actual (Format-Percent $batchMetrics.MinimumIdentityMeasurementRate) -Expected "minimum >= 85%"
$identitySessionComparable = (Read-Integer $batchMetrics.TotalRecentIdentityMeasurementSamples) -ge 12
Add-QualityCheck -Checks $qualityChecks -Name "Identity-session health" -Passed ((-not $identitySessionComparable) -or (Test-Minimum $batchMetrics.MinimumIdentitySessionHealthPercent 70)) -Actual "health $(Format-Number $batchMetrics.MinimumIdentitySessionHealthPercent "0.0")%, comparable samples $($batchMetrics.TotalRecentIdentityMeasurementSamples), avg confidence $(Format-Number $batchMetrics.AverageRecentIdentityConfidencePercent "0.0")%, max outlier $(Format-Percent $batchMetrics.MaximumRecentIdentityOutlierFrameRate)" -Expected "minimum >= 70% once >= 12 comparable samples"
Add-QualityCheck -Checks $qualityChecks -Name "Data-audit health" -Passed (Test-Minimum $batchMetrics.MinimumDataAuditHealthPercent 70) -Actual "$(Format-Number $batchMetrics.MinimumDataAuditHealthPercent "0.0")%" -Expected "minimum >= 70%"
Add-QualityCheck -Checks $qualityChecks -Name "Feature anchoring health" -Passed (Test-Minimum $batchMetrics.MinimumFeatureAnchoringHealthPercent 60) -Actual "$(Format-Number $batchMetrics.MinimumFeatureAnchoringHealthPercent "0.0")%" -Expected "minimum >= 60%"
Add-QualityCheck -Checks $qualityChecks -Name "Pose-explained feature motion" -Passed (Test-Minimum $batchMetrics.MinimumPoseExplainedFeatureMotionHealthPercent 60) -Actual "$(Format-Number $batchMetrics.MinimumPoseExplainedFeatureMotionHealthPercent "0.0")%" -Expected "minimum >= 60%"
Add-QualityCheck -Checks $qualityChecks -Name "Eye aperture reliability" -Passed (Test-Minimum $batchMetrics.MinimumEyeApertureReliabilityHealthPercent 70) -Actual "$(Format-Number $batchMetrics.MinimumEyeApertureReliabilityHealthPercent "0.0")%" -Expected "minimum >= 70%"
Add-QualityCheck -Checks $qualityChecks -Name "Mouth vertical anchor health" -Passed (Test-Minimum $batchMetrics.MinimumMouthVerticalAnchorHealthPercent 70) -Actual "$(Format-Number $batchMetrics.MinimumMouthVerticalAnchorHealthPercent "0.0")%" -Expected "minimum >= 70%"
Add-QualityCheck -Checks $qualityChecks -Name "Pose-aware feature anchor drift" -Passed ((Test-Maximum $batchMetrics.MaximumFeatureAnchorXRange 0.16) -or (Test-Minimum $batchMetrics.MinimumFeatureAnchoringHealthPercent 60)) -Actual "raw $(Format-Number $batchMetrics.MaximumFeatureAnchorXRange "0.###"), health $(Format-Number $batchMetrics.MinimumFeatureAnchoringHealthPercent "0.0")%" -Expected "raw <= 0.16 or pose-aware health >= 60%"
Add-QualityCheck -Checks $qualityChecks -Name "Subject contamination" -Passed ($batchMetrics.TotalSubjectMismatchRejectedSamples -eq 0) -Actual ([string]$batchMetrics.TotalSubjectMismatchRejectedSamples) -Expected "0 subject mismatch rejects"
Add-QualityCheck -Checks $qualityChecks -Name "Measurement storage estimate" -Passed (Test-Maximum $batchMetrics.TotalAvatarCapturePlanEstimatedMeasurementBytes 10000000000) -Actual ([string]$batchMetrics.TotalAvatarCapturePlanEstimatedMeasurementBytes) -Expected "<= 10,000,000,000 bytes"
if ($clipResultArray.Count -ge 3) {
    Add-QualityCheck -Checks $qualityChecks -Name "Dynamic face horizontal coverage" -Passed (Test-Minimum $batchMetrics.MaximumFaceCenterXRange 0.08) -Actual (Format-Number $batchMetrics.MaximumFaceCenterXRange "0.###") -Expected "max center-X range >= 0.08"
    Add-QualityCheck -Checks $qualityChecks -Name "Dynamic face vertical coverage" -Passed (Test-Minimum $batchMetrics.MaximumFaceCenterYRange 0.05) -Actual (Format-Number $batchMetrics.MaximumFaceCenterYRange "0.###") -Expected "max center-Y range >= 0.05"
    Add-QualityCheck -Checks $qualityChecks -Name "Dynamic face scale coverage" -Passed (Test-Minimum $batchMetrics.MaximumFaceHeightRange 0.08) -Actual (Format-Number $batchMetrics.MaximumFaceHeightRange "0.###") -Expected "max face-height range >= 0.08"
}
if (-not [string]::IsNullOrWhiteSpace($EyeInset) -and -not $EyeInset.Equals("none", [StringComparison]::OrdinalIgnoreCase)) {
    if ($batchMetrics.EyeInsetDetectedClipCount -gt 0) {
        Add-QualityCheck -Checks $qualityChecks -Name "Eye-inset measurement" -Passed (Test-Minimum $batchMetrics.MinimumEyeInsetMeasurementRate 0.70) -Actual (Format-Percent $batchMetrics.MinimumEyeInsetMeasurementRate) -Expected "minimum >= 70%"
        Add-QualityCheck -Checks $qualityChecks -Name "Eye-inset diagnostics" -Passed (Test-Minimum $batchMetrics.MinimumEyeInsetImageQualityRate 0.70) -Actual (Format-Percent $batchMetrics.MinimumEyeInsetImageQualityRate) -Expected "minimum >= 70%"
        Add-QualityCheck -Checks $qualityChecks -Name "Eye-inset plausible opening" -Passed (Test-Maximum $batchMetrics.MaximumEyeInsetOpening 0.95) -Actual (Format-Number $batchMetrics.MaximumEyeInsetOpening "0.###") -Expected "maximum <= 0.95"
        Add-QualityCheck -Checks $qualityChecks -Name "Eye-inset/full-frame agreement trust" -Passed (Test-Minimum $batchMetrics.MinimumEyeInsetFullFrameAgreementTrustPercent 35) -Actual "$(Format-Number $batchMetrics.MinimumEyeInsetFullFrameAgreementTrustPercent "0.0")%" -Expected "minimum >= 35%"
    }
    else {
        Add-QualityCheck -Checks $qualityChecks -Name "Eye-inset auto credible region" -Passed $true -Actual "0/$($clipResultArray.Count) clip(s)" -Expected "no zoomed inset found; optional inset gates skipped"
    }
}

$failedQualityChecks = @($qualityChecks | Where-Object { -not $_.Passed })
$batchSummary = [pscustomobject]@{
    StartedAt = $startedAt.ToUniversalTime().ToString("O")
    EndedAt = $endedAt.ToUniversalTime().ToString("O")
    DurationSeconds = [Math]::Round(($endedAt - $startedAt).TotalSeconds, 3)
    SampleFramesPerSecond = $SampleFramesPerSecond
    EyeInset = $EyeInset
    ClipCount = $clipResultArray.Count
    ClipsWithWarnings = @($clipResultArray | Where-Object { $_.WarningCount -gt 0 }).Count
    OutputRoot = $OutputRoot
    MinimumFaceDetectionRate = $batchMetrics.MinimumFaceDetectionRate
    MinimumEyeUsableRate = $batchMetrics.MinimumEyeUsableRate
    MinimumMouthUsableRate = $batchMetrics.MinimumMouthUsableRate
    MinimumOverallQuality = $batchMetrics.MinimumOverallQuality
    MinimumCaptureQualityScore = $batchMetrics.MinimumCaptureQualityScore
    MinimumFaceReliability = $batchMetrics.MinimumFaceReliability
    MinimumFaceReliabilityUsableRate = $batchMetrics.MinimumFaceReliabilityUsableRate
    MaximumFaceCenterXRange = $batchMetrics.MaximumFaceCenterXRange
    MaximumFaceCenterYRange = $batchMetrics.MaximumFaceCenterYRange
    MaximumFaceHeightRange = $batchMetrics.MaximumFaceHeightRange
    MinimumIdentityMeasurementRate = $batchMetrics.MinimumIdentityMeasurementRate
    MinimumIdentitySessionHealthPercent = $batchMetrics.MinimumIdentitySessionHealthPercent
    AverageIdentitySessionHealthPercent = $batchMetrics.AverageIdentitySessionHealthPercent
    AverageRecentIdentityConfidencePercent = $batchMetrics.AverageRecentIdentityConfidencePercent
    MaximumRecentIdentityOutlierFrameRate = $batchMetrics.MaximumRecentIdentityOutlierFrameRate
    TotalRecentIdentityMeasurementSamples = $batchMetrics.TotalRecentIdentityMeasurementSamples
    IdentitySessionAuditStages = @($clipResultArray | ForEach-Object { $_.PersonalFaceCorpusIdentitySessionAuditStage } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Unique)
    MinimumDataAuditHealthPercent = $batchMetrics.MinimumDataAuditHealthPercent
    MinimumFeatureAnchoringHealthPercent = $batchMetrics.MinimumFeatureAnchoringHealthPercent
    MinimumPoseExplainedFeatureMotionHealthPercent = $batchMetrics.MinimumPoseExplainedFeatureMotionHealthPercent
    MinimumEyeApertureReliabilityHealthPercent = $batchMetrics.MinimumEyeApertureReliabilityHealthPercent
    MinimumMouthVerticalAnchorHealthPercent = $batchMetrics.MinimumMouthVerticalAnchorHealthPercent
    MaximumFeatureAnchorXRange = $batchMetrics.MaximumFeatureAnchorXRange
    MaximumEyeMidlineXToFaceWidthRange = $batchMetrics.MaximumEyeMidlineXToFaceWidthRange
    MaximumMouthCenterXToFaceWidthRange = $batchMetrics.MaximumMouthCenterXToFaceWidthRange
    MaximumEyeToMouthXOffsetToFaceWidthRange = $batchMetrics.MaximumEyeToMouthXOffsetToFaceWidthRange
    MaximumInterEyeDistanceToFaceWidthRange = $batchMetrics.MaximumInterEyeDistanceToFaceWidthRange
    MaximumMouthWidthToFaceWidthRange = $batchMetrics.MaximumMouthWidthToFaceWidthRange
    DynamicFaceHorizontalCoverageClipCount = $batchMetrics.DynamicFaceHorizontalCoverageClipCount
    DynamicFaceVerticalCoverageClipCount = $batchMetrics.DynamicFaceVerticalCoverageClipCount
    DynamicFaceScaleCoverageClipCount = $batchMetrics.DynamicFaceScaleCoverageClipCount
    MinimumCorpusReadinessPercent = $batchMetrics.MinimumCorpusReadinessPercent
    MinimumLearningStabilityCoveragePercent = $batchMetrics.MinimumLearningStabilityCoveragePercent
    AverageLearningStabilityCoveragePercent = $batchMetrics.AverageLearningStabilityCoveragePercent
    MinimumLearningAnchorPercent = $batchMetrics.MinimumLearningAnchorPercent
    MaximumNextSampleInfluencePercent = $batchMetrics.MaximumNextSampleInfluencePercent
    MaximumEventLikeNextSampleInfluencePercent = $batchMetrics.MaximumEventLikeNextSampleInfluencePercent
    MinimumIdentityCoveragePercent = $batchMetrics.MinimumIdentityCoveragePercent
    MinimumContourShapeCoveragePercent = $batchMetrics.MinimumContourShapeCoveragePercent
    MinimumContourDepthProfileHealthPercent = $batchMetrics.MinimumContourDepthProfileHealthPercent
    MinimumSurfaceShapeCoveragePercent = $batchMetrics.MinimumSurfaceShapeCoveragePercent
    MinimumSurfaceDepthProfileHealthPercent = $batchMetrics.MinimumSurfaceDepthProfileHealthPercent
    MinimumXYZABCCoveragePercent = $batchMetrics.MinimumXYZABCCoveragePercent
    MinimumDirectFeatureMeasurementTrustPercent = $batchMetrics.MinimumDirectFeatureMeasurementTrustPercent
    AverageFaceReliability = $batchMetrics.AverageFaceReliability
    AverageFaceContinuity = $batchMetrics.AverageFaceContinuity
    AverageDataAuditHealthPercent = $batchMetrics.AverageDataAuditHealthPercent
    AverageFeatureAnchoringHealthPercent = $batchMetrics.AverageFeatureAnchoringHealthPercent
    AveragePoseExplainedFeatureMotionHealthPercent = $batchMetrics.AveragePoseExplainedFeatureMotionHealthPercent
    AverageEyeApertureReliabilityHealthPercent = $batchMetrics.AverageEyeApertureReliabilityHealthPercent
    AverageMouthVerticalAnchorHealthPercent = $batchMetrics.AverageMouthVerticalAnchorHealthPercent
    AverageCaptureQualityScore = $batchMetrics.AverageCaptureQualityScore
    MinimumCaptureQualityCanCollectRate = $batchMetrics.MinimumCaptureQualityCanCollectRate
    MinimumCaptureQualityAvatarGradeRate = $batchMetrics.MinimumCaptureQualityAvatarGradeRate
    AverageCorpusReadinessPercent = $batchMetrics.AverageCorpusReadinessPercent
    AverageIdentityCoveragePercent = $batchMetrics.AverageIdentityCoveragePercent
    AverageContourShapeCoveragePercent = $batchMetrics.AverageContourShapeCoveragePercent
    AverageContourDepthProfileHealthPercent = $batchMetrics.AverageContourDepthProfileHealthPercent
    AverageSurfaceShapeCoveragePercent = $batchMetrics.AverageSurfaceShapeCoveragePercent
    AverageSurfaceDepthProfileHealthPercent = $batchMetrics.AverageSurfaceDepthProfileHealthPercent
    AverageXYZABCCoveragePercent = $batchMetrics.AverageXYZABCCoveragePercent
    AverageZDistanceCoveragePercent = $batchMetrics.AverageZDistanceCoveragePercent
    AverageZDistanceEvidenceHealthPercent = $batchMetrics.AverageZDistanceEvidenceHealthPercent
    AverageZConfidencePercent = $batchMetrics.AverageZConfidencePercent
    TotalZEstimateSamples = $batchMetrics.TotalZEstimateSamples
    AverageARotationAroundXCoveragePercent = $batchMetrics.AverageARotationAroundXCoveragePercent
    AverageBRotationAroundYCoveragePercent = $batchMetrics.AverageBRotationAroundYCoveragePercent
    AverageCRotationAroundZCoveragePercent = $batchMetrics.AverageCRotationAroundZCoveragePercent
    AverageEyeBehindGlassesTrustPercent = $batchMetrics.AverageEyeBehindGlassesTrustPercent
    AverageMouthJawTrustPercent = $batchMetrics.AverageMouthJawTrustPercent
    AverageDirectFeatureMeasurementTrustPercent = $batchMetrics.AverageDirectFeatureMeasurementTrustPercent
    CapturePlanClipCount = $batchMetrics.CapturePlanClipCount
    TotalAvatarCapturePlanItemCount = $batchMetrics.TotalAvatarCapturePlanItemCount
    TotalAvatarCapturePlanTargetMinutes = $batchMetrics.TotalAvatarCapturePlanTargetMinutes
    TotalAvatarCapturePlanEstimatedMeasurementBytes = $batchMetrics.TotalAvatarCapturePlanEstimatedMeasurementBytes
    TotalWarningCount = $batchMetrics.TotalWarningCount
    TotalOneEyeArtifactFrames = $batchMetrics.TotalOneEyeArtifactFrames
    TotalEyeArtifactSuppressedFrames = $batchMetrics.TotalEyeArtifactSuppressedFrames
    TotalTrackingArtifactRejectedSamples = $batchMetrics.TotalTrackingArtifactRejectedSamples
    TotalSubjectMismatchRejectedSamples = $batchMetrics.TotalSubjectMismatchRejectedSamples
    TotalIdentitySignatureSamples = $batchMetrics.TotalIdentitySignatureSamples
    MinimumEyeInsetMeasurementRate = $batchMetrics.MinimumEyeInsetMeasurementRate
    MinimumEyeInsetImageQualityRate = $batchMetrics.MinimumEyeInsetImageQualityRate
    MaximumEyeInsetOpening = $batchMetrics.MaximumEyeInsetOpening
    EyeInsetDetectedClipCount = $batchMetrics.EyeInsetDetectedClipCount
    MinimumEyeInsetFullFrameAgreementTrustPercent = $batchMetrics.MinimumEyeInsetFullFrameAgreementTrustPercent
    AverageEyeInsetFullFrameAgreementTrustPercent = $batchMetrics.AverageEyeInsetFullFrameAgreementTrustPercent
    Passed = $failedQualityChecks.Count -eq 0
    FailedQualityCheckCount = $failedQualityChecks.Count
    QualityChecks = @($qualityChecks.ToArray())
    ClipResults = $clipResultArray
}

$summaryPath = Join-Path $OutputRoot "real_clip_batch_summary.json"
$reportPath = Join-Path $OutputRoot "real_clip_batch_report.html"
$batchSummary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-HtmlReport -ReportPath $reportPath -ClipResults $clipResultArray -BatchSummary $batchSummary

Write-Host ""
Write-Host "Real clip batch summary: $summaryPath"
Write-Host "Real clip batch report:  $reportPath"

if ($FailOnWarnings -and $batchSummary.ClipsWithWarnings -gt 0) {
    throw "Real clip batch completed with $($batchSummary.ClipsWithWarnings) clip(s) over warning thresholds. See $reportPath"
}

if ($FailOnQualityGates -and $batchSummary.FailedQualityCheckCount -gt 0) {
    throw "Real clip batch failed $($batchSummary.FailedQualityCheckCount) quality gate(s). See $reportPath"
}
