# Quake II (PPSA09477) - verify the "GPU hanged" StartFrame abort fix (Q9).
#
# Runs the game for -TimerSeconds, then prints a verdict:
#   usleep.gpu_wait lines  -> the GPU-aware usleep engaged
#   "GPU hanged" / abort   -> the old failure (should be 0)
#
# A/B:  .\run_quake2_gpuhang.ps1 -DisableFix   # expect the abort to come back
# Other: -Config Release, -GamePath <eboot.bin>, -TimerSeconds 120
param(
    [int]$TimerSeconds = 120,
    [string]$Config = "Release",
    [string]$GamePath = "C:\ps5-emulator\ps5-games\PPSA09477-app0\eboot.bin",
    [switch]$DisableFix
)
$ErrorActionPreference = "Stop"

$exePath = "artifacts\bin\$Config\net10.0\win-x64\SharpEmu.exe"
if (-not (Test-Path $exePath)) { Write-Host "ERROR: $exePath missing - run: dotnet build -c $Config"; exit 1 }
if (-not (Test-Path $GamePath)) { Write-Host "ERROR: game missing: $GamePath"; exit 1 }
$exePath = (Resolve-Path $exePath).Path

# Stale-binary guard (M13): the exe must be newer than the last commit.
$exeTime = (Get-Item $exePath).LastWriteTime
$commitTime = [datetime](git log -1 --format=%cI)
if ($exeTime -lt $commitTime) {
    Write-Host "WARNING: $exePath ($exeTime) is older than HEAD ($commitTime). Rebuild first!" -ForegroundColor Yellow
}

# Orphaned mitigated children hold artifacts\bin and skew results (M31).
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$arm = if ($DisableFix) { "nofix" } else { "fix" }
$logFile = "quake2_gpuhang_${arm}_$stamp.txt"

if ($DisableFix) { $env:SHARPEMU_DISABLE_GPU_AWARE_USLEEP = "1" }
else { Remove-Item Env:SHARPEMU_DISABLE_GPU_AWARE_USLEEP -ErrorAction SilentlyContinue }

Write-Host "Quake II, arm=$arm, $TimerSeconds s -> $logFile"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exePath
$psi.Arguments = "`"$GamePath`""
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $psi
$process.Start() | Out-Null
$out = $process.StandardOutput.ReadToEndAsync()
$err = $process.StandardError.ReadToEndAsync()

$deadline = (Get-Date).AddSeconds($TimerSeconds)
while ((Get-Date) -lt $deadline -and -not $process.HasExited) { Start-Sleep -Seconds 1 }
$exitedEarly = $process.HasExited
if (-not $process.HasExited) { $process.Kill() }
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:SHARPEMU_DISABLE_GPU_AWARE_USLEEP -ErrorAction SilentlyContinue

($out.Result + $err.Result) | Out-File -FilePath $logFile -Encoding UTF8

$gpuWait  = (Select-String -Path $logFile -SimpleMatch "usleep.gpu_wait").Count
$hang     = (Select-String -Path $logFile -SimpleMatch "GPU hanged").Count
$abort    = (Select-String -Path $logFile -SimpleMatch "abort() called by guest").Count
$presents = (Select-String -Path $logFile -SimpleMatch "presented guest frame").Count
$lastWait = Select-String -Path $logFile -SimpleMatch "usleep.gpu_wait" | Select-Object -Last 1
$gameLines = Select-String -Path $logFile -Pattern "^\d\d:\d\d:\d\d: " | Select-Object -Last 3
Write-Host ""
Write-Host "==== verdict ($arm) ===="
Write-Host "exited before timer : $exitedEarly"
Write-Host "usleep.gpu_wait     : $gpuWait"
Write-Host "'GPU hanged'        : $hang"
Write-Host "guest abort()       : $abort"
Write-Host "guest frame shown   : $presents"
if ($lastWait) { Write-Host "last gpu_wait       : $($lastWait.Line.Substring(0, [Math]::Min(220, $lastWait.Line.Length)))" }
Write-Host "last game output    :"
$gameLines | ForEach-Object { Write-Host "   $($_.Line)" }
if ($hang -eq 0 -and $abort -eq 0 -and -not $exitedEarly) {
    Write-Host "PASS: no GPU-hang abort in $TimerSeconds s" -ForegroundColor Green
} else {
    Write-Host "FAIL: see $logFile (grep 'abort call-site' / 'GPU hanged')" -ForegroundColor Red
}
