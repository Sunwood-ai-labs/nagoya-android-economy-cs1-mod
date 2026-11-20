[CmdletBinding()]
param(
    [ValidateSet('Nagoya-Real-Citywide-Refined-20261120','Nagoya-Real-Citywide-Ramps-20261120','Nagoya-Real-Citywide-Streets-20261120','Nagoya-Real-Citywide-Neighborhoods-20261120')]
    [string]$Map = 'Nagoya-Real-Citywide-Ramps-20261120',
    [switch]$AllModels,
    [switch]$WardViews,
    [switch]$InspectCrossings
)

$ErrorActionPreference = 'Stop'
$url = 'http://127.0.0.1:32123'
$build = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "$Map-build.json") | ConvertFrom-Json
$terrain = Invoke-RestMethod "$url/state/nagoya-terrain" -Method Post -ContentType 'application/json' -Body '{"x":-1438,"z":948}' -TimeoutSec 5
$state = Invoke-RestMethod "$url/state/summary" -TimeoutSec 5
if ($terrain.sourceSha256 -ne $build.sourceSha256 -or $state.network.segments -ne $build.roads) {
    throw 'The requested Nagoya road network and terrain are not open.'
}
$scene = Invoke-RestMethod "$url/commands/nagoya-place-visuals" -Method Post -ContentType application/json -Body '{}' -TimeoutSec 14
if (!$scene.ok -or $scene.plateauSourceBuildings -le 0) {
    throw 'The PLATEAU models are missing.'
}
if ($AllModels -and $scene.plateauSourceBuildings -ne 738033) {
    throw 'The full citywide PLATEAU set is not visible.'
}

$prefix = switch -Wildcard ($Map) {
    '*-Ramps-*' {'nagoya-ramps'}
    '*-Streets-*' {'nagoya-streets'}
    '*-Neighborhoods-*' {'nagoya-neighborhoods'}
    default {'nagoya-refined'}
}
if ($AllModels) { $prefix += '-all-models' }
$views = @(
    @{name="$prefix-ramp.png"; x=2817; z=3796; zoom=240; yaw=38; pitch=62},
    @{name="$prefix-urban.png"; x=-470; z=100; zoom=5300; yaw=16; pitch=61},
    @{name="$prefix-east.png"; x=2440; z=540; zoom=2400; yaw=160; pitch=47},
    @{name="$prefix-port.png"; x=-1457; z=-2196; zoom=1900; yaw=37; pitch=60},
    @{name="$prefix-station-overview.png"; x=-1438; z=948; zoom=890; yaw=20; pitch=58},
    @{name="$prefix-station-close.png"; x=-1388; z=961; zoom=318; yaw=112; pitch=48}
)
if ($WardViews) {
    $wards = @{
        '中村区'='nakamura'; '天白区'='tempaku'; '緑区'='midori'; '熱田区'='atsuta'
        '瑞穂区'='mizuho'; '名東区'='meito'; '港区'='minato'; '中川区'='nakagawa'
        '守山区'='moriyama'; '西区'='nishi'; '南区'='minami'; '千種区'='chikusa'
        '昭和区'='showa'; '東区'='higashi'; '中区'='naka'; '北区'='kita'
    }
    foreach ($group in ($build.lots | Group-Object ward)) {
        if (!$wards.ContainsKey($group.Name)) { continue }
        $centerX = ($group.Group | Measure-Object -Property x -Average).Average
        $centerZ = ($group.Group | Measure-Object -Property z -Average).Average
        $lot = $group.Group | Sort-Object { [Math]::Pow($_.x - $centerX, 2) + [Math]::Pow($_.z - $centerZ, 2) } | Select-Object -First 1
        $views += @{name="$prefix-ward-$($wards[$group.Name]).png"; x=$lot.x; z=$lot.z; zoom=700; yaw=20; pitch=57}
    }
}
if ($InspectCrossings) {
    $crossings = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'roads/neighborhood-crossing-qa.json') | ConvertFrom-Json
    $number = 0
    foreach ($point in ($crossings.neighborhoodPlan.candidates | Select-Object -First 6)) {
        $number++
        $views += @{name="$prefix-crossing-$number.png"; x=$point.x; z=$point.z; zoom=160; yaw=35; pitch=48}
    }
}
foreach ($view in $views) {
    $before = [DateTime]::UtcNow
    $body = $view | ConvertTo-Json -Compress
    $result = Invoke-RestMethod "$url/commands/nagoya-capture" -Method Post -ContentType application/json -Body $body -TimeoutSec 6
    Start-Sleep -Milliseconds 1700
    $file = Get-Item -LiteralPath $result.path -ErrorAction SilentlyContinue
    if (!$file -or $file.LastWriteTimeUtc -lt $before -or $file.Length -lt 10000) {
        Start-Sleep -Milliseconds 1600
        $file = Get-Item -LiteralPath $result.path -ErrorAction SilentlyContinue
    }
    if (!$result.ok -or !$file -or $file.LastWriteTimeUtc -lt $before -or $file.Length -lt 10000) {
        throw "A new native screenshot was not saved for $($view.name)"
    }
    $file | Select-Object Name, Length
}
