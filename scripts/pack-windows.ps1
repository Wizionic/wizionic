# Pack Windows desktop (Velopack) + Windows homeserver zip. No production upload.
# Usage (from repo root):
#   powershell -File scripts/pack-windows.ps1 -Version 0.2.0
#   powershell -File scripts/pack-windows.ps1 -Version 0.2.0 -SkipDownload

param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [switch] $SkipDownload,
    [switch] $SkipHomeserver
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# MAUI Resizetizer requires a 3-part display version (0.2.0), not 0.2.0-rc.3.
$DisplayVersion = ($Version -split "-")[0]

$MauiOutput = ".\maui_publish"
$ReleasesDir = ".\maui_releases"
$UpdateFeed = "https://github.com/Wizionic/wizionic"
$HomeserverOutput = ".\homeserver_publish_win"
$HomeserverReleases = ".\homeserver_releases_win"

Write-Host "================================================================"
Write-Host " WIZIONIC WINDOWS PACK"
Write-Host " Version: $Version"
Write-Host "================================================================"

if (Test-Path $MauiOutput) { Remove-Item -Recurse -Force $MauiOutput }
if (Test-Path $ReleasesDir) { Remove-Item -Recurse -Force $ReleasesDir }
New-Item -ItemType Directory -Force -Path $ReleasesDir | Out-Null

if (-not $SkipDownload) {
    Write-Host "Downloading existing releases from $UpdateFeed ..."
    try {
        vpk download github --repoUrl $UpdateFeed --outputDir $ReleasesDir
    } catch {
        Write-Host "WARNING: could not download previous releases. Continuing without deltas."
    }
}

# WizionicPackTarget is a MAUI-only property so referenced projects keep net10.0.
$winTfm = "net10.0-windows10.0.19041.0"
dotnet restore "App.Maui\App.Maui.csproj" -p:WizionicPackTarget=windows
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish "App.Maui\App.Maui.csproj" `
    -c Release `
    -f $winTfm `
    -p:WizionicPackTarget=windows `
    -p:RuntimeIdentifierOverride=win-x64 `
    -p:ApplicationDisplayVersion=$DisplayVersion `
    -p:Version=$Version `
    -o $MauiOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Default Velopack Setup.exe is per-user (%LocalAppData%\Wizionic) — no admin.
# Do not add a machine-wide / Program Files flag: unsigned + UAC + MOTW is three warnings.
vpk pack `
    --packId "Wizionic" `
    --packTitle "Wizionic" `
    --packAuthors "Wizionic" `
    --packVersion $Version `
    --packDir $MauiOutput `
    --mainExe Wizionic.exe `
    --outputDir $ReleasesDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# install.ps1 (irm | iex). Checksums for the Setup.exe are SHA256SUMS on the GitHub Release.
$installScript = Join-Path $PSScriptRoot "install.ps1"
if (-not (Test-Path -LiteralPath $installScript)) {
    throw "scripts/install.ps1 is missing"
}
Copy-Item -LiteralPath $installScript -Destination (Join-Path $ReleasesDir "install.ps1") -Force
New-Item -ItemType Directory -Force -Path "wwwroot\releases\windows" | Out-Null
Copy-Item -LiteralPath $installScript -Destination "wwwroot\releases\windows\install.ps1" -Force
Copy-Item -LiteralPath $installScript -Destination "wwwroot\install.ps1" -Force
Write-Host "Wrote install.ps1 to $ReleasesDir"

if (-not $SkipHomeserver) {
    Write-Host "Building Windows Home Server package..."
    if (Test-Path $HomeserverOutput) { Remove-Item -Recurse -Force $HomeserverOutput }
    if (Test-Path $HomeserverReleases) { Remove-Item -Recurse -Force $HomeserverReleases }
    New-Item -ItemType Directory -Force -Path $HomeserverReleases | Out-Null

    dotnet publish "App.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $HomeserverOutput `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        /p:BlazorEnableCompression=true `
        /p:SelectBlazorWebAssemblyRazorConfiguration=Release `
        /p:BuildProjectReferences=true `
        /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $zipName = "homeserver-win-x64-$Version.zip"
    $zipPath = Join-Path $HomeserverReleases $zipName
    Compress-Archive -Path (Join-Path $HomeserverOutput "*") -DestinationPath $zipPath -CompressionLevel Optimal

    $sha256 = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = @{
        version  = $Version
        fileName = $zipName
        sha256   = $sha256
        url      = "https://github.com/Wizionic/wizionic/releases/download/v$Version/$zipName"
    } | ConvertTo-Json
    Set-Content -Path (Join-Path $HomeserverReleases "homeserver-win-latest.json") -Value $manifest -Encoding utf8
}

Write-Host "Done. Artifacts in $ReleasesDir"
if (-not $SkipHomeserver) {
    Write-Host "Homeserver artifacts in $HomeserverReleases"
}
