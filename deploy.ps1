# ==============================================================================
# WIZIONIC AUTOMATED DEPLOYMENT SCRIPT
# ==============================================================================
# Parallel install alongside chatfish: uses /var/www/wizionic and host port 5100.
# First run needs sudo once to create the deploy root (script bootstraps that).
# ==============================================================================
$SERVER_IP = "bg5.local"
$SSH_USER  = "daniel"
$REMOTE_ROOT = "/var/www/wizionic"
$OUTPUT_DIR = ".\publish_output"
$MAUI_OUTPUT  = ".\maui_publish"
$RELEASES_DIR = ".\maui_releases"
$VERSION      = "0.1.21"   # bump this before each release
$UPDATE_FEED  = "https://wizionic.com/releases/windows"
$WindowsBrevoKey = $env:BREVO_API_KEY
# OAuth secrets — set on the machine that runs deploy.ps1 (never commit these).
# ASP.NET Core maps OAuth__GitHub__ClientSecret → OAuth:GitHub:ClientSecret
$OAuthGitHubClientId     = $env:OAUTH_GITHUB_CLIENT_ID
$OAuthGitHubClientSecret = $env:OAUTH_GITHUB_CLIENT_SECRET
$OAuthGoogleClientId     = $env:OAUTH_GOOGLE_CLIENT_ID
$OAuthGoogleClientSecret = $env:OAUTH_GOOGLE_CLIENT_SECRET
$OAuthNotionClientId     = $env:OAUTH_NOTION_CLIENT_ID
$OAuthNotionClientSecret = $env:OAUTH_NOTION_CLIENT_SECRET
$OAuthStripeClientId     = $env:OAUTH_STRIPE_CLIENT_ID
$OAuthStripeClientSecret = $env:OAUTH_STRIPE_CLIENT_SECRET

# Ensure remote tree exists and is owned by the deploy user (idempotent; may prompt for sudo).
# IMPORTANT: keep this as ONE line for ssh - multi-line PowerShell strings inject CRLF and break bash.
function Ensure-RemoteDeployRoot {
    Write-Host "Ensuring remote deploy root $REMOTE_ROOT (sudo may prompt)..." -ForegroundColor Cyan
    $dirs = @(
        "$REMOTE_ROOT/data"
        "$REMOTE_ROOT/releases/windows"
        "$REMOTE_ROOT/releases/linux"
        "$REMOTE_ROOT/releases/homeserver/windows"
        "$REMOTE_ROOT/releases/homeserver/linux"
    ) -join " "
    # -t: TTY for sudo password. Single-quoted remote script avoids PowerShell expanding $ vars remotely.
    $bootstrap = "sudo mkdir -p $dirs && sudo chown -R ${SSH_USER}:${SSH_USER} $REMOTE_ROOT"
    ssh -t "${SSH_USER}@${SERVER_IP}" $bootstrap
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "Remote bootstrap failed. On the server run once:" -ForegroundColor Red
        Write-Host "  sudo mkdir -p $dirs" -ForegroundColor Yellow
        Write-Host "  sudo chown -R ${SSH_USER}:${SSH_USER} $REMOTE_ROOT" -ForegroundColor Yellow
        Write-Host "Then re-run this script." -ForegroundColor Red
        exit 1
    }
}

# ==============================================================================
# PART 0 - Remote directories (new site; does not touch /var/www/chatfish)
# ==============================================================================
Ensure-RemoteDeployRoot

# ==============================================================================
# PART 1 - MAUI WINDOWS INSTALLER (Velopack)
# ==============================================================================
Write-Host "Building MAUI Windows Installer..." -ForegroundColor Cyan

if (Test-Path $MAUI_OUTPUT) { Remove-Item -Recurse -Force $MAUI_OUTPUT }
if (Test-Path $RELEASES_DIR) { Remove-Item -Recurse -Force $RELEASES_DIR }
New-Item -ItemType Directory -Force -Path $RELEASES_DIR | Out-Null

Write-Host "Downloading existing releases from $UPDATE_FEED ..." -ForegroundColor Cyan
vpk download http --url $UPDATE_FEED --outputDir $RELEASES_DIR

dotnet publish "App.Maui\App.Maui.csproj" `
    -c Release `
    -f net10.0-windows10.0.19041.0 `
    -p:RuntimeIdentifierOverride=win-x64 `
    -p:ApplicationDisplayVersion=$VERSION `
    -o $MAUI_OUTPUT

vpk pack `
    --packId "Wizionic" `
    --packTitle "Wizionic" `
    --packAuthors "Wizionic" `
    --packVersion $VERSION `
    --packDir $MAUI_OUTPUT `
    --mainExe Wizionic.exe `
    --outputDir $RELEASES_DIR

Write-Host "Uploading installer to wizionic.com..." -ForegroundColor Cyan
scp -r "${RELEASES_DIR}\*" "${SSH_USER}@${SERVER_IP}:${REMOTE_ROOT}/releases/windows/"

Write-Host "MAUI Installer deployed!" -ForegroundColor Green

# ==============================================================================
# PART 1b - WINDOWS HOMESERVER PACKAGE (self-contained host + WASM)
# Optional install from MAUI first-run / Settings. Does not change production Docker.
# ==============================================================================
Write-Host "Building Windows Home Server package..." -ForegroundColor Cyan

$HOMESERVER_OUTPUT = ".\homeserver_publish_win"
$HOMESERVER_RELEASES = ".\homeserver_releases_win"

if (Test-Path $HOMESERVER_OUTPUT) { Remove-Item -Recurse -Force $HOMESERVER_OUTPUT }
if (Test-Path $HOMESERVER_RELEASES) { Remove-Item -Recurse -Force $HOMESERVER_RELEASES }
New-Item -ItemType Directory -Force -Path $HOMESERVER_RELEASES | Out-Null

