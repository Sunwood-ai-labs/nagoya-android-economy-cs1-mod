[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataRoot,
    [string]$Plan = (Join-Path $PSScriptRoot 'station-plateau-zoning.json'),
    [string]$BridgeUrl = 'http://127.0.0.1:32123'
)

$ErrorActionPreference = 'Stop'
$save = (Invoke-RestMethod "$BridgeUrl/state/saves" -TimeoutSec 6).saves |
    Where-Object name -eq 'NagoyaFlat-BeforePlateauZoning-20261119'
if (!$save -or $save.length -lt 5000000) { throw 'The pre-zoning game save was not verified.' }

$scene = Get-Content -Raw -LiteralPath (Join-Path $DataRoot 'runtime/block-runtime.json') | ConvertFrom-Json
$citywide = Join-Path $DataRoot 'citywide-live-v1'
$state = Get-Content -Raw -LiteralPath (Join-Path $citywide 'state.json') | ConvertFrom-Json
if ($scene.revision -ne 'nagoya-city738033-flat140-roof-g-split-v11' -or
    $state.phase -ne 'running' -or $state.roadChunksLoaded -ne 447) { throw 'The expected Nagoya scene is not ready.' }
$request = Get-Content -Raw -LiteralPath $Plan | ConvertFrom-Json
foreach ($binding in $request.bindings) {
    $mesh = @($scene.models | Where-Object { $_.id -ceq $binding.source -and $_.active })
    if ($mesh.Count -ne 1) { throw "PLATEAU model is not uniquely visible: $($binding.source)" }
    $segment = Invoke-RestMethod "$BridgeUrl/state/network-segment-details?id=$($binding.road)" -TimeoutSec 5
    if (!$segment.ok -or $segment.prefab -cne 'Basic Road') { throw "Native frontage differs: $($binding.road)" }
}

$request | Add-Member -NotePropertyName runId -NotePropertyValue $state.runId
$path = Join-Path $citywide 'plateau-zoning-plan.json'
if (Test-Path -LiteralPath $path) { throw 'A zoning plan is already staged.' }
$temporary = "$path.pending"
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporary, $path)
[pscustomobject]@{runId=$state.runId; plannedBuildings=$request.bindings.Count; plan=$path}
