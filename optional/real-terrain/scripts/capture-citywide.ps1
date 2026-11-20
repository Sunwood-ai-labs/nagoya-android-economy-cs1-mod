[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$url = 'http://127.0.0.1:32123'
$verified = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Nagoya-Real-Citywide-20261120-reload-verified.json') |
    ConvertFrom-Json
$state = Invoke-RestMethod "$url/state/summary" -TimeoutSec 5
if ($state.network.segments -ne $verified.roads) {
    throw 'The verified Nagoya citywide map is not open.'
}

$views = @(
    @{name='nagoya-citywide-verified-urban.png'; x=-470; z=100; zoom=5300; yaw=16; pitch=61},
    @{name='nagoya-citywide-verified-east.png'; x=2440; z=540; zoom=2400; yaw=160; pitch=47},
    @{name='nagoya-citywide-verified-port.png'; x=-1457; z=-2196; zoom=1900; yaw=37; pitch=60},
    @{name='nagoya-citywide-verified-station.png'; x=-1438; z=948; zoom=890; yaw=20; pitch=58}
)
foreach ($view in $views) {
    $body = $view | ConvertTo-Json -Compress
    $result = Invoke-RestMethod "$url/commands/nagoya-capture" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 6
    Start-Sleep -Milliseconds 950
    if (-not $result.ok -or !(Test-Path -LiteralPath $result.path -PathType Leaf)) {
        throw "Native screenshot was not saved: $($view.name)"
    }
    Get-Item -LiteralPath $result.path | Select-Object Name, Length, LastWriteTime
}
