param([switch]$DeleteWallet)
$ErrorActionPreference = 'Stop'
$serviceName = 'SkyframeNode'
$dataDirectory = Join-Path $env:ProgramData 'Skyframe\data'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(25)) }
    & sc.exe delete $serviceName | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'Skyframe Node service is still registered; retry uninstall after it stops.' }
}
& netsh.exe advfirewall firewall delete rule name='Skyframe Node TCP 5001' | Out-Null
if (Test-Path -LiteralPath $dataDirectory) {
    Remove-Item -LiteralPath (Join-Path $dataDirectory 'peers.json') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $dataDirectory 'peers.json.tmp') -Force -ErrorAction SilentlyContinue
    if ($DeleteWallet) {
        Remove-Item -LiteralPath (Join-Path $dataDirectory 'node_identity.json') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $dataDirectory 'node_machine_key.dpapi') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $dataDirectory 'wallet_config.json') -Force -ErrorAction SilentlyContinue
    }
}