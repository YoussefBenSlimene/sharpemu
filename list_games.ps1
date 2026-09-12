# Print title / titleId for every game folder under ps5-games.
foreach ($d in Get-ChildItem 'C:\ps5-emulator\ps5-games' -Directory) {
    $p = Join-Path $d.FullName 'sce_sys\param.json'
    if (Test-Path $p) {
        $raw = Get-Content $p -Raw
        $title = ''
        $titleId = ''
        if ($raw -match '"title"\s*:\s*"([^"]*)"') { $title = $Matches[1] }
        if ($raw -match '"titleId"\s*:\s*"([^"]*)"') { $titleId = $Matches[1] }
        Write-Host ($d.Name + ' | ' + $title + ' | ' + $titleId)
    }
}
