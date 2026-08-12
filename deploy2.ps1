# ==============================================================================
# WIZIONIC AUTOMATED DEPLOYMENT SCRIPT
# ==============================================================================
# Parallel install alongside chatfish: uses /var/www/wizionic and host port 5100.
#
# SSH auth on Windows:
#   OpenSSH ControlMaster is broken on Windows ("getsockname failed: Not a socket").
#   This script instead:
#     1) Tries key/agent auth (BatchMode) - no password if keys work.
#     2) Else prompts once and reuses the password via SSH_ASKPASS for this run.
#   Best: install a key so you never type a password:
#     type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh daniel@bg5.local "cat >> ~/.ssh/authorized_keys"
# ==============================================================================
$ErrorActionPreference = 'Stop'

$SERVER_IP = 'bg5.local'
$SSH_USER = 'daniel'
$REMOTE_ROOT = '/var/www/wizionic'
# Keep all publish outputs under artifacts/ (gitignored) so MSBuild never re-includes them.
$ARTIFACTS = Join-Path $PSScriptRoot 'artifacts'
$OUTPUT_DIR = Join-Path $ARTIFACTS 'linux-publish'
$MAUI_OUTPUT = Join-Path $ARTIFACTS 'maui-publish'
$RELEASES_DIR = Join-Path $ARTIFACTS 'maui-releases'
$HOMESERVER_OUTPUT = Join-Path $ARTIFACTS 'homeserver-win-publish'
$HOMESERVER_RELEASES = Join-Path $ARTIFACTS 'homeserver-win-releases'
$VERSION = '0.1.17'
$UPDATE_FEED = 'https://wizionic.com/releases/windows'
$WindowsBrevoKey = $env:BREVO_API_KEY
# OAuth secrets — set on the machine that runs deploy (never commit).
# ASP.NET Core: OAuth__GitHub__ClientSecret → OAuth:GitHub:ClientSecret
$OAuthGitHubClientId     = $env:OAUTH_GITHUB_CLIENT_ID
$OAuthGitHubClientSecret = $env:OAUTH_GITHUB_CLIENT_SECRET
$OAuthGoogleClientId     = $env:OAUTH_GOOGLE_CLIENT_ID
$OAuthGoogleClientSecret = $env:OAUTH_GOOGLE_CLIENT_SECRET
$OAuthNotionClientId     = $env:OAUTH_NOTION_CLIENT_ID
$OAuthNotionClientSecret = $env:OAUTH_NOTION_CLIENT_SECRET
$OAuthStripeClientId     = $env:OAUTH_STRIPE_CLIENT_ID
$OAuthStripeClientSecret = $env:OAUTH_STRIPE_CLIENT_SECRET
$SSH_TARGET = $SSH_USER + '@' + $SERVER_IP

function Remove-DirForce {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (Test-Path -LiteralPath $Path) {
        Write-Host ('Cleaning ' + $Path + ' ...') -ForegroundColor DarkGray
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Initialize-ArtifactDirs {
    # Wipe legacy in-repo publish folders that previously nested into each other (MAX_PATH).
    $legacy = @(
        (Join-Path $PSScriptRoot 'publish_output'),
        (Join-Path $PSScriptRoot 'maui_publish'),
        (Join-Path $PSScriptRoot 'maui_releases'),
        (Join-Path $PSScriptRoot 'homeserver_publish_win'),
        (Join-Path $PSScriptRoot 'homeserver_releases_win')
    )
    foreach ($p in $legacy) { Remove-DirForce $p }

    # Fresh artifacts tree for this run
    Remove-DirForce $ARTIFACTS
    New-Item -ItemType Directory -Force -Path $ARTIFACTS | Out-Null
}

$script:AskPassDir = $null
$script:AskPassPath = $null
$script:PrevAskPass = $null
$script:PrevAskPassRequire = $null
$script:PrevDisplay = $null

function Get-SshCommonArgs {
    return @(
        '-o', 'ServerAliveInterval=30',
        '-o', 'ServerAliveCountMax=4',
        '-o', 'StrictHostKeyChecking=accept-new'
    )
}

function Invoke-Ssh {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RemoteCommand,
        [switch]$AllocateTty
    )
    $sshArgs = New-Object System.Collections.Generic.List[string]
    foreach ($a in (Get-SshCommonArgs)) { [void]$sshArgs.Add($a) }
    if ($AllocateTty) { [void]$sshArgs.Add('-tt') }
    [void]$sshArgs.Add($SSH_TARGET)
    [void]$sshArgs.Add($RemoteCommand)
    & ssh $sshArgs.ToArray()
    return $LASTEXITCODE
}

function Invoke-Scp {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ScpArgs
    )
    $all = New-Object System.Collections.Generic.List[string]
    foreach ($a in (Get-SshCommonArgs)) { [void]$all.Add($a) }
    foreach ($a in $ScpArgs) { [void]$all.Add($a) }
    & scp $all.ToArray()
    return $LASTEXITCODE
}

