<#
.SYNOPSIS
  Starts WSL and Docker when Windows boots, so Intellifer Hub comes back after a reboot.

.DESCRIPTION
  Registers a Task Scheduler task that runs at startup, before anyone signs in. It starts the WSL distribution
  that runs Docker, starts Docker inside it, and keeps WSL running.

  Docker then restarts the hub's containers (restart: unless-stopped), except containers that were stopped by
  hand with "docker compose stop": those stay off until someone starts them again.

  Run once, in PowerShell opened as Administrator, signed in as the Windows user that owns the WSL
  distribution (WSL distributions belong to a user). You are asked for that user's password so the task can
  run while nobody is signed in.

.PARAMETER Distro
  The WSL distribution that runs Docker. Defaults to the default distribution (see: wsl -l -v).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File C:\IntelliferSupport\windows\install-autostart.ps1
  powershell -ExecutionPolicy Bypass -File C:\IntelliferSupport\windows\install-autostart.ps1 -Distro Ubuntu
#>
#Requires -RunAsAdministrator
param(
    [string]$Distro,
    [string]$TaskName = "Intellifer Hub - start WSL and Docker"
)

$ErrorActionPreference = "Stop"

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

Write-Host "WSL distribution: $Distro"

# Check Docker exists in that distribution before relying on it at boot.
& wsl.exe -d $Distro -u root -- sh -c "command -v dockerd >/dev/null" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker (dockerd) was not found in WSL distribution '$Distro'. Pass the right one with -Distro."
}

# Start Docker (systemd if WSL runs it, the init script otherwise), then keep WSL alive: WSL shuts a
# distribution down when nothing is attached to it, which would stop Docker and the hub.
$inner = 'systemctl start docker 2>/dev/null || service docker start; exec sleep infinity'
$action = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\wsl.exe" `
    -Argument "-d `"$Distro`" -u root -- sh -c `"$inner`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -MultipleInstances IgnoreNew

$user = "$env:USERDOMAIN\$env:USERNAME"
$credential = Get-Credential -UserName $user -Message "Password for $user, so WSL can start at boot without anyone signing in."

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -User $credential.UserName -Password $credential.GetNetworkCredential().Password `
    -RunLevel Highest -Description "Starts WSL ($Distro) and Docker at boot for Intellifer Hub." -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 10
$info = Get-ScheduledTaskInfo -TaskName $TaskName
Write-Host "Registered '$TaskName'. Last run result: $($info.LastTaskResult) (267009 = still running, which is expected)."
Write-Host "Check Docker: wsl -d $Distro -- docker ps"