dotnet publish "App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $HOMESERVER_OUTPUT `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:BlazorEnableCompression=true `
    /p:SelectBlazorWebAssemblyRazorConfiguration=Release `
    /p:BuildProjectReferences=true

$zipName = "homeserver-win-x64-$VERSION.zip"
$zipPath = Join-Path $HOMESERVER_RELEASES $zipName
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $HOMESERVER_OUTPUT "*") -DestinationPath $zipPath -CompressionLevel Optimal

$sha256 = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = @{
    version  = $VERSION
    fileName = $zipName
    sha256   = $sha256
    url      = "https://wizionic.com/releases/homeserver/windows/$zipName"
} | ConvertTo-Json
Set-Content -Path (Join-Path $HOMESERVER_RELEASES "latest.json") -Value $manifest -Encoding utf8

Write-Host "Uploading Home Server package to wizionic.com..." -ForegroundColor Cyan
scp -r "${HOMESERVER_RELEASES}\*" "${SSH_USER}@${SERVER_IP}:${REMOTE_ROOT}/releases/homeserver/windows/"

Write-Host "Home Server package deployed!" -ForegroundColor Green

# ==============================================================================
# PART 2 - SERVER BLAZOR APP (Docker)
# =============================================================================

Write-Host "Starting Production Build for Wizionic..." -ForegroundColor Cyan

# 1. Nuke any legacy local caches or mixed DLL states
if (Test-Path $OUTPUT_DIR) { Remove-Item -Recurse -Force $OUTPUT_DIR }

# 2. Compile a pristine, framework-dependent binary tree targeting Linux
# PublishTrimmed is set per project: host=false (EF), Client=true (WASM size).
# Do not pass /p:PublishTrimmed=... here - a global false would disable Client trim.
dotnet publish "App.csproj" `
    -c Release `
    -o $OUTPUT_DIR `
    --runtime linux-x64 `
    --self-contained false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:BlazorEnableCompression=true `
    /p:SelectBlazorWebAssemblyRazorConfiguration=Release `
    /p:BuildProjectReferences=true

# 3. Inject a perfectly tailored, slim production Dockerfile right into the output directory
$DockerContent = @"
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY . .
ENTRYPOINT ["dotnet", "App.dll"]
"@
Out-File -FilePath "$OUTPUT_DIR\Dockerfile" -InputObject $DockerContent -Encoding ascii

Write-Host "Transferring clean build assets to M5 Server..." -ForegroundColor Cyan

# 4. Clear old deployment assets safely, explicitly preserving the 'data' and 'releases' directories
$SafeCleanCmd = "if [ -d '$REMOTE_ROOT' ]; then find '$REMOTE_ROOT' -mindepth 1 -maxdepth 1 ! -name 'data' ! -name 'releases' -exec rm -rf {} +; fi"
ssh "${SSH_USER}@${SERVER_IP}" $SafeCleanCmd

# 5. Fix the SCP path by wrapping the variable properly using curly braces to avoid the colon collision
scp -r "${OUTPUT_DIR}\*" "${SSH_USER}@${SERVER_IP}:${REMOTE_ROOT}/"

Write-Host "Instructing Remote Docker Engine to assemble and spin up..." -ForegroundColor Cyan

# 6. Pass the remote commands as a single block. Does not touch the chatfish container.
# Env vars for secrets (double-underscore = nested config in ASP.NET Core).
# Client IDs are public-ish but we still pass them the same way for consistency.
$RemoteCmds = "cd '$REMOTE_ROOT' && " +
              "docker build -t wizionic-app:latest . && " +
              "docker rm -f wizionic || true && " +
              "docker run -d --name wizionic -p 5100:8080 " +
              "-e ASPNETCORE_ENVIRONMENT=Production " +
              "-e BREVO_API_KEY='$WindowsBrevoKey' " +
              "-e OAuth__GitHub__ClientId='$OAuthGitHubClientId' " +
              "-e OAuth__GitHub__ClientSecret='$OAuthGitHubClientSecret' " +
              "-e OAuth__GitHub__RedirectUri='https://wizionic.com/api/oauth/github/callback' " +
              "-e OAuth__Google__ClientId='$OAuthGoogleClientId' " +
              "-e OAuth__Google__ClientSecret='$OAuthGoogleClientSecret' " +
              "-e OAuth__Google__RedirectUri='https://wizionic.com/api/oauth/google/callback' " +
              "-e OAuth__Notion__ClientId='$OAuthNotionClientId' " +
              "-e OAuth__Notion__ClientSecret='$OAuthNotionClientSecret' " +
              "-e OAuth__Notion__RedirectUri='https://wizionic.com/api/oauth/notion/callback' " +
              "-e OAuth__Stripe__ClientId='$OAuthStripeClientId' " +
              "-e OAuth__Stripe__ClientSecret='$OAuthStripeClientSecret' " +
              "-e OAuth__Stripe__RedirectUri='https://wizionic.com/api/oauth/stripe/callback' " +
              "-v ${REMOTE_ROOT}/data:/app/data " +
              "-v ${REMOTE_ROOT}/releases:/app/wwwroot/releases " +
              "--restart unless-stopped wizionic-app:latest"

ssh "${SSH_USER}@${SERVER_IP}" $RemoteCmds
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker deploy failed (exit $LASTEXITCODE). Check docker permissions and logs on the server." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Deployment Complete! Wizionic is live at http://${SERVER_IP}:5100" -ForegroundColor Green
Write-Host "(chatfish remains on port 5000 / /var/www/chatfish - untouched)" -ForegroundColor DarkGray