function Invoke-ScpExpanded {
    # Windows OpenSSH scp does not expand globs like publish_output\* when passed as one arg.
    # Expand locally, then scp each top-level item into the remote directory.
    param(
        [Parameter(Mandatory = $true)]
        [string]$LocalDir,
        [Parameter(Mandatory = $true)]
        [string]$RemoteDir
    )
    if (-not (Test-Path -LiteralPath $LocalDir)) {
        throw ('Local dir not found: ' + $LocalDir)
    }
    $resolved = (Resolve-Path -LiteralPath $LocalDir).Path
    $items = @(Get-ChildItem -LiteralPath $resolved -Force)
    if ($items.Count -eq 0) {
        throw ('Local dir is empty: ' + $resolved)
    }
    $fail = 0
    foreach ($item in $items) {
        $code = Invoke-Scp -ScpArgs @('-r', $item.FullName, ($SSH_TARGET + ':' + $RemoteDir + '/'))
        if ($code -ne 0) {
            Write-Host ('scp failed for ' + $item.Name + ' (exit ' + $code + ')') -ForegroundColor Yellow
            $fail = $code
        }
    }
    return $fail
}

function Invoke-PublishUpload {
    # Do NOT pipe tar through PowerShell (| ssh) — PS corrupts binary streams
    # ("Skipping to next header" / "lone zero block"). Write a local archive, scp it, extract remotely.
    param(
        [Parameter(Mandatory = $true)]
        [string]$LocalDir,
        [Parameter(Mandatory = $true)]
        [string]$RemoteDir
    )
    $resolved = (Resolve-Path -LiteralPath $LocalDir).Path
    $dockerfile = Join-Path $resolved 'Dockerfile'
    if (-not (Test-Path -LiteralPath $dockerfile)) {
        throw ('Dockerfile missing before upload: ' + $dockerfile)
    }
    $len = (Get-Item -LiteralPath $dockerfile).Length
    if ($len -lt 20) {
        throw ('Dockerfile looks empty (' + $len + ' bytes) at ' + $dockerfile)
    }

    $tarLocal = Join-Path $env:TEMP ('wizionic-publish-' + [Guid]::NewGuid().ToString('N') + '.tar')
    $tarRemote = '/tmp/wizionic-publish-deploy.tar'
    Write-Host ('Packing publish tree to ' + $tarLocal + ' (Dockerfile ' + $len + ' bytes)...') -ForegroundColor DarkGray

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if (Test-Path -LiteralPath $tarLocal) {
            Remove-Item -LiteralPath $tarLocal -Force -ErrorAction SilentlyContinue
        }
        # ustar is widely readable by GNU tar on Linux
        & tar -C $resolved --format=ustar -cf $tarLocal .
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tarLocal)) {
            if (Test-Path -LiteralPath $tarLocal) {
                Remove-Item -LiteralPath $tarLocal -Force -ErrorAction SilentlyContinue
            }
            # Fallback without format flag (older bsdtar)
            & tar -C $resolved -cf $tarLocal .
        }
        if (-not (Test-Path -LiteralPath $tarLocal) -or ((Get-Item -LiteralPath $tarLocal).Length -lt 100)) {
            throw 'Failed to create local publish tar archive.'
        }
        $tarLen = (Get-Item -LiteralPath $tarLocal).Length
        Write-Host ('Uploading archive (' + $tarLen + ' bytes) via scp...') -ForegroundColor DarkGray

        $code = Invoke-Scp -ScpArgs @($tarLocal, ($SSH_TARGET + ':' + $tarRemote))
        if ($code -ne 0) {
            return $code
        }

        $extract = (
            'mkdir -p ' + "'" + $RemoteDir + "'" +
            ' && tar -C ' + "'" + $RemoteDir + "'" + ' -xf ' + $tarRemote +
            ' && rm -f ' + $tarRemote +
            ' && test -s ' + "'" + $RemoteDir + '/Dockerfile' + "'"
        )
        $code = Invoke-Ssh -RemoteCommand $extract
        return $code
    } finally {
        $ErrorActionPreference = $prevEap
        if (Test-Path -LiteralPath $tarLocal) {
            Remove-Item -LiteralPath $tarLocal -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-SshKeyAuth {
    # Key probe must not throw: $ErrorActionPreference=Stop turns ssh stderr into a terminating error.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $probeArgs = New-Object System.Collections.Generic.List[string]
        foreach ($a in (Get-SshCommonArgs)) { [void]$probeArgs.Add($a) }
        [void]$probeArgs.Add('-o'); [void]$probeArgs.Add('BatchMode=yes')
        [void]$probeArgs.Add('-o'); [void]$probeArgs.Add('ConnectTimeout=8')
        [void]$probeArgs.Add('-o'); [void]$probeArgs.Add('NumberOfPasswordPrompts=0')
        [void]$probeArgs.Add($SSH_TARGET)
        [void]$probeArgs.Add('true')
        $null = & ssh $probeArgs.ToArray() 1>$null 2>$null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    } finally {
        $ErrorActionPreference = $prevEap
    }
}

function Clear-SshAuth {
    if ($script:PrevAskPass) {
        $env:SSH_ASKPASS = $script:PrevAskPass
    } else {
        Remove-Item Env:\SSH_ASKPASS -ErrorAction SilentlyContinue
    }
    if ($script:PrevAskPassRequire) {
        $env:SSH_ASKPASS_REQUIRE = $script:PrevAskPassRequire
    } else {
        Remove-Item Env:\SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue
    }
    if ($null -ne $script:PrevDisplay) {
        $env:DISPLAY = $script:PrevDisplay
    }

    if ($script:AskPassDir -and (Test-Path -LiteralPath $script:AskPassDir)) {
        Remove-Item -LiteralPath $script:AskPassDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:AskPassDir = $null
    $script:AskPassPath = $null
}

function Initialize-SshAuth {
    Write-Host ('Checking SSH auth to ' + $SSH_TARGET + ' ...') -ForegroundColor Cyan
    if (Test-SshKeyAuth) {
        Write-Host 'SSH key/agent auth OK - no password needed.' -ForegroundColor Green
        return
    }

    Write-Host 'Key auth not available. Enter SSH password once for this deploy run.' -ForegroundColor Yellow
    $secure = Read-Host -Prompt ('Password for ' + $SSH_TARGET) -AsSecureString
    if (-not $secure -or $secure.Length -eq 0) {
        Write-Host 'No password entered.' -ForegroundColor Red
        exit 1
    }

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    $script:AskPassDir = Join-Path $env:TEMP ('wizionic-askpass-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $script:AskPassDir | Out-Null

    $passFile = Join-Path $script:AskPassDir 'p.txt'
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($passFile, $plain, $utf8)
    $plain = $null
    $secure = $null

    $askPs1 = Join-Path $script:AskPassDir 'askpass.ps1'
    $ps1 = @(
        '$p = Join-Path $PSScriptRoot ''p.txt'''
        '[Console]::Out.Write([IO.File]::ReadAllText($p))'
    )
    [System.IO.File]::WriteAllLines($askPs1, $ps1)

    $script:AskPassPath = Join-Path $script:AskPassDir 'askpass.cmd'
    $cmd = @(
        '@echo off'
        ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $askPs1 + '"')
    )
    [System.IO.File]::WriteAllLines($script:AskPassPath, $cmd)

    $script:PrevAskPass = $env:SSH_ASKPASS
    $script:PrevAskPassRequire = $env:SSH_ASKPASS_REQUIRE
    $script:PrevDisplay = $env:DISPLAY

    $env:SSH_ASKPASS = $script:AskPassPath
    $env:SSH_ASKPASS_REQUIRE = 'force'
    if (-not $env:DISPLAY) {
        $env:DISPLAY = 'localhost:0'
    }

    Write-Host 'SSH password cached for this run only (cleared on exit).' -ForegroundColor DarkGray

    $probe = New-Object System.Collections.Generic.List[string]
    foreach ($a in (Get-SshCommonArgs)) { [void]$probe.Add($a) }
    [void]$probe.Add('-o'); [void]$probe.Add('ConnectTimeout=15')
    [void]$probe.Add($SSH_TARGET)
    [void]$probe.Add('true')
    & ssh $probe.ToArray()
    if ($LASTEXITCODE -ne 0) {
        Write-Host ('SSH still failed after password helper (exit ' + $LASTEXITCODE + ').') -ForegroundColor Red
        Clear-SshAuth
        exit $LASTEXITCODE
    }
    Write-Host 'SSH OK.' -ForegroundColor Green
}

function Ensure-RemoteDeployRoot {
    # Never use ssh -tt + interactive sudo here: with SSH_ASKPASS it hangs waiting for a
    # TTY password you never see. Prefer non-interactive checks / sudo -n / sudo -S.
    Write-Host ('Ensuring remote deploy root ' + $REMOTE_ROOT + ' ...') -ForegroundColor Cyan

    $subdirs = @(
        ($REMOTE_ROOT + '/data'),
        ($REMOTE_ROOT + '/releases/windows'),
        ($REMOTE_ROOT + '/releases/linux'),
        ($REMOTE_ROOT + '/releases/homeserver/windows'),
        ($REMOTE_ROOT + '/releases/homeserver/linux')
    )
    $dirsJoined = [string]::Join(' ', $subdirs)

    # 1) Already writable and present? Done.
    $checkCmd = 'test -d ' + "'" + $REMOTE_ROOT + "'" + ' -a -w ' + "'" + $REMOTE_ROOT + "'" + ' -a -d ' + "'" + $REMOTE_ROOT + '/data' + "'" + ' -a -d ' + "'" + $REMOTE_ROOT + '/releases/windows' + "'"
    $code = Invoke-Ssh -RemoteCommand $checkCmd
    if ($code -eq 0) {
        Write-Host 'Deploy root already OK (writable).' -ForegroundColor DarkGray
        return
    }

    # 2) Try as deploy user without sudo (works if parent is writable or root pre-created).
    $userMk = 'mkdir -p ' + $dirsJoined
    $code = Invoke-Ssh -RemoteCommand $userMk
    if ($code -eq 0) {
        $code = Invoke-Ssh -RemoteCommand $checkCmd
        if ($code -eq 0) {
            Write-Host 'Deploy root created without sudo.' -ForegroundColor DarkGray
            return
        }
    }

    # 3) Passwordless sudo (NOPASSWD) if configured.
    $sudoN = 'sudo -n mkdir -p ' + $dirsJoined + '; sudo -n chown -R ' + $SSH_USER + ':' + $SSH_USER + ' ' + $REMOTE_ROOT
    $code = Invoke-Ssh -RemoteCommand $sudoN
    if ($code -eq 0) {
        Write-Host 'Deploy root created with passwordless sudo.' -ForegroundColor DarkGray
        return
    }

    # 4) One-shot sudo via stdin (no TTY — avoids hang with SSH_ASKPASS).
    Write-Host 'Need sudo on the server to create /var/www/wizionic.' -ForegroundColor Yellow
    Write-Host 'Enter the remote sudo password (often the same as SSH). Leave blank to print manual steps and exit.' -ForegroundColor Yellow
    $sudoSecure = Read-Host -Prompt 'Remote sudo password' -AsSecureString
    if (-not $sudoSecure -or $sudoSecure.Length -eq 0) {
        Write-Host ''
        Write-Host 'On the server run once, then re-run deploy.ps1:' -ForegroundColor Red
        Write-Host ('  sudo mkdir -p ' + $dirsJoined) -ForegroundColor Yellow
        Write-Host ('  sudo chown -R ' + $SSH_USER + ':' + $SSH_USER + ' ' + $REMOTE_ROOT) -ForegroundColor Yellow
        Clear-SshAuth
        exit 1
    }

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sudoSecure)
    try {
        $sudoPlain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    # Base64 avoids shell-quoting hell for special characters in the sudo password
    $sudoB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sudoPlain))
    $sudoPlain = $null
    $sudoSecure = $null

    $sudoS = (
        'PW=$(echo ' + $sudoB64 + ' | base64 -d); ' +
        'echo "$PW" | sudo -S -p "" mkdir -p ' + $dirsJoined + '; ' +
        'echo "$PW" | sudo -S -p "" chown -R ' + $SSH_USER + ':' + $SSH_USER + ' ' + $REMOTE_ROOT + '; ' +
        'unset PW'
    )
    $sudoB64 = $null

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $code = Invoke-Ssh -RemoteCommand $sudoS
    } finally {
        $ErrorActionPreference = $prevEap
    }

    if ($code -ne 0) {
        Write-Host ''
        Write-Host 'Remote bootstrap with sudo failed. On the server run once:' -ForegroundColor Red
        Write-Host ('  sudo mkdir -p ' + $dirsJoined) -ForegroundColor Yellow
        Write-Host ('  sudo chown -R ' + $SSH_USER + ':' + $SSH_USER + ' ' + $REMOTE_ROOT) -ForegroundColor Yellow
        Clear-SshAuth
        exit 1
    }

    Write-Host 'Deploy root ready.' -ForegroundColor DarkGray
}

