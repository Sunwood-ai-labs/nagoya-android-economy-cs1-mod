[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Ward,
    [Parameter(Mandatory)][string]$CheckpointName,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CheckpointSha256,
    [ValidateRange(1,3)][int]$LimitPerWard = 2,
    [switch]$PreferExistingWorkers,
    [switch]$ExistingWorkersOnly,
    [switch]$DispatchAtWork,
    [uint16[]]$StartupFactoryId = @(),
    [uint16[]]$StartupShopId = @(),
    [uint16[]]$StartupHomeId = @()
)

$ErrorActionPreference = 'Stop'
if ($DispatchAtWork -and ($PreferExistingWorkers -or $ExistingWorkersOnly)) {
    throw 'Choose live dispatch at work or fixed worker assignments.'
}
if ($StartupFactoryId.Count -gt 48 -or $StartupShopId.Count -gt 48 -or $StartupHomeId.Count -gt 48 -or
    0 -in $StartupFactoryId -or 0 -in $StartupShopId -or 0 -in $StartupHomeId) {
    throw 'Select at most 48 valid IDs per building type.'
}
if ($ExistingWorkersOnly) {
    if ($StartupShopId.Count -eq 0 -or $StartupHomeId.Count -ne 0 -or $PreferExistingWorkers) {
        throw 'Select one or more receiving shops without recruiting from housing.'
    }
} elseif ((($StartupFactoryId.Count -eq 0) -ne ($StartupShopId.Count -eq 0)) -or
    ($StartupHomeId.Count -gt 0 -and $StartupFactoryId.Count -eq 0) -or
    ($StartupFactoryId.Count -gt 0 -and ($DispatchAtWork -or $PreferExistingWorkers))) {
    throw 'Provide both new factory and store IDs for starting a local work assignment.'
}
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$bridge = 'http://127.0.0.1:32123'
$roster = Get-Content -Raw -LiteralPath "$root/native-city-roster.json" | ConvertFrom-Json
$life = Get-Content -Raw -LiteralPath "$root/android-life-state.json" | ConvertFrom-Json
$state = Invoke-RestMethod "$bridge/state/summary" -TimeoutSec 5
$checkpoint = @((Invoke-RestMethod "$bridge/state/saves" -TimeoutSec 5).saves | Where-Object name -CEQ $CheckpointName)
$resume = Get-Content -Raw -LiteralPath "$env:LOCALAPPDATA/Colossal Order/Cities_Skylines/continue_game.json" | ConvertFrom-Json
if ($checkpoint.Count -ne 1 -or $resume.title -cne $CheckpointName -or
    $CheckpointName -notlike 'NagoyaCitywide-*' -or (Get-FileHash -LiteralPath $checkpoint[0].path).Hash -ne $CheckpointSha256) {
    throw 'The resumed game save must match the named checkpoint.'
}
if (!$state.simulation.loadingComplete -or !$state.simulation.paused -or $roster.city -cne 'NagoyaCitywide-Construction' -or
    [DateTimeOffset]::UtcNow - [DateTimeOffset]$life.utc -gt [TimeSpan]::FromSeconds(20) -or
    $null -eq $life.nativeDeliveries -or $null -ne $life.localDeliveryAdmissions) {
    throw 'The native city must be paused before the first local admission request.'
}
$Ward = @($Ward | Sort-Object -Unique)
if ($Ward.Count -eq 0 -or $Ward.Count -gt 16 -or @($Ward | Where-Object {$_ -notin $roster.wards.PSObject.Properties.Name -or $_ -eq 'unassigned'}).Count) {
    throw 'Choose districts present in the native city roster.'
}
$request=@{
    runId=$life.runId
    caseId='local-delivery-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
    localDeliveryWards=$Ward
    limitPerWard=$LimitPerWard
    preferExistingWorkers=[bool]$PreferExistingWorkers
    existingWorkersOnly=[bool]$ExistingWorkersOnly
    dispatchAtWork=[bool]$DispatchAtWork
    startupFactoryIds=@($StartupFactoryId | Sort-Object -Unique)
    startupShopIds=@($StartupShopId | Sort-Object -Unique)
    startupHomeIds=@($StartupHomeId | Sort-Object -Unique)
    expectedBuildIndex=$state.buildIndex
    expectedSegments=$state.network.segments
}
$destination="$root/android-life-scenario.json"
$pending="$destination.$($request.caseId).tmp"
[IO.File]::WriteAllText($pending, ($request|ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($pending,$destination,[NullString]::Value) }
else { [IO.File]::Move($pending,$destination) }
[pscustomobject]$request
