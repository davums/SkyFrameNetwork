$ErrorActionPreference = 'Stop'
$serviceName = 'SkyframeNode'
$installRoot = Split-Path $PSScriptRoot -Parent
$nodeExe = Join-Path $installRoot 'Ledger.Node\Ledger.Node.exe'
$dataDirectory = Join-Path $env:ProgramData 'Skyframe\data'
if (-not (Test-Path -LiteralPath $nodeExe)) { throw "Node executable not found: $nodeExe" }
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
& icacls.exe $dataDirectory /inheritance:e /grant '*S-1-5-32-545:(OI)(CI)M' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set permissions on the Skyframe data directory.' }
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
    & sc.exe delete $serviceName | Out-Null
    for ($i = 0; $i -lt 20 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
}
$binaryPath = '"{0}" --data-dir "{1}" --port 5001' -f $nodeExe, $dataDirectory
New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'Skyframe Node Service' -Description 'Skyframe peer-to-peer node; started by the Dashboard after wallet onboarding.' -StartupType Manual | Out-Null
# Authenticated users may query/start/stop through the Dashboard; the service still runs as LocalSystem.
& sc.exe sdset $serviceName 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRPWPLO;;;AU)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not grant the Dashboard permission to start and stop the manual node service.' }
& netsh.exe advfirewall firewall delete rule name='Skyframe Node TCP 5001' | Out-Null
& netsh.exe advfirewall firewall add rule name='Skyframe Node TCP 5001' dir=in action=allow protocol=TCP localport=5001 profile=private,domain | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not add the Skyframe P2P firewall rule.' }