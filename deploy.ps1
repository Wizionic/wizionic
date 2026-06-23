# ==============================================================================
# CHATFISH AUTOMATED DEPLOYMENT SCRIPT
# ==============================================================================
$SERVER_IP = "192.168.4.230"
$SSH_USER  = "daniel"
$OUTPUT_DIR = ".\publish_output"
$WindowsBrevoKey = $env:BREVO_API_KEY

Write-Host "Starting Production Build for Chatfish..." -ForegroundColor Cyan

# 1. Nuke any legacy local caches or mixed DLL states
if (Test-Path $OUTPUT_DIR) { Remove-Item -Recurse -Force $OUTPUT_DIR }

# 2. Compile a pristine, framework-dependent binary tree targeting Linux
dotnet publish "ChatfishApp.csproj" `
    -c Release `
    -o $OUTPUT_DIR `
    --runtime linux-x64 `
    --self-contained false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:PublishTrimmed=false `
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

# 4. Clear the remote host directory using a semicolon instead of && (safe for both shells)
ssh "${SSH_USER}@${SERVER_IP}" "rm -rf /var/www/chatfish/* ; mkdir -p /var/www/chatfish"

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
              "--restart unless-stopped chatfish-app:latest"


ssh "${SSH_USER}@${SERVER_IP}" $RemoteCmds

Write-Host "Deployment Complete! Chatfish is live at http://$SERVER_IP:5000" -ForegroundColor Green