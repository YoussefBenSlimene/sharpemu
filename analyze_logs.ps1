$paramPid = 10880
$log = "c:\ps5-emulator\sharpemu\hellboy_live_20260906_011810.err.log"
$deadline = (Get-Date).AddMinutes(8)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 20
  $alive = Get-Process -Id $paramPid -ErrorAction SilentlyContinue
  if (-not $alive) { Write-Host "PROCESS EXITED at $(Get-Date -Format HH:mm:ss)"; break }
  Write-Host ("alive CPU=" + [Math]::Round($alive.CPU,1) + "s mem=" + [Math]::Round($alive.WorkingSet64/1MB) + "MB")
}
$alive = Get-Process -Id $paramPid -ErrorAction SilentlyContinue
if ($alive) { Write-Host "STILL RUNNING after timeout - leaving it up (user can close it)" }
Write-Host "=== log summary ==="
Write-Host ("recoveries: " + @(Select-String -Path $log -Pattern 'bad-store recovery').Count)
Write-Host ("substitutions: " + @(Select-String -Path $log -Pattern 'substitution').Count)
Write-Host ("fixups: " + @(Select-String -Path $log -Pattern 'null-allocation fixup').Count)
$ex = Select-String -Path $log -Pattern 'NATIVE EXCEPTION CAUGHT'
Write-Host ("exception blocks: " + @($ex).Count)
if ($ex) {
  $lines = Get-Content $log
  $start = $ex[-1].LineNumber
  $lines[($start-1)..([Math]::Min($start+30, $lines.Count-1))] |
    Select-String -Pattern "RAX|RBX|RSI|RDI|R1[0-5]|Code at RIP|AV |Type|Exception Address" |
    ForEach-Object { Write-Host $_.Line }
}
Write-Host "=== last 5 lines ==="
Get-Content $log -Tail 5 | ForEach-Object { Write-Host $_.Substring(0, [Math]::Min(180, $_.Length)) }
