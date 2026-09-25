# Capture WHAT Mortal Shell actually presents.
#
# The crash (M12) is fixed and the transport is healthy (242k draws / 6015
# flips in a 300 s run), so the black screen can no longer be blamed on
# stalled rendering. This run reads the swapchain image back after the
# present blit and dumps it, which answers the only question that matters:
# is the image handed to the window black, and if so, is the *guest* image
# that was blitted into it black as well?
#
#   SHARPEMU_TRACE_GUEST_IMAGES=present  -> arms the swapchain readback and
#                                           prints vk.swapchain_image
#                                           nonblack_pixels=<n>/<total> hash=
#   SHARPEMU_SWAPCHAIN_DUMP_EVERY=100    -> read back + dump every 100th flip
#   SHARPEMU_GUEST_IMAGE_DUMP_DIR        -> raw .bgra frames for conversion
$ErrorActionPreference = "Stop"

$timerSeconds = 150
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "ms_frames_$stamp.txt"
$dumpDir = Join-Path $PSScriptRoot "ms_frames"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

if (Test-Path $dumpDir) { Remove-Item $dumpDir -Recurse -Force }
New-Item -ItemType Directory -Path $dumpDir | Out-Null

$env:SHARPEMU_WRITABLE_APP0 = "1"
# Tracker stays off: it is the M12 crash and it does not change the placeholders.
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
# Presented-image readback + frame dumps.
$env:SHARPEMU_TRACE_GUEST_IMAGES = "present"
$env:SHARPEMU_SWAPCHAIN_DUMP_EVERY = "100"
$env:SHARPEMU_GUEST_IMAGE_DUMP_DIR = $dumpDir
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"

Write-Host "Starting Mortal Shell with $timerSeconds second timer (frame dumps)..."
Write-Host "Log:  $logFile"
Write-Host "Dump: $dumpDir"

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
Get-ChildItem $dumpDir -Filter *.bgra | Measure-Object -Property Length -Sum |
    ForEach-Object { Write-Host ("Frames dumped: {0} ({1:N1} MB)" -f $_.Count, ($_.Sum / 1MB)) }
