# Wizionic Windows installer
#
# Inspect first:
#   https://github.com/Wizionic/wizionic/blob/main/scripts/install.ps1
#   https://github.com/Wizionic/wizionic/releases/latest
#
#   irm https://wizionic.com/install.ps1 | iex
#
# Same script from GitHub Releases (once published as an asset):
#   irm https://github.com/Wizionic/wizionic/releases/latest/download/install.ps1 | iex
#
# Or download, read, then run:
#   iwr -useb https://wizionic.com/install.ps1 -OutFile install.ps1
#   notepad install.ps1
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
#
# What this script does:
#   1. Downloads Wizionic-win-Setup.exe and SHA256SUMS from GitHub Releases
#   2. Verifies SHA256
#   3. Unblock-File (clears Mark-of-the-Web if the download tagged the file)
#   4. Runs the Velopack per-user installer (no administrator)
#
# This is not a Defender bypass. An unsigned Setup.exe can still be flagged.
# Override the asset base with WIZIONIC_INSTALL_BASE if you are testing.

$ErrorActionPreference = "Stop"

$RepoLatestDownload = "https://github.com/Wizionic/wizionic/releases/latest/download"
$AssetName = "Wizionic-win-Setup.exe"
$SumsName = "SHA256SUMS"

$BaseUrl = $RepoLatestDownload
if (-not [string]::IsNullOrWhiteSpace($env:WIZIONIC_INSTALL_BASE)) {
    $BaseUrl = $env:WIZIONIC_INSTALL_BASE.TrimEnd("/")
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    # Older hosts may already be on TLS 1.2; continue and let the request fail clearly.
}

function Get-WizionicReleaseFile {
    param(
        [Parameter(Mandatory = $true)][string] $FileName,
        [Parameter(Mandatory = $true)][string] $OutFile
    )
    $uri = "$BaseUrl/$FileName"
    Write-Host "Downloading $uri"
    Invoke-WebRequest -Uri $uri -OutFile $OutFile -UseBasicParsing
    if (-not (Test-Path -LiteralPath $OutFile)) {
        throw "Download failed: $uri"
    }
}

function Get-ExpectedSha256 {
    param(
        [Parameter(Mandatory = $true)][string] $SumsPath,
        [Parameter(Mandatory = $true)][string] $FileName
    )
    $escaped = [regex]::Escape($FileName)
    $line = Get-Content -LiteralPath $SumsPath | Where-Object { $_ -match "(^|\s)$escaped`$" } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "$SumsName does not list $FileName. Check $BaseUrl/$SumsName"
    }
    if ($line -notmatch "^[0-9A-Fa-f]{64}") {
        throw "Could not parse SHA256 for $FileName in $SumsName : $line"
    }
    return $Matches[0].ToLowerInvariant()
}

$workDir = Join-Path $env:TEMP "wizionic-install"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$exePath = Join-Path $workDir $AssetName
$sumsPath = Join-Path $workDir $SumsName

Write-Host "Installing Wizionic for the current user (no administrator)."
Get-WizionicReleaseFile -FileName $SumsName -OutFile $sumsPath
Get-WizionicReleaseFile -FileName $AssetName -OutFile $exePath

$expected = Get-ExpectedSha256 -SumsPath $sumsPath -FileName $AssetName
$actual = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) {
    throw "SHA256 mismatch for $AssetName.`n  expected $expected`n  actual   $actual"
}
Write-Host "SHA256 verified ($actual)"

Unblock-File -LiteralPath $exePath -ErrorAction SilentlyContinue

Write-Host "Launching $AssetName ..."
$proc = Start-Process -FilePath $exePath -Wait -PassThru
if ($null -ne $proc -and $null -ne $proc.ExitCode -and $proc.ExitCode -ne 0) {
    throw "Installer exited with code $($proc.ExitCode). Windows Security may have blocked the unsigned setup."
}

Write-Host "Done. Launch Wizionic from the Start menu if it did not open."
