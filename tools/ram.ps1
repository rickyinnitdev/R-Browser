param([string]$Url = "https://www.cnn.com/", [int]$Wait = 12)
$env:R1_LOG = "1"
Get-Process RBrowser -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process "D:\R1\bin\Debug\net9.0-windows\RBrowser.exe" -ArgumentList $Url -PassThru
Start-Sleep -Seconds $Wait
$mark = "$env:LOCALAPPDATA\R1"
$rows = @()
$me = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($me) { $rows += [pscustomobject]@{ Type = "host"; Id = $p.Id; WS = [math]::Round($me.WorkingSet64 / 1MB); Priv = [math]::Round($me.PrivateMemorySize64 / 1MB) } }
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | ForEach-Object {
    if ($_.CommandLine -like "*$mark*") {
        $wp = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        if ($wp) {
            $type = if ($_.CommandLine -match "--type=([a-z-]+)") { $matches[1] } else { "browser" }
            $rows += [pscustomobject]@{ Type = $type; Id = $_.ProcessId; WS = [math]::Round($wp.WorkingSet64 / 1MB); Priv = [math]::Round($wp.PrivateMemorySize64 / 1MB) }
        }
    }
}
$rows | Sort-Object WS -Descending | Format-Table -AutoSize | Out-String -Width 120
"working set MB: " + [math]::Round((($rows | Measure-Object WS -Sum).Sum)) + " | private MB: " + [math]::Round((($rows | Measure-Object Priv -Sum).Sum)) + " | processes: " + $rows.Count
Stop-Process $p -Force
