[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Ward,
    [Parameter(Mandatory)][string]$CheckpointName,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CheckpointSha256,
    [ValidateRange(1,50)][int]$LimitPerWard = 5,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$city = 'NagoyaCitywide-Construction'
$bridge = 'http://127.0.0.1:32123'
$zone = Get-Content -Raw -LiteralPath "$root/zone-runtime.json" | ConvertFrom-Json
$roster = Get-Content -Raw -LiteralPath "$root/native-city-roster.json" | ConvertFrom-Json
$state = Invoke-RestMethod "$bridge/state/summary" -TimeoutSec 5
$saves = Invoke-RestMethod "$bridge/state/saves" -TimeoutSec 5
$checkpoint = @($saves.saves | Where-Object name -CEQ $CheckpointName)
$sessionRoot = "$env:LOCALAPPDATA/Colossal Order/Cities_Skylines"
$current = Get-Content -Raw -LiteralPath "$sessionRoot/continue_game.json" | ConvertFrom-Json
if ($CheckpointName -notlike 'NagoyaCitywide-*' -or $current.title -cne $CheckpointName -or
    $checkpoint.Count -ne 1 -or (Get-FileHash -LiteralPath $checkpoint[0].path).Hash -ne $CheckpointSha256) {
    throw 'The resumed save must match the archived checkpoint.'
}
if (!$state.ok -or !$state.simulation.loadingComplete -or !$state.simulation.paused -or
    $zone.loadedCity -cne $city -or $roster.city -cne $city -or $zone.phase -cne 'ready' -or
    [DateTimeOffset]::UtcNow - [DateTimeOffset]$roster.utc -gt [TimeSpan]::FromSeconds(20)) {
    throw 'The isolated city must be loaded and paused.'
}
$Ward = @($Ward | Sort-Object -Unique)
if ($Ward.Count -eq 0 -or $Ward.Count -gt 16 -or @($Ward | Where-Object { $_ -notin $roster.wards.PSObject.Properties.Name -or $_ -eq 'unassigned' }).Count) {
    throw 'Choose actual districts in the loaded city.'
}

$request = @{
    runId = $zone.runId
    commandId = [Guid]::NewGuid().ToString('N')
    expectedCityName = $city
    repairFrontages = @{
        wards = $Ward
        limitPerWard = $LimitPerWard
        apply = [bool]$Apply
        expectedBuildIndex = $state.buildIndex
        expectedSegments = $state.network.segments
    }
}
$destination = Join-Path $root 'zone-command.json'
$temporary = $destination + '.' + $request.commandId + '.tmp'
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $destination) {
    [IO.File]::Replace($temporary, $destination, [NullString]::Value)
} else {
    [IO.File]::Move($temporary, $destination)
}
[pscustomobject]$request
