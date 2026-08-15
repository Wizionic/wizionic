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

$MauiOutput = ".\maui_publish"
$ReleasesDir = ".\maui_releases"
$UpdateFeed = "https://wizionic.com/releases/windows"
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
        vpk download http --url $UpdateFeed --outputDir $ReleasesDir
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
    -p:ApplicationDisplayVersion=$Version `
    -p:Version=$Version `
    -o $MauiOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

vpk pack `
    --packId "Wizionic" `
    --packTitle "Wizionic" `
    --packAuthors "Wizionic" `
    --packVersion $Version `
    --packDir $MauiOutput `
    --mainExe Wizionic.exe `
    --outputDir $ReleasesDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

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
        url      = "https://wizionic.com/releases/homeserver/windows/$zipName"
    } | ConvertTo-Json
    Set-Content -Path (Join-Path $HomeserverReleases "latest.json") -Value $manifest -Encoding utf8
}

Write-Host "Done. Artifacts in $ReleasesDir"
if (-not $SkipHomeserver) {
    Write-Host "Homeserver artifacts in $HomeserverReleases"
}
