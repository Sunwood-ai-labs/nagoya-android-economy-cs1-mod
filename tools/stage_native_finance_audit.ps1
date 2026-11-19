[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^NagoyaCitywide-[\w.-]+$')][string]$CheckpointName,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CheckpointSha256,
    [ValidateRange(50, 150)][int]$ElectricityBudgetPercent,
    [ValidateRange(50, 150)][int]$ExpectedElectricityDay,
    [ValidateRange(50, 150)][int]$ExpectedElectricityNight,
    [string]$Bridge = 'http://127.0.0.1:32123'
)

$ErrorActionPreference = 'Stop'
$change = $PSBoundParameters.ContainsKey('ElectricityBudgetPercent')
if ($change -and ($CheckpointName -cnotlike 'NagoyaCitywide-PowerBudgetTrial-*' -or
    !$PSBoundParameters.ContainsKey('ExpectedElectricityDay') -or !$PSBoundParameters.ContainsKey('ExpectedElectricityNight'))) {
    throw 'An electricity budget change requires the named trial checkpoint and both expected values.'
}
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$finance = Get-Content -LiteralPath (Join-Path $root 'finance-runtime.json') -Raw | ConvertFrom-Json
$live = Invoke-RestMethod "$Bridge/state/summary" -TimeoutSec 6
$files = @((Invoke-RestMethod "$Bridge/state/saves" -TimeoutSec 5).saves | Where-Object name -CEQ $CheckpointName)
$resume = Get-Content -LiteralPath "$env:LOCALAPPDATA/Colossal Order/Cities_Skylines/continue_game.json" -Raw | ConvertFrom-Json
if (!$live.ok -or !$live.simulation.loadingComplete -or !$live.simulation.paused -or
    $finance.phase -cnotin 'ready','complete','rejected' -or $finance.cityName -cne 'NagoyaCitywide-Construction' -or
    $resume.title -cne $CheckpointName -or $files.Count -ne 1 -or
    (Get-FileHash -LiteralPath $files[0].path).Hash -ne $CheckpointSha256) {
    throw 'The paused Nagoya checkpoint must match the finance runtime.'
}

$destination = Join-Path $root 'finance-command.json'
if (Test-Path -LiteralPath $destination) {
    $prior = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
    if ($prior.runId -ceq $finance.runId -and $prior.commandId -cne $finance.commandId) {
        throw 'A finance command is already pending.'
    }
}
$request = @{
    runId = $finance.runId
    commandId = [Guid]::NewGuid().ToString('N')
    cityName = $finance.cityName
    buildIndex = $live.buildIndex
    segments = $live.network.segments
}
if ($change) {
    $request.electricityBudget = @{
        percent = $ElectricityBudgetPercent
        expectedDay = $ExpectedElectricityDay
        expectedNight = $ExpectedElectricityNight
    }
}
$temporary = "$destination.$($request.commandId).tmp"
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $destination) {
    [IO.File]::Replace($temporary, $destination, [NullString]::Value)
} else {
    [IO.File]::Move($temporary, $destination)
}
[pscustomobject]$request