Initialize-ArtifactDirs
Initialize-SshAuth
try {

# ==============================================================================
# PART 0 - Remote directories
# ==============================================================================
Ensure-RemoteDeployRoot

# ==============================================================================
# PART 1 - MAUI WINDOWS INSTALLER (Velopack)
# ==============================================================================
Write-Host 'Building MAUI Windows Installer...' -ForegroundColor Cyan

Remove-DirForce $MAUI_OUTPUT
Remove-DirForce $RELEASES_DIR
New-Item -ItemType Directory -Force -Path $RELEASES_DIR | Out-Null

Write-Host ('Downloading existing releases from ' + $UPDATE_FEED + ' ...') -ForegroundColor Cyan
vpk download http --url $UPDATE_FEED --outputDir $RELEASES_DIR

dotnet publish 'App.Maui\App.Maui.csproj' `
    -c Release `
    -f net10.0-windows10.0.19041.0 `
    -p:RuntimeIdentifierOverride=win-x64 `
    -p:ApplicationDisplayVersion=$VERSION `
    -o $MAUI_OUTPUT
if ($LASTEXITCODE -ne 0) { throw ('MAUI publish failed (exit ' + $LASTEXITCODE + ')') }

vpk pack `
    --packId 'Wizionic' `
    --packTitle 'Wizionic' `
    --packAuthors 'Wizionic' `
    --packVersion $VERSION `
    --packDir $MAUI_OUTPUT `
    --mainExe Wizionic.exe `
    --outputDir $RELEASES_DIR

Write-Host 'Uploading installer to wizionic.com...' -ForegroundColor Cyan
$code = Invoke-ScpExpanded -LocalDir $RELEASES_DIR -RemoteDir ($REMOTE_ROOT + '/releases/windows')
if ($code -ne 0) { throw ('scp windows releases failed (exit ' + $code + ')') }

Write-Host 'MAUI Installer deployed!' -ForegroundColor Green

# ==============================================================================
# PART 1b - WINDOWS HOMESERVER PACKAGE
# ==============================================================================
Write-Host 'Building Windows Home Server package...' -ForegroundColor Cyan

Remove-DirForce $HOMESERVER_OUTPUT
Remove-DirForce $HOMESERVER_RELEASES
New-Item -ItemType Directory -Force -Path $HOMESERVER_RELEASES | Out-Null

dotnet publish 'App.csproj' `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $HOMESERVER_OUTPUT `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:BlazorEnableCompression=true `
    /p:SelectBlazorWebAssemblyRazorConfiguration=Release `
    /p:BuildProjectReferences=true
if ($LASTEXITCODE -ne 0) { throw ('homeserver win publish failed (exit ' + $LASTEXITCODE + ')') }

$zipName = 'homeserver-win-x64-' + $VERSION + '.zip'
$zipPath = Join-Path $HOMESERVER_RELEASES $zipName
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $HOMESERVER_OUTPUT '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sha256 = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestObj = @{
    version  = $VERSION
    fileName = $zipName
    sha256   = $sha256
    url      = 'https://wizionic.com/releases/homeserver/windows/' + $zipName
}
$manifestJson = $manifestObj | ConvertTo-Json
Set-Content -Path (Join-Path $HOMESERVER_RELEASES 'latest.json') -Value $manifestJson -Encoding utf8

