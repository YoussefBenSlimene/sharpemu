# Run Quake II (PPSA09477) with diagnostics for a fixed time, then kill.
$ErrorActionPreference = "Stop"

$timerSeconds = 100
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "quake2_diag_$stamp.txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA09477-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_LOG_IO = "1"
$env:SHARPEMU_LOG_OPEN = "1"
$timerSeconds = 60

Write-Host "Starting Quake II with $timerSeconds second timer..."
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

Write-Host "Timer expired, stopping Quake II..."
if (!$process.HasExited) {
    $process.Kill()
}

$combinedOutput = $outputTask.Result + $stderrOutput.Result
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8
Write-Host "Logs saved to: $logFile"
