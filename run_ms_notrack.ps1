# Hypothesis test: is the CLR "UnmanagedCallersOnly from managed code" FailFast
# caused by GuestImageWriteTracker (page-guard faults entering managed VEH)?
# Every observed FailFast is immediately preceded by [SYNC] cpu-write-drain,
# so this run disables the tracker and keeps everything else identical to
# run_mortal_shell_dbg.ps1 so the two logs are directly comparable.
$ErrorActionPreference = "Stop"

$timerSeconds = 300
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "ms_notrack_$stamp.txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
# The variable under test: tracker OFF (default-on was introduced in 56bad5f).
$env:SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC = "1"
$env:SHARPEMU_GUEST_IMAGE_CPU_SYNC = "0"
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"

Write-Host "Starting Mortal Shell (write-tracker DISABLED) with $timerSeconds second timer..."
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
