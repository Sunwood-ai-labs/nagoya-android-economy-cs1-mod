[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^NagoyaCitywide-[\w.-]+$')][string]$CheckpointName,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CheckpointSha256,
    [string]$Bridge = 'http://127.0.0.1:32123'
)

$ErrorActionPreference = 'Stop'
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$zone = Get-Content -LiteralPath (Join-Path $root 'zone-runtime.json') -Raw | ConvertFrom-Json
$live = Invoke-RestMethod "$Bridge/state/summary" -TimeoutSec 6
$files = @((Invoke-RestMethod "$Bridge/state/saves" -TimeoutSec 5).saves | Where-Object name -CEQ $CheckpointName)
$loaded = Get-Content -LiteralPath "$env:LOCALAPPDATA/Colossal Order/Cities_Skylines/continue_game.json" -Raw | ConvertFrom-Json
if (!$live.ok -or !$live.simulation.loadingComplete -or !$live.simulation.paused -or
    $zone.phase -cne 'ready' -or $zone.loadedCity -cne 'NagoyaCitywide-Construction' -or
    $loaded.title -cne $CheckpointName -or $files.Count -ne 1 -or
    (Get-FileHash -LiteralPath $files[0].path).Hash -ne $CheckpointSha256) {
    throw 'The named paused Nagoya save must match the current zoning runtime.'
}

$request = @{
    runId = $zone.runId
    commandId = [Guid]::NewGuid().ToString('N')
    expectedCityName = $zone.loadedCity
    expectedBuildIndex = $live.buildIndex
    expectedSegments = $live.network.segments
}
$destination = Join-Path $root 'vacancy-command.json'
$temporary = "$destination.$($request.commandId).tmp"
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $destination) {
    [IO.File]::Replace($temporary, $destination, [NullString]::Value)
} else {
    [IO.File]::Move($temporary, $destination)
}
[pscustomobject]$request
