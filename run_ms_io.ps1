# Follow-up to M27: the rendering side is exonerated (the emulator draws exactly
# the single 1x1 zero texel the guest supplies), so the question is upstream:
# does the guest ever read real texture data?
#
#   SHARPEMU_LOG_IO=1    -> [LOADER][TRACE] <op> path='…' <detail> for file ops
#                          (stat/open/apr_resolve/lseek/fstat/getdents/…)
#   SHARPEMU_LOG_OPEN=1  -> open-path resolution trace
#   SHARPEMU_DISABLE_GAME_DBG=1 -> keep the log readable; draws are not the topic
param(
    [string]$LogIoFilter = "",
    [switch]$OpenTraceOnly,
    [switch]$IoTraceOnly,
    [switch]$AmprTrace,
    [int]$TimerSeconds = 90
)
$ErrorActionPreference = "Stop"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$suffix = if ($OpenTraceOnly) { "open" } elseif ($IoTraceOnly) { "io" } else { "both" }
$logFile = "ms_io_${suffix}_$stamp.txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
$env:SHARPEMU_LOG_IO = if ($OpenTraceOnly) { "0" } else { "1" }
$env:SHARPEMU_LOG_OPEN = if ($IoTraceOnly) { "0" } else { "1" }
if ($AmprTrace) {
    # apr.* / ampr.* traces, including the read-file path:
    # ampr.read_file … fileId=… dest=… size=… offset=… bytesRead=… result=…
    $env:SHARPEMU_LOG_AMPR = "1"
    # And a fingerprint of the bytes at the read destination, once per
    # (fileId,size): ampr.read_content … nonzero_head=n/64 head=<hex>.
    $env:SHARPEMU_TRACE_AMPR_READ_CONTENT = "1"
    # The APR data path is what matters here, not the draw-level noise.
    $env:SHARPEMU_DISABLE_GAME_DBG = "1"
}
$env:SHARPEMU_DISABLE_GAME_DBG = "1"
if ($LogIoFilter) { $env:SHARPEMU_LOG_IO_FILTER = $LogIoFilter }

Write-Host "Timer: $TimerSeconds s   LOG_IO_FILTER='$LogIoFilter'"
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

Start-Sleep -Seconds $TimerSeconds

if (!$process.HasExited) { $process.Kill() }

# Killing the parent leaves the emulator's "mitigated child process" alive
# holding artifacts\bin (breaks the next dotnet build) and running alongside the
# next arm. Clean up both.
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$combinedOutput = $outputTask.Result + $stderrOutput.Result
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8
Write-Host "Logs saved to: $logFile"
