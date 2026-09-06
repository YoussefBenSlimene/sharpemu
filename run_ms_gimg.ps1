# Run Mortal Shell for 75s with guest-image event tracing to diagnose the black screen
$ErrorActionPreference = "Stop"
$timerSeconds = 75
$logFile = "ms_gimg_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
$exePath = "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe"
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_TRACE_GUEST_IMAGE_EVENTS = "1"
$env:SHARPEMU_LOG_AGC_SHADER = "1"
$env:SHARPEMU_TRACE_GUEST_IMAGES = "present"
$env:SHARPEMU_DISABLE_MITIGATION_RELAUNCH = "1"

Write-Host "Starting Mortal Shell ($timerSeconds s) -> $logFile"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path $exePath).Path
$psi.Arguments = "`"$gamePath`""
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $psi
$process.Start() | Out-Null
$outTask = $process.StandardOutput.ReadToEndAsync()
$errTask = $process.StandardError.ReadToEndAsync()
Start-Sleep -Seconds $timerSeconds
if (!$process.HasExited) { $process.Kill() }
$combined = $outTask.Result + $errTask.Result
$combined | Out-File -FilePath $logFile -Encoding UTF8
Write-Host "Saved $logFile ($(($combined -split "`n").Count) lines)"