# Phase A benchmark harness (docs/PERFORMANCE_PLAN.md).
#
# Runs a title for N seconds with a fixed, minimal instrumentation set (thread
# snapshots for import counts; AMPR trace for read volume), then prints a
# machine-readable summary line for A/B comparison across optimization phases:
#
#   BENCH game=<id> seconds=<n> wall_s=<n> imports=<n> imports_per_s=<n>
#         presents=<n> reads=<n> read_mb=<n> read_mb_per_min=<n>
#
# Note: the snapshot/AMPR overhead is part of the baseline; keep it constant
# across A/B runs so deltas remain meaningful.
param(
    [Parameter(Mandatory=$true)][string]$Game,   # sarah | mortal | hellboy | quake2 | smurfs
    [int]$TimerSeconds = 300,
    [string]$LogPrefix = "bench"
)
$ErrorActionPreference = "Stop"

$games = @{
    sarah   = "C:\ps5-emulator\ps5-games\PPSA02929-app0\eboot.bin"
    mortal  = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"
    hellboy = "C:\ps5-emulator\ps5-games\PPSA11264-app0\eboot.bin"
    quake2  = "C:\ps5-emulator\ps5-games\PPSA09477-app0\eboot.bin"
    smurfs  = "C:\ps5-emulator\ps5-games\[DLPSGAME.COM]-PPSA21607\PPSA21607\eboot.bin"
}
if (-not $games.ContainsKey($Game)) { Write-Host "ERROR: unknown game '$Game'"; exit 1 }
$gamePath = $games[$Game]

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "${LogPrefix}_${Game}_$stamp.txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path

if (-not (Test-Path -LiteralPath $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path -LiteralPath $gamePath)) { Write-Host "ERROR: game missing: $gamePath"; exit 1 }

# Fixed measurement set (see header). Everything else stays off.
# Child-process env only (env: would poison this session).
$childEnv = @{
    SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"
    SHARPEMU_LOG_AMPR = "1"
    SHARPEMU_TRACE_GUEST_IMAGES = "present"   # one swapchain readback per present = the frame counter
    SHARPEMU_WRITABLE_APP0 = "1"
    SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
}

Write-Host "Game: $Game   Timer: $TimerSeconds s   Log: $logFile"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$cmdFile = "$env:TEMP\sharpemu_run_$stamp.cmd"
$cmdLines = @($childEnv.GetEnumerator() | ForEach-Object { "set `"$($_.Key)=$($_.Value)`"" }) + @("`"$exePath`" `"$gamePath`" > `"$logFile`" 2>&1")
Set-Content -Path $cmdFile -Value $cmdLines
$process = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c `"$cmdFile`"" `
    -WindowStyle Hidden -PassThru

Start-Sleep -Seconds $TimerSeconds
if (!$process.HasExited) { $process.Kill() }
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$sw.Stop()

# --- metrics ---
# Total imports = sum of each thread's LAST snapshot (grouped by handle), so
# exited threads still contribute and no second is double-counted.
$totalImports = 0L
$lastByHandle = @{}
foreach ($m in (Select-String -Path $logFile -Pattern "guest_thread.snapshot handle=(0x[0-9A-Fa-f]+).*imports=(\d+)")) {
    if ($m.Line -match "handle=(0x[0-9A-Fa-f]+).*imports=(\d+)") {
        $lastByHandle[$Matches[1]] = [int64]$Matches[2]
    }
}
foreach ($v in $lastByHandle.Values) { $totalImports += $v }

# "presented guest frame" is a one-shot log (M16); the per-present swapchain
# readback (TRACE_GUEST_IMAGES=present, enabled above) is the per-frame count.
$presents = (Select-String -Path $logFile -Pattern "vk\.swapchain_image").Count

$readCount = 0; $readBytes = 0L
Select-String -Path $logFile -Pattern "ampr\.read_file.*read=0x([0-9A-Fa-f]+)" |
    ForEach-Object { $readCount++; $readBytes += [Convert]::ToInt64($_.Matches[0].Groups[1].Value, 16) }

$wall = [math]::Round($sw.Elapsed.TotalSeconds, 1)
$ips  = if ($wall -gt 0) { [int64]($totalImports / $wall) } else { 0 }
$mbm  = if ($wall -gt 0) { [math]::Round($readBytes / 1MB / ($wall / 60), 1) } else { 0 }

Write-Host ("BENCH game={0} seconds={1} wall_s={2} imports={3} imports_per_s={4} presents={5} reads={6} read_mb={7} read_mb_per_min={8}" -f
    $Game, $TimerSeconds, $wall, $totalImports, $ips, $presents, $readCount, [math]::Round($readBytes/1MB,1), $mbm)
Write-Host "Log: $logFile"
