[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Plan,
    [Parameter(Mandatory)][string]$RoadCommandId,
    [Parameter(Mandatory)][string]$ExpectedCityName,
    [Parameter(Mandatory)][string]$CheckpointName,
    [string]$Bridge = 'http://127.0.0.1:32123'
)

$ErrorActionPreference = 'Stop'
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
if ($RoadCommandId -cnotmatch '^[A-Za-z0-9_-]{1,100}$') { throw 'Invalid road receipt identity.' }
$file = Get-Item -LiteralPath $Plan
if ($file.Extension -ne '.tsv' -or $file.Directory.FullName -cne (Get-Item -LiteralPath $root).FullName) {
    throw 'Use a compiled zoning TSV in the isolated citywide stage.'
}

$zone = Get-Content -LiteralPath (Join-Path $root 'zone-runtime.json') -Raw | ConvertFrom-Json
$roads = Get-Content -LiteralPath (Join-Path $root 'road-runtime.json') -Raw | ConvertFrom-Json
$summary = Invoke-RestMethod "$Bridge/state/summary" -TimeoutSec 5
if ($zone.phase -cne 'ready' -or $zone.loadedCity -cne $ExpectedCityName -or
    $ExpectedCityName -notlike 'NagoyaCitywide-*' -or
    $roads.commandId -cne $RoadCommandId -or $roads.loadedCity -cne $ExpectedCityName -or
    $roads.phase -notin 'built','built_partial' -or $roads.createdRoads -le 0 -or
    $summary.network.segments -ne $roads.nativeSegments) {
    throw 'The current native city and the completed road import must match.'
}

$checkpoint = @((Invoke-RestMethod "$Bridge/state/saves" -TimeoutSec 5).saves |
    Where-Object name -eq $CheckpointName)
if ($CheckpointName -notlike 'NagoyaCitywide-*-Roads' -or
    $checkpoint.Count -ne 1 -or $checkpoint[0].length -lt 500000) {
    throw 'The isolated native road checkpoint must be recorded first.'
}

$roadReceipt = Get-Item -LiteralPath (Join-Path $root "$RoadCommandId-roads.tsv")
$request = @{
    runId = $zone.runId
    commandId = [Guid]::NewGuid().ToString('N')
    expectedCityName = $ExpectedCityName
    roadCommandId = $RoadCommandId
    roadSha256 = (Get-FileHash -LiteralPath $roadReceipt.FullName).Hash
    plan = $file.Name
    planSha256 = (Get-FileHash -LiteralPath $file.FullName).Hash
}
$destination = Join-Path $root 'zone-command.json'
$temporary = $destination + '.' + $request.commandId + '.tmp'
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $destination) {
    [IO.File]::Replace($temporary, $destination, [NullString]::Value)
} else {
    [IO.File]::Move($temporary, $destination)
}
[pscustomobject]$request
