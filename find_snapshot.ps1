# Finds the first PreloadManager snapshot past a threshold and prints context.
param(
    [string]$LogPath = "hellboy_live_20260906_132852.err.log",
    [long]$Threshold = 1000000,
    [int]$Context = 4
)
$lines = Get-Content $LogPath
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'Loading\.PreloadManager' -and $lines[$i] -match 'imports=(\d+)') {
        $n = [long]$Matches[1]
        if ($n -gt $Threshold) {
            Write-Host ("first >{0} at line {1}" -f $Threshold, ($i + 1))
            $start = [Math]::Max(0, $i - 1)
            $end = [Math]::Min($lines.Count - 1, $i + $Context)
            $lines[$start..$end]
            break
        }
    }
}
