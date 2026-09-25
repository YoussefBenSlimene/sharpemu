# Bisect the black flip buffers: rasterisation/target problem vs content problem.
#
# The composite draw (ps=0x2005B40000) into the flip images is built and
# submitted with no pipeline error, yet the flip images stay exactly zero and so
# does the presented swapchain. The two explanations left are:
#
#   A) the draw runs and legitimately computes zero, because its sampled input
#      is the zero-filled 1x1 placeholder descriptor (pc=0x50);
#   B) the draw never reaches the flip image the presenter blits.
#
# Two independent, env-only probes (both read the swapchain back afterwards):
#
#   -Targets <addr list>            (done: 0x8FC0000000,0x8FC2000000)
#       SHARPEMU_FORCE_FULLSCREEN_VERTEX_TARGETS / _SOLID_FRAGMENT_TARGETS /
#       _DEFAULT_RASTER_STATE_TARGETS -> magenta fullscreen fragment for draws
#       into those targets. Magenta would mean A; zero is suspicious because the
#       override is applied in CreateTranslatedDrawResources, which the
#       offscreen path does call with its targets.
#
#   -WhiteTextures                  (input-side, cannot be defeated by a
#       shader-override/caching problem)
#       SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS=* fills every uploaded texture with
#       0xFF and logs vk.texture_force_white addr/size. Non-zero output means the
#       draws do land and their content is what decides the frame (=> A).
#       Still zero means the flip image is never written by these draws (=> B).
param(
    [string]$Targets = "",
    [string]$WhiteTextureTargets = "",
    [string]$TracePixelShaderAddress = "",
    [string]$TraceGuestTextureAddresses = "",
    [switch]$ForceTexelUpload,
    [int]$TimerSeconds = 150
)
$ErrorActionPreference = "Stop"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$arm = if ($Targets) { "magenta" } elseif ($WhiteTextureTargets) { "whitetex" } else { "control" }
$logFile = "ms_forcebisect_${arm}_$stamp.txt"
$dumpDir = Join-Path $PSScriptRoot "ms_frames_forced_$arm"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

if (Test-Path $dumpDir) { Remove-Item $dumpDir -Recurse -Force }
New-Item -ItemType Directory -Path $dumpDir | Out-Null

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
if ($Targets) {
    $env:SHARPEMU_FORCE_FULLSCREEN_VERTEX_TARGETS = $Targets
    $env:SHARPEMU_FORCE_SOLID_FRAGMENT_TARGETS = $Targets
    $env:SHARPEMU_FORCE_DEFAULT_RASTER_STATE_TARGETS = $Targets
    # Proof that the override reached a draw: the presenter writes this file iff
    # forceSolidFragment was true for some pipeline (VulkanVideoPresenter:7438).
    $env:SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT = (Join-Path $PSScriptRoot "forced_solid_fragment.spv")
}
if ($WhiteTextureTargets) {
    $env:SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS = $WhiteTextureTargets
    # Without this the white fill can be hidden by the upload-known short-cut
    # (it is only applied where a texture upload is actually prepared), so the
    # two belong together.
    $env:SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD = "1"
}
if ($TracePixelShaderAddress) {
    # Dump every image binding of one pixel shader:
    # agc.texture_binding ps=… es=… pc=… op=… storage=… decoded=addr=… WxH …
    # Use the ps= value from the agc.texture_1x1_linear_binding warning.
    $env:SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS = $TracePixelShaderAddress
}
if ($TraceGuestTextureAddresses) {
    # vk.texture_upload_contents addr=… size=WxH nonzero_bytes=n/N
    #   nonblack_pixels=… center=HEX sample_unique=… hash=…
    # "*" traces every upload, which is the only way to catch the 1x1
    # placeholder whose address changes on every boot.
    $env:SHARPEMU_TRACE_GUEST_IMAGE_ADDRS = $TraceGuestTextureAddresses
}
if ($ForceTexelUpload) {
    # Needed together with the content trace: with the upload-known short-cut
    # active the upload never happens, so there would be nothing to fingerprint.
    $env:SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD = "1"
}
# Verdict: read the presented image back and dump frames.
$env:SHARPEMU_TRACE_GUEST_IMAGES = "present"
$env:SHARPEMU_SWAPCHAIN_DUMP_EVERY = "100"
$env:SHARPEMU_GUEST_IMAGE_DUMP_DIR = $dumpDir
# Always on: the upload-known audit is cheap (once per address) and is the
# difference between "no upload happened" and "the upload was silently skipped".
$env:SHARPEMU_TRACE_UPLOAD_KNOWN = "1"

Write-Host "Arm: $arm   Targets: $Targets   WhiteTextures: $WhiteTextureTargets   Timer: $TimerSeconds s"
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

$combinedOutput = $outputTask.Result + $stderrOutput.Result
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8
Write-Host "Logs saved to: $logFile"


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
