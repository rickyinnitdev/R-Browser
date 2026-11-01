param([string]$Url, [string]$Expr = "document.title", [int]$Wait = 8, [switch]$Verbose)
$env:R1_OFFSCREEN = "1"
$env:R1_DEBUG_PORT = "9333"
$env:R1_LOG = "1"
Get-Process RBrowser -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-Item "$env:LOCALAPPDATA\R1\r1.log" -ErrorAction SilentlyContinue
$p = Start-Process "D:\R1\bin\Debug\net9.0-windows\RBrowser.exe" -ArgumentList $Url -PassThru
Start-Sleep -Seconds $Wait
$extra = @()
if ($Verbose) { $extra += "--verbose" }
$out = & node D:\R1\tools\cdp.mjs 9333 "$Expr" @extra 2>&1
"--- $Url"
$out
if (-not $p.HasExited) { "ram: " + [math]::Round($p.WorkingSet64 / 1MB) + " MB" } else { "R1 EXITED code=" + $p.ExitCode }
if (Test-Path "$env:LOCALAPPDATA\R1\r1.log") { "--- log"; Get-Content "$env:LOCALAPPDATA\R1\r1.log" -Tail 40 }
if (-not $p.HasExited) { Stop-Process $p -Force }
