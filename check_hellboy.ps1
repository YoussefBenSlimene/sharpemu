# AV detail + liveness check for the running Hellboy instance.
$pidFile = Join-Path $PSScriptRoot "hellboy_live.pid"
$parts = (Get-Content $pidFile -Raw).Trim().Split('|')
$errLog = $parts[1]
$outLog = $parts[2]

$lines = @()
foreach ($log in @($errLog, $outLog)) { if (Test-Path $log) { $lines += Get-Content $log } }
Write-Host ("Log lines total: " + $lines.Count)

$avLines = @($lines | Select-String -Pattern "NATIVE EXCEPTION CAUGHT")
Write-Host ("AV count: " + $avLines.Count)

$first = ($lines | Select-String -Pattern "VEH_AV first-chance" | Select-Object -First 1).LineNumber
if ($first) {
    Write-Host "--- AV context ---"
    $start = [Math]::Max(0, $first - 1)
    $end = [Math]::Min($first + 20, $lines.Count - 1)
    for ($i = $start; $i -le $end; $i++) {
        $line = $lines[$i]
        Write-Host $line.Substring(0, [Math]::Min(190, $line.Length))
    }
}

Write-Host "--- thread at AV ---"
$lines | Select-String -Pattern "Guest thread: handle" | Select-Object -First 2 | ForEach-Object { Write-Host $_.Line }

Write-Host "--- guest frame presentations ---"
$lines | Select-String -Pattern "presented guest frame|presented first frame" | ForEach-Object { Write-Host $_.Line }

$procId = [int]$parts[0]
$proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
Write-Host ("Launcher still running: " + ($null -ne $proc))
Get-Process SharpEmu -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  SharpEmu PID " + $_.Id + " CPU=" + $_.TotalProcessorTime)
}
