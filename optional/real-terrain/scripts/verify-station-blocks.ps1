[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('save', 'readback')][string]$Phase)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'station-precinct/blocks'
$expected = @(1..8 | ForEach-Object { '{0:00}' -f $_ })
$api = 'http://127.0.0.1:32123'
$active = Invoke-RestMethod "$api/state/nagoya-verified-blocks" -TimeoutSec 6
$city = Invoke-RestMethod "$api/state/summary" -TimeoutSec 6
$statePath = Join-Path $root 'verified-blocks.txt'
if (!$active.ok -or $active.blocks.Count -ne $expected.Count -or $city.network.segments -ne 19380) {
    throw 'The eight station blocks are not present on the target map.'
}
$records = foreach ($id in $expected) {
    $record = Get-Content -Raw -LiteralPath (Join-Path $root "block-$id-runtime.json") | ConvertFrom-Json
    $item = @($active.blocks | Where-Object { $_.block -eq $id })
    if ($item.Count -ne 1 -or $item[0].triangles -ne $record.applied.triangles -or $item[0].renderObjects -ne 1) {
        throw "Live block $id disagrees with its audit record."
    }
    [pscustomobject]@{block=$id; triangles=$item[0].triangles; manifestSha256=(Get-FileHash -LiteralPath (Join-Path $root "block-$id.tsv")).Hash}
}
$game = Get-Process -Name Cities -ErrorAction Stop
$snapshot = @{phase=$Phase; pid=$game.Id; observedAt=(Get-Date).ToString('o'); segments=$city.network.segments; blocks=$records}
if ($Phase -eq 'save') {
    if (Test-Path -LiteralPath $statePath) { throw 'The activation list already exists.' }
    $temp = Join-Path $root 'verified-blocks.pending'
    [System.IO.File]::WriteAllLines($temp, [string[]]$expected)
    [System.IO.File]::Move($temp, $statePath)
} else {
    $saved = @(Get-Content -LiteralPath $statePath)
    if (Compare-Object -ReferenceObject $expected -DifferenceObject $saved -SyncWindow 0) { throw 'The saved activation list has changed.' }
    $prior = Get-Content -Raw -LiteralPath (Join-Path $root 'verified-blocks-save.json') | ConvertFrom-Json
    if ($prior.pid -eq $game.Id) { throw 'This check requires an independent game restart.' }
}
$snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root "verified-blocks-$Phase.json") -Encoding utf8
$snapshot | ConvertTo-Json -Depth 5
