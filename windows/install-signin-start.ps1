<#
.SYNOPSIS
  Starts WSL and Docker when this Windows user signs in, so Intellifer Hub comes back after a reboot.
  Needs no administrator rights and stores no password.

.DESCRIPTION
  Puts a small launcher in this user's Startup folder. At each sign-in it starts the WSL distribution that
  runs Docker, starts Docker inside it, and keeps WSL running, all without a window. Docker then restarts
  the hub's containers (restart: unless-stopped), except containers stopped by hand with
  "docker compose stop": those stay off until someone starts them again.

  The hub runs while this user is signed in. Locking the screen is fine; signing out stops WSL and the hub
  until the next sign-in. For a start at boot with nobody signed in, use install-autostart.ps1 (needs admin).

  Run once, in normal PowerShell, as the Windows user that owns the WSL distribution.

.PARAMETER Distro
  The WSL distribution that runs Docker. Defaults to the default distribution (see: wsl -l -v).

.PARAMETER StartNow
  Also start it now, without signing out and in.

.PARAMETER Uninstall
  Remove the launcher.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File C:\IntelliferSupport\windows\install-signin-start.ps1 -StartNow
  powershell -ExecutionPolicy Bypass -File C:\IntelliferSupport\windows\install-signin-start.ps1 -Uninstall
#>
param(
    [string]$Distro,
    [switch]$StartNow,
    [switch]$Uninstall,
    # Only for testing: where to write the launcher instead of the real Startup folder.
    [string]$StartupFolder = [Environment]::GetFolderPath("Startup")
)

$ErrorActionPreference = "Stop"
$launcher = Join-Path $StartupFolder "Intellifer Hub - start WSL and Docker.vbs"

if ($Uninstall) {
    if (Test-Path $launcher) {
        Remove-Item $launcher
        Write-Host "Removed $launcher. WSL and Docker no longer start at sign-in."
    } else {
        Write-Host "Nothing to remove: $launcher does not exist."
    }
    return
}

if (-not $Distro) {
    # The default distribution is recorded per user in the registry.
    $lxss = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss"
    $defaultId = (Get-ItemProperty -Path $lxss -ErrorAction SilentlyContinue).DefaultDistribution
    if ($defaultId) {
        $Distro = (Get-ItemProperty -Path (Join-Path $lxss $defaultId)).DistributionName
    }
}
if (-not $Distro) {
    throw "Could not find the default WSL distribution. Run 'wsl -l -v' and pass it with -Distro <name>."
}
if ($Distro -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Unexpected WSL distribution name '$Distro'."
}

Write-Host "WSL distribution: $Distro"
& wsl.exe -d $Distro -u root -- sh -c "command -v dockerd >/dev/null" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker (dockerd) was not found in WSL distribution '$Distro'. Pass the right one with -Distro."
}

# Start Docker (systemd if WSL runs it, the init script otherwise), then keep WSL alive: WSL shuts a
# distribution down when nothing is attached to it, which would stop Docker and the hub.
# Root inside WSL needs no Windows administrator rights. Window style 0 = hidden.
$inner = 'systemctl start docker 2>/dev/null || service docker start; exec sleep infinity'
$command = "wsl.exe -d $Distro -u root -- sh -c ""$inner"""
$vbs = @"
' Intellifer Hub: starts WSL ($Distro) and Docker at sign-in, without a window.
' Installed by windows\install-signin-start.ps1. Remove with: install-signin-start.ps1 -Uninstall
CreateObject("WScript.Shell").Run "$($command.Replace('"', '""'))", 0, False
"@

New-Item -ItemType Directory -Force -Path $StartupFolder | Out-Null
Set-Content -Path $launcher -Value $vbs -Encoding ASCII
Write-Host "Installed $launcher"
Write-Host "WSL and Docker now start each time $env:USERNAME signs in. Stay signed in (locking is fine)."

if ($StartNow) {
    Start-Process -FilePath "wscript.exe" -ArgumentList "`"$launcher`""
    Start-Sleep -Seconds 15
    Write-Host "Started. Check: wsl -d $Distro -- docker ps"
}
