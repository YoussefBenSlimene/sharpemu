# Script to run SharpEmu with debugging enabled (no timer)
$ErrorActionPreference = "Stop"

$exePath = "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.dll"

# Set environment variables to fix permission errors and enable debugging
$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_LOG_AUDIO_QUEUE = "1"
$env:SHARPEMU_LOG_GPU_DETILE = "1"
$env:SHARPEMU_TRACE_VULKAN_SHADER = "1"
$env:SHARPEMU_TRACE_GUEST_WORK_COMPLETION = "1"
$env:SHARPEMU_VBLANK_PRECISION_US = "100"

Write-Host "Starting SharpEmu with debugging enabled..."
Write-Host "Environment variables set:"
Write-Host "  SHARPEMU_WRITABLE_APP0=1 (fixes permission errors)"
Write-Host "  SHARPEMU_LOG_AUDIO_QUEUE=1 (enables audio logging)"
Write-Host "  SHARPEMU_LOG_GPU_DETILE=1 (enables GPU detile logging)"
Write-Host "  SHARPEMU_TRACE_VULKAN_SHADER=1 (enables Vulkan shader tracing)"
Write-Host "  SHARPEMU_TRACE_GUEST_WORK_COMPLETION=1 (enables guest work completion tracing)"
Write-Host ""
Write-Host "Press Ctrl+C to stop the emulator"

# Start the emulator
& dotnet $exePath