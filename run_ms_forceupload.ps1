# A/B the upload-known short-cut against the Mortal Shell black screen.
#
# Background: the composite that fills the flip buffers samples descriptor slot
# pc=0x50, which decodes as a 1x1 R8G8B8A8 image at a real guest address (docs
# /investigations/mortal-shell-black-screen.md, sections 2-7). The emulator
# answers IsGuestImageUploadKnown() for such an address, and when the write
# tracker is off that answer comes from a sparse guest-memory probe -- but only
# if the address has a registered extent. With no extent the probe has nothing
# to compare and the short-cut is taken unconditionally, so the GPU image is
# reused and the guest texels are never copied (a zero-filled image => black).
#
#   .\run_ms_forceupload.ps1                 # control arm: report the skips
#   .\run_ms_forceupload.ps1 -ForceUpload    # experiment: copy guest texels
#
# Both arms read the swapchain back after the present blit, so the verdict is
# the nonblack_pixels count, not an inference.
param(
    [switch]$ForceUpload,
    [int]$TimerSeconds = 150
)
$ErrorActionPreference = "Stop"

$arm = if ($ForceUpload) { "forced" } else { "control" }
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "ms_forceupload_${arm}_$stamp.txt"
$dumpDir = Join-Path $PSScriptRoot "ms_frames_$arm"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

if (Test-Path $dumpDir) { Remove-Item $dumpDir -Recurse -Force }
New-Item -ItemType Directory -Path $dumpDir | Out-Null

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
# Diagnosis of the short-cut itself.
$env:SHARPEMU_TRACE_UPLOAD_KNOWN = "1"
# The variable under test.
if ($ForceUpload) { $env:SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD = "1" }
else { Remove-Item Env:SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD -ErrorAction SilentlyContinue }
# Verdict: read the presented image back.
$env:SHARPEMU_TRACE_GUEST_IMAGES = "present"
$env:SHARPEMU_SWAPCHAIN_DUMP_EVERY = "100"
$env:SHARPEMU_GUEST_IMAGE_DUMP_DIR = $dumpDir
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"

Write-Host "Arm: $arm   Timer: $TimerSeconds s"
Write-Host "Log:  $logFile"

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

$combinedOutput = $outputTask.Result + $stderrOutput.Result
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8
Write-Host "Logs saved to: $logFile"
