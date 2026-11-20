[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$api = 'http://127.0.0.1:32123'
$audit = Join-Path $PSScriptRoot 'station-precinct/density-audit'
$plan = Get-Content -Raw -LiteralPath (Join-Path $audit 'density-build.json') | ConvertFrom-Json
$baseline = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'station-precinct/final-runtime-verified.json') | ConvertFrom-Json
$city = Invoke-RestMethod "$api/state/summary" -TimeoutSec 7
$models = Invoke-RestMethod "$api/commands/nagoya-place-visuals" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 18
$utilities = Invoke-RestMethod "$api/state/nagoya-unlimited-utilities" -TimeoutSec 7
if (!$models.stationPrecinct -or $models.renderObjects -ne $plan.manifestRowsAfter -or
    $models.triangles -ne ($baseline.models.triangles + $plan.trianglesAdded) -or
    $models.plateauSourceBuildings -ne $baseline.models.plateauSourceBuildings -or
    $city.network.segments -ne $baseline.roadSegments -or !$utilities.enabled) {
    throw 'Live game counts do not match the surveyed station manifest and map.'
}

& (Join-Path $PSScriptRoot 'capture-station-density.ps1') -Phase after
$game = Get-Process -Name Cities -ErrorAction Stop
$manifest = Join-Path $PSScriptRoot 'refined-visuals-citywide-station.tsv'
$files = @('overview','east','west','north','south') | ForEach-Object {
    $filename = "nagoya-station-density-after-$_.png"
    @{ name=$filename; sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $filename)).Hash }
}
$record = @{ map=$baseline.map; pid=$game.Id; observedAt=(Get-Date).ToString('o'); models=$models;
    roads=$city.network; unlimitedUtilities=$utilities.enabled; captures=$files;
    manifestSha256=(Get-FileHash -LiteralPath $manifest).Hash }
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $audit 'density-runtime-verified.json') -Encoding utf8
$record | ConvertTo-Json -Depth 6
