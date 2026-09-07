# Run Mortal Shell with a 150-second timer and save logs
$ErrorActionPreference = "Stop"

$timerSeconds = 150
$logFile = "mortal_shell_fix_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

if (-not (Test-Path $exePath)) { Write-Host "Error: SharpEmu.exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "Error: Mortal Shell eboot.bin missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_LOG_AUDIO_QUEUE = "1"

Write-Host "Starting Mortal Shell with $timerSeconds second timer..."
Write-Host "Logs will be saved to: $logFile"

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
$errorTask = $process.StandardError.ReadToEndAsync()

Start-Sleep -Seconds $timerSeconds

Write-Host "Timer expired, stopping Mortal Shell..."
if (!$process.HasExited) {
    $process.Kill()
}

$output = $outputTask.Result
$stderrOutput = $errorTask.Result
$combinedOutput = $output + $stderrOutput
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8

Write-Host "Mortal Shell stopped. Logs saved to: $logFile"
