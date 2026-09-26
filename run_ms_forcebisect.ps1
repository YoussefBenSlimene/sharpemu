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
    [string]$TraceStorageImageInit = "",
    [switch]$ForceTexelUpload,
    [switch]$ThreadSnapshots,
    [switch]$AmprTrace,
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

# Env vars go into the CHILD process only. PowerShell's env: provider mutates
# the *session* environment (harness runs poisoned every later manual game run
# in the same terminal — the "fps 52 -> 2.4" user report was exactly that),
# so never $env:X = ... here.
$childEnv = @{
    SHARPEMU_WRITABLE_APP0 = "1"
    SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
    SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
    # Verdict: read the presented image back and dump frames.
    SHARPEMU_TRACE_GUEST_IMAGES = "present"
    SHARPEMU_SWAPCHAIN_DUMP_EVERY = "100"
    SHARPEMU_GUEST_IMAGE_DUMP_DIR = $dumpDir
    # Always on: the upload-known audit is cheap (once per address).
    SHARPEMU_TRACE_UPLOAD_KNOWN = "1"
}
if ($Targets) {
    $childEnv.SHARPEMU_FORCE_FULLSCREEN_VERTEX_TARGETS = $Targets
    $childEnv.SHARPEMU_FORCE_SOLID_FRAGMENT_TARGETS = $Targets
    $childEnv.SHARPEMU_FORCE_DEFAULT_RASTER_STATE_TARGETS = $Targets
    $childEnv.SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT = (Join-Path $PSScriptRoot "forced_solid_fragment.spv")
}
if ($WhiteTextureTargets) {
    $childEnv.SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS = $WhiteTextureTargets
    $childEnv.SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD = "1"
}
if ($TracePixelShaderAddress) { $childEnv.SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS = $TracePixelShaderAddress }
if ($TraceGuestTextureAddresses) { $childEnv.SHARPEMU_TRACE_GUEST_IMAGE_ADDRS = $TraceGuestTextureAddresses }
if ($ForceTexelUpload) { $childEnv.SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD = "1" }
if ($TraceStorageImageInit) { $childEnv.SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS = $TraceStorageImageInit }
if ($ThreadSnapshots) { $childEnv.SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1" }
if ($AmprTrace) { $childEnv.SHARPEMU_LOG_AMPR = "1" }

Write-Host "Arm: $arm   Targets: $Targets   WhiteTextures: $WhiteTextureTargets   Timer: $TimerSeconds s"
Write-Host "Log: $logFile"

# Redirect straight to the log file so the log is written incrementally and
# survives a Ctrl+C / window kill of this harness (the previous
# ReadToEndAsync buffer lost the whole log when the run froze, M33).
# Env goes in the cmd string: env: would poison this PowerShell session.
# Env goes into a generated .cmd so nothing leaks into this PowerShell session.
# (Nested-quote parsing of cmd /c with inline `set` chains is unreliable.)
$cmdFile = "$env:TEMP\sharpemu_run_$stamp.cmd"
$cmdLines = @($childEnv.GetEnumerator() | ForEach-Object { "set `"$($_.Key)=$($_.Value)`"" }) + @("`"$exePath`" `"$gamePath`" > `"$logFile`" 2>&1")
Set-Content -Path $cmdFile -Value $cmdLines
$process = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c `"$cmdFile`"" `
    -WindowStyle Hidden -PassThru

Start-Sleep -Seconds $TimerSeconds

if (!$process.HasExited) { $process.Kill() }

# The emulator runs the guest in a "mitigated child process" ([DEBUG] Running in
# mitigated child process in the log), so killing the parent leaves the child
# alive holding artifacts\bin — which then breaks the next `dotnet build` with
# MSB3021/MSB3027 file-lock errors, and leaves a second emulator instance
# running while the next arm starts. Clean up both.
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Logs saved to: $logFile"
