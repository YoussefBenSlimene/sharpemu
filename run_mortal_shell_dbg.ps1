# Run Mortal Shell with GAME-DBG diagnostics for a fixed time, then kill.
#
# Usage:
#   .\run_mortal_shell_dbg.ps1                    # 300 s, ms_dbg_*.txt
#   .\run_mortal_shell_dbg.ps1 -TimerSeconds 90 -LogPrefix ms_ps -QuietGameDbg
param(
    [int]$TimerSeconds = 300,
    [string]$LogPrefix = "ms_dbg",
    [switch]$QuietGameDbg
)
$ErrorActionPreference = "Stop"

$timerSeconds = $TimerSeconds
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "${LogPrefix}_$stamp.txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
# GuestImageWriteTracker is opt-in again (2026-09-25): the page-guard design
# killed Mortal Shell with a CLR FailFast ("attempted to call a
# UnmanagedCallersOnly method from managed code") within ~230 s and did not
# remove the 1x1 placeholder textures, so the dbg run must not enable it.
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"
if ($QuietGameDbg) { $env:SHARPEMU_DISABLE_GAME_DBG = "1" }

Write-Host "Starting Mortal Shell with $timerSeconds second timer..."
Write-Host "Log: $logFile"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exePath
$psi.Arguments = "`"$gamePath`""
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $psi
$process.Start() | Out-Null

$outputTask = $process.StandardOutput.ReadToEndAsync()
$stderrOutput = $process.StandardError.ReadToEndAsync()

Start-Sleep -Seconds $timerSeconds

Write-Host "Timer expired, stopping..."
if (!$process.HasExited) {
    $process.Kill()
}

$combinedOutput = $outputTask.Result + $stderrOutput.Result
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8
Write-Host "Logs saved to: $logFile"
