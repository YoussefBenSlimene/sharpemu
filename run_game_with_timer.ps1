# Script to run Mortal Shell with a 60-second timer and save logs
$ErrorActionPreference = "Stop"

$timerSeconds = 60
$logFile = "mortal_shell_run_log_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
$exePath = "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe"
$gamePath = "C:\ps5-emulator\ps5-games\PPSA02868-app0\eboot.bin"

# Check if the executable exists
if (-not (Test-Path $exePath)) {
    Write-Host "Error: SharpEmu executable not found at: $exePath"
    Write-Host "Please build the project first with: dotnet build"
    exit 1
}

# Check if the game file exists
if (-not (Test-Path $gamePath)) {
    Write-Host "Error: Mortal Shell eboot.bin not found at: $gamePath"
    exit 1
}

# Set environment variables to fix permission errors and enable debugging
$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_LOG_AUDIO_QUEUE = "1"
$env:SHARPEMU_LOG_GPU_DETILE = "1"
$env:SHARPEMU_TRACE_VULKAN_SHADER = "1"
$env:SHARPEMU_TRACE_GUEST_WORK_COMPLETION = "1"
$env:SHARPEMU_VBLANK_PRECISION_US = "100"
$env:SHARPEMU_LOG_IO = "1"

Write-Host "Starting Mortal Shell with $timerSeconds second timer..."
Write-Host "Logs will be saved to: $logFile"
Write-Host "Game path: $gamePath"
Write-Host "Environment variables set:"
Write-Host "  SHARPEMU_WRITABLE_APP0=1 (fixes permission errors)"
Write-Host "  SHARPEMU_LOG_AUDIO_QUEUE=1 (enables audio logging)"
Write-Host "  SHARPEMU_LOG_GPU_DETILE=1 (enables GPU detile logging)"
Write-Host "  SHARPEMU_TRACE_VULKAN_SHADER=1 (enables Vulkan shader tracing)"
Write-Host "  SHARPEMU_TRACE_GUEST_WORK_COMPLETION=1 (enables guest work completion tracing)"
Write-Host "  SHARPEMU_LOG_IO=1 (enables IO logging)"

# Start the emulator in background and capture output
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

# Start reading output
$outputBuilder = New-Object System.Text.StringBuilder
$errorBuilder = New-Object System.Text.StringBuilder

$outputTask = $process.StandardOutput.ReadToEndAsync()
$errorTask = $process.StandardError.ReadToEndAsync()

# Wait for the timer
Start-Sleep -Seconds $timerSeconds

# Stop the process
Write-Host "Timer expired, stopping Mortal Shell..."
if (!$process.HasExited) {
    $process.Kill()
}

# Get the output
$output = $outputTask.Result
$error = $errorTask.Result

# Combine and save output
$combinedOutput = $output + $error
$combinedOutput | Out-File -FilePath $logFile -Encoding UTF8

Write-Host "Mortal Shell stopped. Logs saved to: $logFile"
Write-Host "Last 50 lines of log:"
if ($combinedOutput -and $combinedOutput.Length -gt 0) {
    $combinedOutput -split "`r`n" | Select-Object -Last 50
} else {
    Write-Host "No log output found"
}