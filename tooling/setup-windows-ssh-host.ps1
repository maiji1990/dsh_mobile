#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Prepares THIS Windows PC as a test SSH host for DSH Mobile's remote host feature (TASK-012, ADR-0006).

.DESCRIPTION
  Run in an elevated PowerShell, after reviewing it. It:
    1. starts the already-installed OpenSSH Server service (start type is left unchanged, e.g. Manual);
    2. adds the phone's SSH public key (an ecdsa-sha2-nistp256 line shown on the phone's 远程主机 screen) to
       C:\ProgramData\ssh\administrators_authorized_keys -- the file Windows OpenSSH reads for members of the
       Administrators group -- and restricts that file to Administrators + SYSTEM as sshd requires;
    3. prints the host key fingerprints so you can compare them with the one the phone shows before trusting it.
  It does NOT change firewall rules, the sshd_config, environment variables or the service start type.
  The phone reaches this sshd over USB with `adb reverse tcp:2222 tcp:22` (no network exposure needed).

  DSH itself is installed separately, as your normal user (not elevated):
      npm install -g @deepseek-ai/dsh@0.2.1-rc.5

.PARAMETER PublicKeyFile
  A text file containing exactly the phone's public key line.

.EXAMPLE
  .\tooling\setup-windows-ssh-host.ps1 -PublicKeyFile C:\path\phone-key.pub
#>
param(
    [Parameter(Mandatory = $true)][string]$PublicKeyFile
)
$ErrorActionPreference = 'Stop'

$line = (Get-Content -LiteralPath $PublicKeyFile -Raw).Trim()
if ($line -notmatch '^ecdsa-sha2-nistp256 [A-Za-z0-9+/]+=* [A-Za-z0-9._-]+$') {
    throw "Not a single ecdsa-sha2-nistp256 public key line: $PublicKeyFile"
}

# 1. sshd (installed but stopped on this PC). First start also generates host keys and sshd_config.
$svc = Get-Service sshd -ErrorAction SilentlyContinue
if (-not $svc) { throw 'OpenSSH Server is not installed (Settings > Optional features > OpenSSH Server).' }
if ($svc.Status -ne 'Running') { Start-Service sshd }
Write-Host "sshd: $((Get-Service sshd).Status) (start type $((Get-Service sshd).StartType), unchanged)"

# 2. administrators_authorized_keys with the ACL sshd insists on.
$auth = 'C:\ProgramData\ssh\administrators_authorized_keys'
$existing = if (Test-Path $auth) { Get-Content -LiteralPath $auth } else { @() }
if ($existing -contains $line) {
    Write-Host 'phone key already present'
} else {
    Add-Content -LiteralPath $auth -Value $line -Encoding ascii
    Write-Host "phone key added to $auth"
}
icacls.exe $auth /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
Write-Host 'ACL: Administrators + SYSTEM only'

# 3. Host key fingerprints to compare on the phone.
Write-Host "`nHost key fingerprints (compare with the phone's dialog):"
Get-ChildItem 'C:\ProgramData\ssh\ssh_host_*_key.pub' | ForEach-Object { ssh-keygen.exe -lf $_.FullName }
