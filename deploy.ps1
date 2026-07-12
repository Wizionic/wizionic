# ==============================================================================
# CHATFISH AUTOMATED DEPLOYMENT SCRIPT
# ==============================================================================
$SERVER_IP = "bg5.local"
$SSH_USER  = "daniel"
$OUTPUT_DIR = ".\publish_output"
$MAUI_OUTPUT  = ".\maui_publish"
$RELEASES_DIR = ".\maui_releases"
$VERSION      = "0.0.6"   # bump this before each release
$UPDATE_FEED  = "https://chatfish.me/releases/windows"
$WindowsBrevoKey = $env:BREVO_API_KEY

# ==============================================================================
# PART 1 — MAUI WINDOWS INSTALLER (Velopack)
# ==============================================================================
Write-Host "Building MAUI Windows Installer..." -ForegroundColor Cyan

if (Test-Path $MAUI_OUTPUT) { Remove-Item -Recurse -Force $MAUI_OUTPUT }
if (Test-Path $RELEASES_DIR) { Remove-Item -Recurse -Force $RELEASES_DIR }
New-Item -ItemType Directory -Force -Path $RELEASES_DIR | Out-Null

Write-Host "Downloading existing releases from $UPDATE_FEED ..." -ForegroundColor Cyan
vpk download http --url $UPDATE_FEED --outputDir $RELEASES_DIR

dotnet publish "ChatfishApp.Maui\ChatfishApp.Maui.csproj" `
    -c Release `
    -f net10.0-windows10.0.19041.0 `
    -p:RuntimeIdentifierOverride=win-x64 `
    -p:ApplicationDisplayVersion=$VERSION `
    -o $MAUI_OUTPUT

vpk pack `
    --packId "Chatfish" `
    --packTitle "Chatfish" `
    --packAuthors "Chatfish" `
    --packVersion $VERSION `
    --packDir $MAUI_OUTPUT `
    --mainExe Chatfish.exe `
    --outputDir $RELEASES_DIR

Write-Host "Uploading installer to chatfish.me..." -ForegroundColor Cyan

# SCP the release files into the releases folder on the server
scp -r "${RELEASES_DIR}\*" "${SSH_USER}@${SERVER_IP}:/var/www/chatfish/releases/windows/"

Write-Host "MAUI Installer deployed!" -ForegroundColor Green

# ==============================================================================
# PART 2 — SERVER BLAZOR APP (Docker)
# =============================================================================

Write-Host "Starting Production Build for Chatfish..." -ForegroundColor Cyan

# 1. Nuke any legacy local caches or mixed DLL states
if (Test-Path $OUTPUT_DIR) { Remove-Item -Recurse -Force $OUTPUT_DIR }

# 2. Compile a pristine, framework-dependent binary tree targeting Linux
# PublishTrimmed is set per project: host=false (EF), Client=true (WASM size).
# Do not pass /p:PublishTrimmed=... here — a global false would disable Client trim.
dotnet publish "ChatfishApp.csproj" `
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
ENTRYPOINT ["dotnet", "ChatfishApp.dll"]
"@
Out-File -FilePath "$OUTPUT_DIR\Dockerfile" -InputObject $DockerContent -Encoding ascii

Write-Host "Transferring clean build assets to M5 Server..." -ForegroundColor Cyan

# 4. Clear old deployment assets safely, explicitly preserving the 'data' directory
$SafeCleanCmd = "find /var/www/chatfish -mindepth 1 -maxdepth 1 ! -name 'data'  ! -name 'releases' -exec rm -rf {} +"
ssh "${SSH_USER}@${SERVER_IP}" $SafeCleanCmd

# 5. Fix the SCP path by wrapping the variable properly using curly braces to avoid the colon collision
scp -r "${OUTPUT_DIR}\*" "${SSH_USER}@${SERVER_IP}:/var/www/chatfish/"

Write-Host "Instructing Remote Docker Engine to assemble and spin up..." -ForegroundColor Cyan

# 6. Pass the remote commands as a single, single-quoted block to prevent PS from parsing bash symbols

$RemoteCmds = "cd /var/www/chatfish && " +
              "docker build -t chatfish-app:latest . && " +
              "docker rm -f chatfish || true && " +
              "docker run -d --name chatfish -p 5000:8080 " +
              "-e ASPNETCORE_ENVIRONMENT=Production " +
              "-e BREVO_API_KEY='$WindowsBrevoKey' " +
              "-v /var/www/chatfish/data:/app/data " +
              "-v /var/www/chatfish/releases:/app/wwwroot/releases " +
              "--restart unless-stopped chatfish-app:latest"


ssh "${SSH_USER}@${SERVER_IP}" $RemoteCmds

Write-Host "Deployment Complete! Chatfish is live at http://$SERVER_IP:5000" -ForegroundColor Green