# Pack Windows desktop as an unsigned Store MSIX. Does not run vpk pack.
# Usage (from repo root):
#   powershell -File scripts/pack-windows-msix.ps1 -Version 0.2.25

param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# MAUI Resizetizer requires a 3-part display version (0.2.0), not 0.2.0-rc.3.
$DisplayVersion = ($Version -split "-")[0]
$PackageVersion = "$DisplayVersion.0"

$ExpectedName = "Wizionic.Wizionic"
$ExpectedPublisher = "CN=B7638B36-393C-411D-91A5-DCF5DAB35944"

$winTfm = "net10.0-windows10.0.19041.0"
$ReleasesDir = Join-Path $RepoRoot "msix_releases"

Write-Host "================================================================"
Write-Host " WIZIONIC WINDOWS STORE MSIX"
Write-Host " Version: $Version  (package $PackageVersion)"
Write-Host "================================================================"

if (Test-Path $ReleasesDir) { Remove-Item -Recurse -Force $ReleasesDir }
New-Item -ItemType Directory -Force -Path $ReleasesDir | Out-Null
# AppxPackageDir must end with a separator.
$AppxPackageDir = Join-Path $ReleasesDir ""

dotnet restore "App.Maui\App.Maui.csproj" -p:WizionicPackTarget=windows
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish "App.Maui\App.Maui.csproj" `
    -c Release `
    -f $winTfm `
    -p:WizionicPackTarget=windows `
    -p:STORE_BUILD=true `
    -p:RuntimeIdentifierOverride=win-x64 `
    -p:ApplicationDisplayVersion=$DisplayVersion `
    -p:ApplicationVersion=0 `
    -p:Version=$Version `
    -p:AppxPackageDir=$AppxPackageDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

function Get-ManifestIdentity {
    param([xml] $Xml)
    $id = $Xml.Package.Identity
    if ($null -eq $id) { throw "AppxManifest.xml has no Package/Identity." }
    return @{ Name = [string]$id.Name; Publisher = [string]$id.Publisher; Version = [string]$id.Version }
}

function Read-IdentityFromMsix {
    param([string] $Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" } | Select-Object -First 1
        if ($null -eq $entry) { throw "No AppxManifest.xml in $Path" }
        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            [xml]$xml = $reader.ReadToEnd()
            return Get-ManifestIdentity $xml
        } finally {
            $stream.Dispose()
        }
    } finally {
        $zip.Dispose()
    }
}

$packages = Get-ChildItem -Path $ReleasesDir -Recurse -File | Where-Object {
    $_.Extension -in ".msix", ".msixbundle", ".msixupload"
}
if (-not $packages) {
    throw "No .msix / .msixbundle / .msixupload produced under $ReleasesDir"
}

$identity = $null
$msix = $packages | Where-Object { $_.Extension -eq ".msix" } | Select-Object -First 1
$manifestFile = Get-ChildItem -Path $ReleasesDir -Recurse -Filter "AppxManifest.xml" | Select-Object -First 1
if ($manifestFile) {
    [xml]$xml = Get-Content -LiteralPath $manifestFile.FullName
    $identity = Get-ManifestIdentity $xml
} elseif ($msix) {
    $identity = Read-IdentityFromMsix $msix.FullName
}

if ($null -eq $identity) {
    throw "Could not read package identity from AppxManifest.xml or .msix"
}

Write-Host "Identity Name=$($identity.Name) Publisher=$($identity.Publisher) Version=$($identity.Version)"
if ($identity.Name -ne $ExpectedName) {
    throw "Package Identity Name '$($identity.Name)' != '$ExpectedName'"
}
if ($identity.Publisher -ne $ExpectedPublisher) {
    throw "Package Identity Publisher '$($identity.Publisher)' != '$ExpectedPublisher'"
}

Write-Host "Done. Store packages in $ReleasesDir"
$packages | ForEach-Object { Write-Host "  $($_.FullName)" }
