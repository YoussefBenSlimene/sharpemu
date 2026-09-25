# Probe whether ANY guest render target contains pixels.
#
# run_ms_framedump.ps1 proved the image handed to the window is 100% zeros
# (nonzero_bytes=0/8294400 nonblack_pixels=0/2073600 on every sampled flip).
# That only says the *swapchain* is black; it does not say whether the guest
# render targets the composite reads are black too. This run uses the
# every-Nth-draw guest image readback:
#
#   SHARPEMU_TRACE_GUEST_IMAGES=every:50@5000
#       -> for every 1280x720+ guest image, read it back on every 50th draw
#          into it, but only after 5000 such draws have happened globally.
#          Prints: vk.guest_image ... nonblack_pixels=<n>/<total> ... hash=
#          and    [RB] addr=... mean=r,g,b,A sample_unique=<n>
#   SHARPEMU_GUEST_IMAGE_DUMP_DIR -> raw .rgba frames (every 25th readback)
#
# Decision rule:
#   all black  -> the guest never renders content (composite/shaders/inputs)
#   any color  -> the guest renders and the loss happens after the guest
#                 image (present blit, layout handoff, DCC/detile, format)
$ErrorActionPreference = "Stop"

$timerSeconds = 200
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "ms_guestimg_$stamp.txt"
$dumpDir = Join-Path $PSScriptRoot "ms_guestimg"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

if (Test-Path $dumpDir) { Remove-Item $dumpDir -Recurse -Force }
New-Item -ItemType Directory -Path $dumpDir | Out-Null

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
$env:SHARPEMU_TRACE_GUEST_IMAGES = "every:50@5000"
$env:SHARPEMU_GUEST_IMAGE_DUMP_DIR = $dumpDir
$env:SHARPEMU_DISABLE_GAME_DBG = "1"

Write-Host "Starting Mortal Shell with $timerSeconds second timer (guest image probe)..."
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
Get-ChildItem $dumpDir -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum |
    ForEach-Object { Write-Host ("Dumps: {0} ({1:N1} MB)" -f $_.Count, ($_.Sum / 1MB)) }
