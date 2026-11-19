[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Plan,
    [string]$ExpectedCity = 'NagoyaCitywide-Construction',
    [string]$Bridge = 'http://127.0.0.1:32123'
)

$ErrorActionPreference = 'Stop'
$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$network = Get-Content -LiteralPath "$root/utility-runtime.json" -Raw | ConvertFrom-Json
$roster = Get-Content -LiteralPath "$root/native-city-roster.json" -Raw | ConvertFrom-Json
if ($network.loadedCity -cne $ExpectedCity -or $roster.city -cne $ExpectedCity -or
    $network.channel -cne 'utility' -or $network.phase -cne 'built' -or $roster.activeNativeZonedSites -le 0) {
    throw 'Finished utility lines and the live zoned city must be verified before placing service buildings.'
}

$sites = @(Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json)
$allowed = 'Nuclear Power Plant','Large Water Tower 01','Eco Advanced Inland Water Treatment Plant 01'
if (($sites | Group-Object facility | Where-Object Count -gt 1) -or $sites.Count -lt 1 -or $sites.Count -gt 20) {
    throw 'Utility building site identities must be unique and bounded.'
}
$existing = (Invoke-RestMethod "$Bridge/state/facilities?limit=5000" -TimeoutSec 10).facilities
$placed = [Collections.Generic.List[object]]::new()
$call = @{Uri="$Bridge/commands/place-building";Method='Post';ContentType='application/json';TimeoutSec=10}
foreach ($site in $sites) {
    if ($site.buildingPrefab -cnotin $allowed -or [Math]::Abs($site.position.x) -ge 4670 -or
        [Math]::Abs($site.position.z) -ge 4670) {
        throw "Utility building source or position is invalid for $($site.facility)."
    }
    $current = @($existing | Where-Object {
        $_.prefab -ceq $site.buildingPrefab -and
        [Math]::Abs($_.position.x - $site.position.x) -lt .1 -and
        [Math]::Abs($_.position.z - $site.position.z) -lt .1
    })
    if ($current.Count -gt 1) { throw "Utility building placement is ambiguous for $($site.facility)." }
    if ($current.Count -eq 1) {
        $buildingId = $current[0].id
    } else {
        $request = @{buildingPrefab=$site.buildingPrefab;angleDegrees=$site.angleDegrees;position=$site.position;dryRun=$true}
        $dry = Invoke-RestMethod @call -Body ($request | ConvertTo-Json -Depth 4 -Compress)
        if (!$dry.ok -or $dry.buildingPrefab -cne $site.buildingPrefab) {
            throw "Native service building preflight rejected $($site.facility)."
        }
        $request.dryRun = $false
        $actual = Invoke-RestMethod @call -Body ($request | ConvertTo-Json -Depth 4 -Compress)
        if (!$actual.ok -or $actual.buildingPrefab -cne $site.buildingPrefab -or $actual.buildingId -le 0) {
            throw "Native service building placement rejected $($site.facility)."
        }
        $buildingId = $actual.buildingId
    }
    $placed.Add([pscustomobject]@{facility=$site.facility;buildingId=$buildingId;prefab=$site.buildingPrefab;position=$site.position})
}

$observed = (Invoke-RestMethod "$Bridge/state/facilities?limit=5000" -TimeoutSec 10).facilities
foreach ($expected in $placed) {
    $actual = @($observed | Where-Object id -eq $expected.buildingId)
    if ($actual.Count -ne 1 -or $actual[0].prefab -cne $expected.prefab -or
        [Math]::Abs($actual[0].position.x - $expected.position.x) -ge .1 -or
        [Math]::Abs($actual[0].position.z - $expected.position.z) -ge .1) {
        throw "Native service building read-back differs for $($expected.facility)."
    }
}
$report = [pscustomobject]@{city=$ExpectedCity;readBack=$true;facilities=$placed.ToArray()}
$path = "$root/native-utility-facilities-live.json"
$temporary = "$path.tmp"
[IO.File]::WriteAllText($temporary, ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $path) {
    [IO.File]::Replace($temporary, $path, [NullString]::Value)
} else {
    [IO.File]::Move($temporary, $path)
}
$report