Write-Host 'Uploading Home Server package to wizionic.com...' -ForegroundColor Cyan
$code = Invoke-ScpExpanded -LocalDir $HOMESERVER_RELEASES -RemoteDir ($REMOTE_ROOT + '/releases/homeserver/windows')
if ($code -ne 0) { throw ('scp homeserver windows failed (exit ' + $code + ')') }

Write-Host 'Home Server package deployed!' -ForegroundColor Green

# ==============================================================================
# PART 2 - SERVER BLAZOR APP (Docker)
# ==============================================================================
Write-Host 'Starting Production Build for Wizionic...' -ForegroundColor Cyan

Remove-DirForce $OUTPUT_DIR
New-Item -ItemType Directory -Force -Path $OUTPUT_DIR | Out-Null

dotnet publish 'App.csproj' `
    -c Release `
    -o $OUTPUT_DIR `
    --runtime linux-x64 `
    --self-contained false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:BlazorEnableCompression=true `
    /p:SelectBlazorWebAssemblyRazorConfiguration=Release `
    /p:BuildProjectReferences=true
if ($LASTEXITCODE -ne 0) { throw ('linux publish failed (exit ' + $LASTEXITCODE + ')') }
if (-not (Test-Path -LiteralPath $OUTPUT_DIR)) {
    throw ('linux publish did not create ' + $OUTPUT_DIR)
}

