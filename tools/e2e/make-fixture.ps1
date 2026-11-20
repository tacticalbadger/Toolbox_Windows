<#
.SYNOPSIS
    Creates a throwaway Toolbox setup for end-to-end runs.

.DESCRIPTION
    SSH keys for the test servers, a known_hosts file, a fake 1Password CLI
    (op.cmd), a store with connections for the local test servers plus a
    Royal-style folder tree, and settings that skip the owner check. Nothing
    here touches the real app data: launch.ps1 points the app at it with the
    TOOLBOX_* environment overrides.

        tools\e2e\make-fixture.ps1 [-Dir <folder>]     default: build\e2e (gitignored)

    The fixture is rebuilt each time; the keys are kept if they exist.
#>
[CmdletBinding()]
param([string]$Dir)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dir) { $Dir = Join-Path $Root 'build\e2e' }
New-Item -ItemType Directory -Force $Dir | Out-Null
$Dir = (Resolve-Path $Dir).Path
$Utf8 = New-Object System.Text.UTF8Encoding($false)

$keygen = Get-Command ssh-keygen -ErrorAction SilentlyContinue
if (-not $keygen) { throw 'ssh-keygen not found (Windows OpenSSH client: Settings > System > Optional features).' }

function New-TestKey([string]$name, [string]$comment) {
    $path = Join-Path $Dir $name
    if (Test-Path $path) { return }
    # An explicit command line: PowerShell versions disagree on how an empty
    # argument (the empty passphrase) reaches a native program.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $keygen.Source
    $psi.Arguments = "-q -t ed25519 -N `"`" -C $comment -f `"$path`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "ssh-keygen failed for $name (exit code $($process.ExitCode))" }
}

New-TestKey 'host_key' 'toolbox-e2e-host'
New-TestKey 'user_key' 'toolbox-e2e-user'

$hostPub = (Get-Content (Join-Path $Dir 'host_key.pub') -TotalCount 1) -split ' '
[System.IO.File]::WriteAllText((Join-Path $Dir 'known_hosts'), "[127.0.0.1]:2222 $($hostPub[0]) $($hostPub[1])`n", $Utf8)

# Fake 1Password CLI: enough of "op item list" / "op item get" for the
# provider sync and credential resolution. Calls are logged to op.log.
$op = @(
    '@echo off',
    'echo %* >> "%~dp0op.log"',
    'if "%1 %2"=="item list" (',
    '  echo [{"id":"i1","title":"Echo login","vault":{"id":"v","name":"work"},"category":"LOGIN","additional_information":"demo"},',
    '  echo {"id":"i2","title":"Lab SSH key","vault":{"id":"v","name":"work"},"category":"SSH_KEY"}]',
    '  exit /b 0',
    ')',
    'if "%1 %2"=="item get" (',
    '  echo {"id":"i1","fields":[{"id":"username","purpose":"USERNAME","value":"demo"},{"id":"password","purpose":"PASSWORD","value":"secret"}]}',
    '  exit /b 0',
    ')',
    'echo [ERROR] unexpected arguments: %* 1>&2',
    'exit /b 1'
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $Dir 'op.cmd'), $op + "`r`n", [System.Text.Encoding]::ASCII)
Remove-Item (Join-Path $Dir 'op.log') -ErrorAction SilentlyContinue

