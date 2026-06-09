#requires -Version 5.1
<#
.SYNOPSIS
  Register the NetworkAutoLogin Windows scheduled task.

.DESCRIPTION
  Creates a scheduled task named "NetworkAutoLogin" that runs
  "NetworkAutoLogin.exe run" as the current user (hidden window), triggered:
    1) at user logon, and
    2) repeated every N minutes (indefinitely).
  The program itself decides whether a login/refresh is actually needed.

  Before running this script:
    1) Build:      dotnet build -c Release   (in src\NetworkAutoLogin)
    2) Configure:  NetworkAutoLogin.exe setup

.PARAMETER ExePath
  Full path to NetworkAutoLogin.exe. Auto-detected under bin\Release if omitted.

.PARAMETER IntervalMinutes
  Repeat interval in minutes. Default 60.
#>
param(
    [string]$ExePath,
    [int]$IntervalMinutes = 60
)

$ErrorActionPreference = 'Stop'
$TaskName = 'NetworkAutoLogin'

# Locate the exe automatically if not provided.
if (-not $ExePath) {
    $root = Join-Path $PSScriptRoot 'src\NetworkAutoLogin'
    $candidate = Get-ChildItem -Path $root -Recurse -Filter 'NetworkAutoLogin.exe' `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\(Release|Debug)\\' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($candidate) { $ExePath = $candidate.FullName }
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    Write-Error "NetworkAutoLogin.exe not found. Build it first (dotnet build), or pass -ExePath."
    return
}

Write-Host "Using exe: $ExePath"

$action = New-ScheduledTaskAction -Execute $ExePath -Argument 'run' `
    -WorkingDirectory (Split-Path $ExePath)

# Trigger 1: at logon.
$triggerLogon = New-ScheduledTaskTrigger -AtLogOn

# Trigger 2: start now, repeat every IntervalMinutes, indefinitely.
$triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 15)

# Run as the current user, hidden, no elevation.
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

$task = New-ScheduledTask -Action $action `
    -Trigger @($triggerLogon, $triggerRepeat) `
    -Settings $settings -Principal $principal `
    -Description 'ZJU campus-net auto re-login (refresh before the 14-day expiry)'

Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

Write-Host "Registered scheduled task '$TaskName': at logon + every $IntervalMinutes minutes."
Write-Host "Inspect:   Get-ScheduledTask -TaskName $TaskName"
Write-Host "Run now:   Start-ScheduledTask -TaskName $TaskName"
Write-Host "Uninstall: Unregister-ScheduledTask -TaskName $TaskName -Confirm:`$false"
