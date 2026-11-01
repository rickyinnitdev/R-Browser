param([string]$Output = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "dist" }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet publish (Join-Path $root "R1.csproj") -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=false -p:DebugType=none -o $Output

# Keep the internal pages next to the executable.
New-Item -ItemType Directory -Force (Join-Path $Output "assets") | Out-Null
Copy-Item (Join-Path $root "assets\*.html") (Join-Path $Output "assets") -Force
Copy-Item (Join-Path $root "assets\*.css") (Join-Path $Output "assets") -Force

$exe = Join-Path $Output "RBrowser.exe"
if (-not (Test-Path $exe)) { throw "publish failed: $exe missing" }
$size = [math]::Round((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
"published: $exe ({0} MB total)" -f $size
