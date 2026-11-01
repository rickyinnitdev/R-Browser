param([string]$Output = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "dist-linux" }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet publish (Join-Path $root "R1.Linux\R1.Linux.csproj") -f net9.0 -c Release -r linux-x64 --self-contained true `
  -p:PublishSingleFile=true -o $Output

# Copy local internal pages
New-Item -ItemType Directory -Force (Join-Path $Output "assets") | Out-Null
Copy-Item (Join-Path $root "assets\*.html") (Join-Path $Output "assets") -Force
Copy-Item (Join-Path $root "assets\*.css") (Join-Path $Output "assets") -Force

# Create compressed tarball for easy transfer
$tar = Join-Path $root "r-browser-linux-x64.tar.gz"
if (Test-Path $tar) { Remove-Item $tar -Force }
tar -czf $tar -C $Output RBrowser assets

$bin = Join-Path $Output "RBrowser"
if (-not (Test-Path $bin)) { throw "publish failed: $bin missing" }
$size = [math]::Round((Get-Item $bin).Length / 1MB)
$tarSize = [math]::Round((Get-Item $tar).Length / 1MB)
"published: $bin ({0} MB binary, {1} MB compressed tarball at {2})" -f $size, $tarSize, $tar
