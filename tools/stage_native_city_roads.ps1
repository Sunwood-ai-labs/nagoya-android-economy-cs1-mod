[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Plan,
    [Parameter(Mandatory)][string]$ExpectedCityName,
    [Parameter(Mandatory)][string]$CheckpointName,
    [string]$Bridge = 'http://127.0.0.1:32123',
    [switch]$Infrastructure,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$file = Get-Item -LiteralPath $Plan
if ($file.Extension -ne '.tsv' -or $file.Directory.FullName -cne (Get-Item -LiteralPath $root).FullName) {
    throw 'Use a compiled TSV in the isolated citywide staging directory.'
}

$channel = if ($Infrastructure) { 'utility' } else { 'road' }
$runtime = Get-Content -LiteralPath (Join-Path $root "$channel-runtime.json") -Raw | ConvertFrom-Json
$expectedPhase = if ($Execute) { 'preflight_passed' } else { 'ready' }
$hash = (Get-FileHash -LiteralPath $file.FullName).Hash
$summary = Invoke-RestMethod "$Bridge/state/summary" -TimeoutSec 5
$area = Invoke-RestMethod "$Bridge/state/map-areas" -TimeoutSec 5
if ($runtime.phase -cne $expectedPhase -or $runtime.loadedCity -cne $ExpectedCityName -or
    $ExpectedCityName -notlike 'NagoyaCitywide-*' -or $area.areaCount -ne 25 -or
    $summary.network.nodes -ne $runtime.baselineNodes -or
    $summary.network.segments -ne $runtime.baselineSegments -or
    ($Execute -and $hash -cne $runtime.planSha256)) {
    throw 'The isolated NagoyaCitywide city, network snapshot and 25 open tiles must match.'
}

$checkpoint = @((Invoke-RestMethod "$Bridge/state/saves" -TimeoutSec 5).saves |
    Where-Object name -eq $CheckpointName)
$checkpointPattern = if ($Infrastructure) { 'NagoyaCitywide-*-Connected' } else { 'NagoyaCitywide-*-Baseline' }
if ($CheckpointName -notlike $checkpointPattern -or
    $checkpoint.Count -ne 1 -or $checkpoint[0].length -lt 500000) {
    throw 'The separate native construction baseline must exist before executing.'
}

$request = @{
    runId = $runtime.runId
    commandId = [Guid]::NewGuid().ToString('N')
    expectedCityName = $ExpectedCityName
    expectedNodes = $runtime.baselineNodes
    expectedSegments = $runtime.baselineSegments
    plan = $file.Name
    sha256 = $hash
    execute = [bool]$Execute
}
$destination = Join-Path $root "$channel-command.json"
$temporary = $destination + '.' + $request.commandId + '.tmp'
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $destination) {
    [IO.File]::Replace($temporary, $destination, [NullString]::Value)
} else {
    [IO.File]::Move($temporary, $destination)
}
[pscustomobject]$request
