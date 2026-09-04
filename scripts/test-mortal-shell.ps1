# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Run Mortal Shell for a fixed duration, capture all stdout/stderr to a log file, then exit.

param(
    [int]$DurationSeconds = 35,
    [string]$GamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin",
    [string]$EmulatorPath = "",
    [string]$LogDir = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($EmulatorPath)) {
    $EmulatorPath = Join-Path $RepoRoot "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe"
}

if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $RepoRoot "artifacts\logs"
}

if (-not (Test-Path $EmulatorPath)) {
    Write-Error "Emulator not found at: $EmulatorPath`nBuild first with: dotnet build SharpEmu.slnx -c Debug"
}

if (-not (Test-Path $GamePath)) {
    Write-Error "Game eboot not found at: $GamePath"
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$LogFile = Join-Path $LogDir "mortal-shell-$Timestamp.log"

Write-Host "Starting Mortal Shell test run"
Write-Host "  Emulator: $EmulatorPath"
Write-Host "  Game:     $GamePath"
Write-Host "  Duration: ${DurationSeconds}s"
Write-Host "  Log file: $LogFile"
Write-Host ""

$ProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
$ProcessInfo.FileName = $EmulatorPath
$ProcessInfo.Arguments = "`"$GamePath`""
$ProcessInfo.UseShellExecute = $false
$ProcessInfo.RedirectStandardOutput = $true
$ProcessInfo.RedirectStandardError = $true
$ProcessInfo.CreateNoWindow = $false
$ProcessInfo.WorkingDirectory = Split-Path -Parent $EmulatorPath

$Process = New-Object System.Diagnostics.Process
$Process.StartInfo = $ProcessInfo

$StdoutBuilder = New-Object System.Text.StringBuilder
$StderrBuilder = New-Object System.Text.StringBuilder

$StdoutHandler = {
    if ($EventArgs.Data) {
        [void]$Event.MessageData.AppendLine($EventArgs.Data)
        Write-Host $EventArgs.Data
    }
}

$StderrHandler = {
    if ($EventArgs.Data) {
        [void]$Event.MessageData.AppendLine($EventArgs.Data)
        Write-Host $EventArgs.Data -ForegroundColor Yellow
    }
}

Register-ObjectEvent -InputObject $Process -EventName OutputDataReceived -Action $StdoutHandler -MessageData $StdoutBuilder | Out-Null
Register-ObjectEvent -InputObject $Process -EventName ErrorDataReceived -Action $StderrHandler -MessageData $StderrBuilder | Out-Null

$Process.Start() | Out-Null
$Process.BeginOutputReadLine()
$Process.BeginErrorReadLine()

Write-Host "Waiting ${DurationSeconds} seconds..."
Start-Sleep -Seconds $DurationSeconds

if (-not $Process.HasExited) {
    Write-Host "Timer expired - stopping emulator (PID $($Process.Id))..."
    try {
        $Process.Kill()
        $Process.WaitForExit(5000) | Out-Null
    }
    catch {
        Write-Warning "Failed to kill process cleanly: $_"
    }
}

$Header = @(
    "=== Mortal Shell Test Run ==="
    "Timestamp: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "Duration: ${DurationSeconds}s"
    "Emulator: $EmulatorPath"
    "Game: $GamePath"
    "Exit code: $($Process.ExitCode)"
    "============================="
    ""
)

$LogContent = ($Header + $StdoutBuilder.ToString() + $StderrBuilder.ToString()) -join [Environment]::NewLine
Set-Content -Path $LogFile -Value $LogContent -Encoding UTF8

Write-Host ""
Write-Host "Log saved to: $LogFile"
Write-Host "Done."