# Write Dockerfile (LF, UTF-8 no BOM). Verify size so we never upload an empty file.
$outFull = (Resolve-Path -LiteralPath $OUTPUT_DIR).Path
$dockerPath = Join-Path $outFull 'Dockerfile'
$dockerText = (
    "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final`n" +
    "WORKDIR /app`n" +
    "COPY . .`n" +
    "ENTRYPOINT [`"dotnet`", `"App.dll`"]`n"
)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($dockerPath, $dockerText, $utf8NoBom)
if (-not (Test-Path -LiteralPath $dockerPath) -or ((Get-Item -LiteralPath $dockerPath).Length -lt 20)) {
    throw ('Failed to write Dockerfile at ' + $dockerPath)
}
Write-Host ('Dockerfile ready (' + (Get-Item -LiteralPath $dockerPath).Length + ' bytes).') -ForegroundColor DarkGray

Write-Host 'Transferring clean build assets to M5 Server...' -ForegroundColor Cyan

$SafeCleanCmd = 'if [ -d ''' + $REMOTE_ROOT + ''' ]; then find ''' + $REMOTE_ROOT + ''' -mindepth 1 -maxdepth 1 ! -name data ! -name releases -exec rm -rf {} +; fi'
$code = Invoke-Ssh -RemoteCommand $SafeCleanCmd
if ($code -ne 0) { throw ('remote clean failed (exit ' + $code + ')') }

$code = Invoke-PublishUpload -LocalDir $OUTPUT_DIR -RemoteDir $REMOTE_ROOT
if ($code -ne 0) { throw ('publish upload failed (exit ' + $code + ')') }

# Confirm Dockerfile landed on the server before docker build
$verifyDf = 'wc -c ' + "'" + $REMOTE_ROOT + '/Dockerfile' + "'"
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $verifyOut = & ssh (@(Get-SshCommonArgs) + @($SSH_TARGET, $verifyDf)) 2>&1 | Out-String
} finally {
    $ErrorActionPreference = $prevEap
}
Write-Host ('Remote Dockerfile: ' + $verifyOut.Trim()) -ForegroundColor DarkGray
if ($verifyOut -notmatch '\d{2,}') {
    throw 'Remote Dockerfile missing or empty after upload.'
}

Write-Host 'Instructing Remote Docker Engine to assemble and spin up...' -ForegroundColor Cyan

if ($WindowsBrevoKey) {
    $brevo = $WindowsBrevoKey
} else {
    $brevo = ''
}

# Important: group (docker rm || true) so a failed build does NOT still run docker run.
# Bash: A && B && (C || true) && D  — D only if A and B succeeded.
$ghId = if ($OAuthGitHubClientId) { $OAuthGitHubClientId } else { '' }
$ghSec = if ($OAuthGitHubClientSecret) { $OAuthGitHubClientSecret } else { '' }
$goId = if ($OAuthGoogleClientId) { $OAuthGoogleClientId } else { '' }
$goSec = if ($OAuthGoogleClientSecret) { $OAuthGoogleClientSecret } else { '' }
$noId = if ($OAuthNotionClientId) { $OAuthNotionClientId } else { '' }
$noSec = if ($OAuthNotionClientSecret) { $OAuthNotionClientSecret } else { '' }
$stId = if ($OAuthStripeClientId) { $OAuthStripeClientId } else { '' }
$stSec = if ($OAuthStripeClientSecret) { $OAuthStripeClientSecret } else { '' }

$RemoteCmds = (
    'cd ' + "'" + $REMOTE_ROOT + "'" +
    ' && test -s Dockerfile' +
    ' && docker build -t wizionic-app:latest .' +
    ' && (docker rm -f wizionic || true)' +
    ' && docker run -d --name wizionic -p 5100:8080' +
    ' -e ASPNETCORE_ENVIRONMENT=Production' +
    ' -e BREVO_API_KEY=' + "'" + $brevo + "'" +
    ' -e OAuth__GitHub__ClientId=' + "'" + $ghId + "'" +
    ' -e OAuth__GitHub__ClientSecret=' + "'" + $ghSec + "'" +
    ' -e OAuth__GitHub__RedirectUri=' + "'" + 'https://wizionic.com/api/oauth/github/callback' + "'" +
    ' -e OAuth__Google__ClientId=' + "'" + $goId + "'" +
    ' -e OAuth__Google__ClientSecret=' + "'" + $goSec + "'" +
    ' -e OAuth__Google__RedirectUri=' + "'" + 'https://wizionic.com/api/oauth/google/callback' + "'" +
    ' -e OAuth__Notion__ClientId=' + "'" + $noId + "'" +
    ' -e OAuth__Notion__ClientSecret=' + "'" + $noSec + "'" +
    ' -e OAuth__Notion__RedirectUri=' + "'" + 'https://wizionic.com/api/oauth/notion/callback' + "'" +
    ' -e OAuth__Stripe__ClientId=' + "'" + $stId + "'" +
    ' -e OAuth__Stripe__ClientSecret=' + "'" + $stSec + "'" +
    ' -e OAuth__Stripe__RedirectUri=' + "'" + 'https://wizionic.com/api/oauth/stripe/callback' + "'" +
    ' -v ' + $REMOTE_ROOT + '/data:/app/data' +
    ' -v ' + $REMOTE_ROOT + '/releases:/app/wwwroot/releases' +
    ' --restart unless-stopped wizionic-app:latest'
)

$code = Invoke-Ssh -RemoteCommand $RemoteCmds
if ($code -ne 0) {
    Write-Host ('Docker deploy failed (exit ' + $code + '). Check docker permissions and logs on the server.') -ForegroundColor Red
    exit $code
}

Write-Host ('Deployment Complete! Wizionic is live at http://' + $SERVER_IP + ':5100') -ForegroundColor Green
Write-Host '(chatfish remains on port 5000 / /var/www/chatfish - untouched)' -ForegroundColor DarkGray

} finally {
    Clear-SshAuth
}
