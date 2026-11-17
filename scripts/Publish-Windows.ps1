$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishRoot = Join-Path $root 'artifacts\publish'
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
$dashboard = Join-Path $publishRoot 'Ledger.Dashboard'
$node = Join-Path $publishRoot 'Ledger.Node'
& dotnet publish (Join-Path $root 'Ledger.Dashboard\Ledger.Dashboard.csproj') -c Release -f net9.0-windows10.0.19041.0 -r win-x64 --self-contained true -p:WindowsPackageType=None -o $dashboard
if ($LASTEXITCODE -ne 0) { throw 'Dashboard publish failed.' }
& dotnet publish (Join-Path $root 'Ledger.Node\Ledger.Node.csproj') -c Release -f net9.0 -r win-x64 --self-contained true -o $node
if ($LASTEXITCODE -ne 0) { throw 'Node publish failed.' }
Write-Output "Publish output ready at $publishRoot. Compile installer\SkyframeNetwork.iss with Inno Setup 6 (ISCC.exe)."
