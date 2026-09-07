                                                                                                                        # Run Hellboy WITHOUT a timer: launch detached with OS-level output
# redirection (survives the launching shell), and return immediately.
# The log file is complete once the game process has exited.
$ErrorActionPreference = "Stop"
$exePath = (Resolve-Path "artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe").Path
$gamePath = "C:\ps5-emulator\ps5-games\PPSA11264-app0\eboot.bin"
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outLog = Join-Path (Get-Location) "hellboy_live_$stamp.out.log"
$errLog = Join-Path (Get-Location) "hellboy_live_$stamp.err.log"

if (-not (Test-Path $exePath)) { Write-Host "ERROR: exe missing"; exit 1 }
if (-not (Test-Path $gamePath)) { Write-Host "ERROR: game missing"; exit 1 }

$env:SHARPEMU_WRITABLE_APP0 = "1"
$env:SHARPEMU_LOG_SEMA = "1"
$env:SHARPEMU_LOG_AUDIO_QUEUE = "1"
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = "1"
$env:SHARPEMU_PERIODIC_SNAPSHOT_SECONDS = "20"
$env:SHARPEMU_STALL_WATCHDOG_SECONDS = "30"

$process = Start-Process -FilePath $exePath `
    -ArgumentList ('"' + $gamePath + '"') `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog `
    -PassThru -WindowStyle Hidden

"$($process.Id)|$errLog|$outLog" | Set-Content -Path (Join-Path $PSScriptRoot "hellboy_live.pid")
Write-Host "Hellboy launched (PID $($process.Id))."
Write-Host "stderr log: $errLog"
Write-Host "stdout log: $outLog"