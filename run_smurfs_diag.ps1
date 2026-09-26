# The Smurfs – Dreams (PPSA21607) diagnostic run.
#
# Symptom (2026-09-26, S1): clean boot, AgcCleanupThread spawns, but the log
# contains ZERO videoout/flip/draw calls and one thread spins forever in
# scePthreadCondTimedwait on cond 0x1F678F1E58 (ret=eboot+0x12184) — a starved
# task graph (same shape as Hellboy H6/H7). This harness enables the per-second
# per-thread state snapshot so we can see what every *other* thread is blocked
# on while that one spins.
param(
    [int]$TimerSeconds = 90,
    [string]$LogPrefix = "smurfs_diag"
)
$ErrorActionPreference = "Stop"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "${LogPrefix}_$stamp.txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\[DLPSGAME.COM]-PPSA21607\PPSA21607\eboot.bin"

if (-not (Test-Path -LiteralPath $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path -LiteralPath $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

# Env vars go into the CHILD process only — env: assignments would poison this
# PowerShell session for later manual game runs.
$childEnv = @{
    SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"
    SHARPEMU_WRITABLE_APP0 = "1"
    SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
    # Verdict traces: did the game ever submit/present during this run?
    SHARPEMU_TRACE_GUEST_IMAGES = "present"
}

Write-Host "Log: $logFile   Timer: $TimerSeconds s"

$cmdFile = "$env:TEMP\sharpemu_run_$stamp.cmd"
$cmdLines = @($childEnv.GetEnumerator() | ForEach-Object { "set `"$($_.Key)=$($_.Value)`"" }) + @("`"$exePath`" `"$gamePath`" > `"$logFile`" 2>&1")
Set-Content -Path $cmdFile -Value $cmdLines
$process = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c `"$cmdFile`"" `
    -WindowStyle Hidden -PassThru

Start-Sleep -Seconds $TimerSeconds

if (!$process.HasExited) { $process.Kill() }
# Kill the mitigated child too (it survives a parent kill, M31).
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Logs saved to: $logFile"