# Store document (version 2). Ids are stable so launch arguments can name them.
$store = @'
{
  "version": 2,
  "connections": [
    {"id":"echo","name":"jump01 (echo, key file)","kind":"Ssh","host":"127.0.0.1","port":2222,"username":"demo","auth":{"KeyFile":{"path":"@DIR@\\user_key"}},"folder":"Jump Boxes","tags":[],"source":null},
    {"id":"echo-1p","name":"Local echo via 1Password","kind":"Ssh","host":"127.0.0.1","port":2222,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":null,"tags":[],"source":null},
    {"id":"echo-folder","name":"jump02 (folder credential)","kind":"Ssh","host":"127.0.0.1","port":2222,"username":"","auth":"FolderCredential","folder":"Jump Boxes","tags":[],"source":null},
    {"id":"rdp-local","name":"Local RDP (port 3390)","kind":"Rdp","host":"127.0.0.1","port":3390,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":null,"tags":[],"source":null},
    {"id":"vnc-local","name":"Local VNC (nothing listening)","kind":"Vnc","host":"127.0.0.1","port":5900,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":null,"tags":[],"source":null},
    {"id":"sftp-local","name":"Files over SFTP (echo server)","kind":"Sftp","host":"127.0.0.1","port":2222,"username":"demo","auth":{"KeyFile":{"path":"@DIR@\\user_key"}},"folder":"File Transfer","tags":[],"source":null},
    {"id":"ftp-local","name":"Files over FTP (plain)","kind":"Ftp","host":"127.0.0.1","port":2121,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"File Transfer","tags":[],"source":null},
    {"id":"ftps-local","name":"Files over FTPS (self-signed)","kind":"Ftps","host":"127.0.0.1","port":2122,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"File Transfer","tags":[],"source":null},
    {"id":"ad:dc01","name":"DC01","kind":"Rdp","host":"dc01.corp.example","port":3389,"username":"demo","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"ACME/CORP-AD/Domain Controllers","tags":["ad"],"source":"ad-fake"},
    {"id":"ad:sql01","name":"SQL01","kind":"Rdp","host":"sql01.corp.example","port":3389,"username":"demo","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"ACME/CORP-AD/Datacenter Servers/Database Servers","tags":["ad"],"source":"ad-fake"},
    {"id":"ad:web01","name":"WEB01","kind":"Ssh","host":"web01.corp.example","port":22,"username":"demo","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"ACME/CORP-AD/Datacenter Servers/Web Servers","tags":["ad"],"source":"ad-fake"},
    {"id":"ad:mac01","name":"MAC-01","kind":"Vnc","host":"mac-01.corp.example","port":5900,"username":"demo","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"ACME/CORP-AD/Workstations","tags":["ad"],"source":"ad-fake"}
  ],
  "credentials": [
    {"id":"op-fake:i1","name":"Echo login","username":"demo","kind":{"OnePassword":{"item_id":"i1","account":"work","op_path":"@DIR@\\op.cmd"}},"folder":"ACME/1Password/work","has_private_key":false,"tags":["login"],"source":"op-fake"},
    {"id":"op-fake:i2","name":"Lab SSH key","username":"","kind":{"OnePassword":{"item_id":"i2","account":"work","op_path":"@DIR@\\op.cmd"}},"folder":"ACME/1Password/work","has_private_key":true,"tags":["ssh_key"],"source":"op-fake"},
    {"id":"folder-user","name":"Folder user (asks)","username":"folderuser","kind":"Prompt","folder":"Jump Boxes","has_private_key":false,"tags":[],"source":null},
    {"id":"ad-fake#bind","name":"CORP-AD bind account","username":"CORP\\svc-toolbox","kind":"Vault","folder":"ACME/CORP-AD","has_private_key":false,"tags":["provider-account"],"source":null}
  ],
  "providers": [
    {"id":"op-fake","name":"1Password","kind":{"OnePassword":{"op_path":"@DIR@\\op.cmd","account":"work","vaults":["work"]}},"folder":"ACME/1Password","auto_refresh_minutes":60,"last_sync_epoch":null,"last_status":null},
    {"id":"ad-fake","name":"CORP-AD","kind":{"ActiveDirectory":{"server":"dc01.corp.example","search_base":"DC=corp,DC=example","bind_credential_id":"ad-fake#bind","admin_credential_id":"op-fake:i1","user_credential_id":null,"allow_plain_ldap":false,"accept_invalid_certs":false}},"folder":"ACME/CORP-AD","auto_refresh_minutes":0,"last_sync_epoch":null,"last_status":"fixture: never synced"}
  ],
  "folders": ["ACME", "File Transfer", "Jump Boxes"],
  "folder_credentials": {"Jump Boxes": "folder-user"}
}
'@
$store = $store.Replace('@DIR@', $Dir.Replace('\', '\\'))
[System.IO.File]::WriteAllText((Join-Path $Dir 'store.json'), $store, $Utf8)

# A fresh vault and key each time, and no owner check for these runs.
foreach ($name in 'vault.json', 'vault.key', 'toolbox.log') {
    Remove-Item (Join-Path $Dir $name) -ErrorAction SilentlyContinue
}
[System.IO.File]::WriteAllText((Join-Path $Dir 'settings.json'), "{`n  `"vaultRequiresAuth`": false`n}`n", $Utf8)

# Files for the FTP servers to serve.
$ftpRoot = Join-Path $Dir 'ftp-root'
New-Item -ItemType Directory -Force (Join-Path $ftpRoot 'docs') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $ftpRoot 'readme.txt'), "Toolbox test FTP server`n", $Utf8)
[System.IO.File]::WriteAllText((Join-Path $ftpRoot 'docs\notes.txt'), "nested file`n", $Utf8)

Write-Host "fixture ready: $Dir"
Write-Host 'next: tools\e2e\servers.ps1 start; tools\e2e\launch.ps1 --connect echo --connect echo-1p; tools\e2e\capture.ps1'
