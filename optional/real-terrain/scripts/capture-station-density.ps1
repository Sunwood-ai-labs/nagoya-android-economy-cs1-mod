[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('before', 'after')][string]$Phase)

$ErrorActionPreference = 'Stop'
$api = 'http://127.0.0.1:32123'
$city = Invoke-RestMethod "$api/state/summary" -TimeoutSec 5
if ($city.network.segments -ne 19380 -or $city.network.nodes -ne 17473) {
    throw 'The intended Nagoya neighborhoods map is not loaded.'
}
$views = @(
    @{sector='east'; x=-1349; z=964; zoom=195; yaw=0; pitch=88},
    @{sector='west'; x=-1504; z=946; zoom=205; yaw=0; pitch=88},
    @{sector='north'; x=-1440; z=1049; zoom=220; yaw=0; pitch=88},
    @{sector='south'; x=-1385; z=862; zoom=210; yaw=0; pitch=88},
    @{sector='overview'; x=-1420; z=945; zoom=390; yaw=0; pitch=88}
)
foreach ($view in $views) {
    $sector = $view.sector
    $name = "nagoya-station-density-$Phase-$sector.png"
    $pose = @{ name=$name; x=$view.x; z=$view.z; zoom=$view.zoom; yaw=$view.yaw; pitch=$view.pitch }
    $requested = [DateTime]::UtcNow
    $capture = Invoke-RestMethod "$api/commands/nagoya-capture" -Method Post -ContentType 'application/json' -Body ($pose | ConvertTo-Json -Compress) -TimeoutSec 5
    Start-Sleep -Milliseconds 2200
    $image = Get-Item -LiteralPath (Join-Path $PSScriptRoot $name)
    if (!$capture.ok -or $image.Length -lt 50000 -or $image.LastWriteTimeUtc -lt $requested) {
        throw "A fresh capture was not saved for $sector."
    }
    $image | Select-Object Name,Length
}
