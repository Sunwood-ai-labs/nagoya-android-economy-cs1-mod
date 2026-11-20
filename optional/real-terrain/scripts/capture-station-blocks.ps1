[CmdletBinding()]
param(
    [ValidateSet('survey','apply')][string]$Phase = 'survey',
    [ValidatePattern('^0[1-8]$')][string]$Block
)

$ErrorActionPreference = 'Stop'
$api = 'http://127.0.0.1:32123'
$audit = Join-Path $PSScriptRoot 'station-precinct/density-audit'
$data = Join-Path $PSScriptRoot 'station-precinct/blocks'
$blocks = Get-Content -Raw -LiteralPath (Join-Path $audit 'block-views.json') | ConvertFrom-Json
if ($Block) { $blocks = @($blocks | Where-Object { $_.id -eq $Block }) }
if ($blocks.Count -lt 1 -or $blocks.Count -gt 8) { throw 'Invalid block selection.' }
$state = Invoke-RestMethod "$api/state/summary" -TimeoutSec 5
if ($state.network.segments -ne 19380 -or $state.network.nodes -ne 17473) { throw 'The intended Nagoya map is not open.' }

function Capture-Block($BlockView, $Prefix) {
    $name = "nagoya-block-$($BlockView.id)-$Prefix.png"
    $body = @{name=$name; x=$BlockView.camera.x; z=$BlockView.camera.z; zoom=$BlockView.camera.zoom; yaw=0; pitch=88}
    $requested = [DateTime]::UtcNow
    $capture = Invoke-RestMethod "$api/commands/nagoya-capture" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 6
    Start-Sleep -Milliseconds 2100
    $image = Get-Item -LiteralPath (Join-Path $PSScriptRoot $name)
    if (!$capture.ok -or $image.LastWriteTimeUtc -lt $requested -or $image.Length -lt 50000) { throw "Fresh screenshot missing for $name." }
    $hash = (Get-FileHash -LiteralPath $image.FullName).Hash
    @{name=$name; sha256=$hash; capturedAt=$image.LastWriteTime.ToString('o'); settings=$body}
}

foreach ($view in $blocks) {
    $id = $view.id
    if ($Phase -eq 'survey') {
        $result = Capture-Block $view 'survey'
        "$id $($result.name) $($result.sha256)"
        continue
    }
    $recordPath = Join-Path $data "block-$id-runtime.json"
    if (Test-Path -LiteralPath $recordPath) { throw "Block $id already has a recorded application." }
    $plan = Get-Content -Raw -LiteralPath (Join-Path $data "block-$id-plan.json") | ConvertFrom-Json
    if ($plan.id -ne $id -or $plan.triangles -le 0) { throw "Block $id has no validated plan." }
    $before = Capture-Block $view 'before'
    $applied = Invoke-RestMethod "$api/commands/nagoya-place-verified-block?block=$id" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 12
    if (!$applied.ok -or $applied.block -ne $id -or $applied.triangles -ne $plan.triangles -or $applied.renderObjects -ne $plan.renderObjects -or $applied.alreadyApplied) { throw "Live read-back does not match block $id plan." }
    $after = Capture-Block $view 'after'
    @{id=$id; name=$view.name; bounds=$view.bounds; before=$before; applied=$applied; after=$after} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $recordPath -Encoding utf8
    py -3.12 (Join-Path $PSScriptRoot 'record_station_block.py') $id
    if ($LASTEXITCODE -ne 0) { throw "Evidence archive failed for block $id." }
    "$id $($plan.triangles) triangles; $($before.name) -> $($after.name)"
}
