<#
.SYNOPSIS
    Registers a Task Scheduler task that starts the widget at logon with admin rights, without a
    UAC prompt. Re-run any time to update the exe path. Self-elevates.

.PARAMETER ExePath
    Path to Overlay.exe. Defaults to the Release build output in this repo.

.PARAMETER NoStart
    Only register the task; don't (re)start the widget now.
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\Overlay\bin\Release\net8.0-windows\Overlay.exe'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$TaskName = 'Overlay Hardware Widget'
$ExePath = [IO.Path]::GetFullPath($ExePath)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-ExePath', "`"$ExePath`"")
    if ($NoStart) { $argList += '-NoStart' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

if (-not (Test-Path $ExePath)) { throw "Overlay.exe not found at $ExePath. Build it first: dotnet build -c Release" }

$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory (Split-Path $ExePath)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT10S' # let Explorer create the desktop first
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Registered '$TaskName' -> $ExePath"

Get-Process Overlay -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not $NoStart) {
    Start-Sleep -Milliseconds 500
    Start-ScheduledTask -TaskName $TaskName
    Write-Host 'Widget (re)started.'
}
