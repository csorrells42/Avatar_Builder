param(
    [string]$ShortcutName = "Avatar Builder.lnk",
    [string]$TargetPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedTargetPath = if ([string]::IsNullOrWhiteSpace($TargetPath)) {
    Join-Path $repoRoot "desktop-runtime\AvatarBuilder.exe"
}
else {
    [System.IO.Path]::GetFullPath($TargetPath)
}

if (-not (Test-Path -LiteralPath $resolvedTargetPath -PathType Leaf)) {
    Push-Location $repoRoot
    try {
        & dotnet build .\AvatarBuilder.csproj --no-restore /p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) {
            throw "Avatar Builder build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$desktopPath = [Environment]::GetFolderPath("Desktop")
if ([string]::IsNullOrWhiteSpace($desktopPath)) {
    throw "Windows did not return a desktop folder path."
}

$shortcutPath = Join-Path $desktopPath $ShortcutName
$explorerPath = Join-Path $env:WINDIR "explorer.exe"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $explorerPath
$shortcut.Arguments = "`"$resolvedTargetPath`""
$shortcut.WorkingDirectory = Split-Path -Parent $resolvedTargetPath
$shortcut.Description = "Launch the latest successfully built Avatar Builder runtime."
$shortcut.Save()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)

Write-Output $shortcutPath
