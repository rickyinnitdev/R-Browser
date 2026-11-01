param([int]$Samples = 3, [int]$IntervalSeconds = 5)
$ErrorActionPreference = 'Stop'
$Samples = [Math]::Clamp($Samples, 1, 60)
$IntervalSeconds = [Math]::Clamp($IntervalSeconds, 1, 30)
for ($sample = 1; $sample -le $Samples; $sample++) {
    $processes = @(Get-CimInstance Win32_Process)
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($item in $processes) { if ($item.Name -eq 'RBrowser.exe') { [void]$ids.Add([int]$item.ProcessId) } }
    do {
        $added = 0
        foreach ($item in $processes) {
            if ($ids.Contains([int]$item.ParentProcessId) -and $ids.Add([int]$item.ProcessId)) { $added++ }
        }
    } while ($added -gt 0)
    $rows = @(foreach ($item in $processes) {
        if (-not $ids.Contains([int]$item.ProcessId)) { continue }
        $p = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $p) { continue }
        $role = if ($item.Name -eq 'RBrowser.exe') { 'shell' } elseif ($item.CommandLine -match '--type=([^ ]+)') { $matches[1] } else { 'webview-browser' }
        [pscustomobject]@{ PID = $p.Id; Role = $role; WorkingSetMiB = [Math]::Round($p.WorkingSet64 / 1MB, 1); PrivateCommittedMiB = [Math]::Round($p.PrivateMemorySize64 / 1MB, 1) }
    })
    [pscustomobject]@{
        Sample = $sample
        Time = (Get-Date).ToString('o')
        Processes = $rows
        WorkingSetMiB = [Math]::Round(($rows | Measure-Object WorkingSetMiB -Sum).Sum, 1)
        PrivateCommittedMiB = [Math]::Round(($rows | Measure-Object PrivateCommittedMiB -Sum).Sum, 1)
    } | ConvertTo-Json -Depth 4 -Compress
    if ($sample -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
}
