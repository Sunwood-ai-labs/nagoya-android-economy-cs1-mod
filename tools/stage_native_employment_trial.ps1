[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^NagoyaCitywide-EmploymentTrial-[\w.-]+$')][string]$CheckpointName,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CheckpointSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{1,60}$')][string]$CaseId,
    [Parameter(Mandatory)][datetime]$GameTime,
    [Parameter(Mandatory)][uint32]$BuildIndex,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$NetworkSegments,
    [Parameter(Mandatory)][ValidateRange(0, 4096)][int]$AdditionalAndroids,
    [ValidateRange(1, 720)][int]$DurationHours = 720,
    [ValidateRange(100000, 5000000)][int]$OpeningCash = 2000000,
    [switch]$KeepOpeningCash,
    [switch]$ShareAndroidHouseholds,
    [switch]$ShareMixedHouseholds,
    [uint32[]]$CommuteAuditIds,
    [switch]$TestLongWalkCommutes,
    [switch]$AuditWardWorkTrips,
    [string]$PedestrianLinksFile,
    [string]$PedestrianLegsFile,
    [switch]$LoadCheckpointFromMenu,
    [switch]$LinkWithRoadSidewalks,
    [switch]$LinkWithinRoadSegments
)

$ErrorActionPreference = 'Stop'
if ($KeepOpeningCash -and $PSBoundParameters.ContainsKey('OpeningCash')) {
    throw 'Choose either the existing balance or an explicit opening grant.'
}
if (($ShareAndroidHouseholds -and $ShareMixedHouseholds) -or (($ShareAndroidHouseholds -or $ShareMixedHouseholds) -and $AdditionalAndroids -eq 0)) {
    throw 'Select one household sharing mode with additional Android residents.'
}
$data = "$env:LOCALAPPDATA/Colossal Order/Cities_Skylines"
$save = Join-Path $data "Saves/$CheckpointName.crp"
$resume = Get-Content -LiteralPath (Join-Path $data 'continue_game.json') -Raw | ConvertFrom-Json
if ((!$LoadCheckpointFromMenu -and $resume.title -cne $CheckpointName) -or
    (Get-FileHash -LiteralPath $save).Hash -ne $CheckpointSha256) {
    throw 'The game must resume the verified employment trial checkpoint.'
}

$root = 'H:/CS1-PLATEAU/nagoya-station/citywide-zoning-v3'
$request = @{
    city = 'NagoyaCitywide-Construction'
    caseId = $CaseId
    expectedGameTime = $GameTime.ToString('s')
    expectedBuildIndex = $BuildIndex
    expectedNetworkSegments = $NetworkSegments
    durationHours = $DurationHours
    additionalAndroids = $AdditionalAndroids
}
if ($KeepOpeningCash) { $request.keepOpeningCash = $true }
else { $request.openingCash = $OpeningCash }
if ($ShareAndroidHouseholds) { $request.shareAndroidHouseholds = $true }
if ($ShareMixedHouseholds) { $request.shareMixedHouseholds = $true }
if ($TestLongWalkCommutes -and -not $PSBoundParameters.ContainsKey('CommuteAuditIds')) {
    throw 'Specify the citizens participating in the long walk trial.'
}
if ($PSBoundParameters.ContainsKey('CommuteAuditIds')) {
    if ($CommuteAuditIds.Count -lt 1 -or $CommuteAuditIds.Count -gt 48 -or 0 -in $CommuteAuditIds -or
        ($CommuteAuditIds | Sort-Object -Unique).Count -ne $CommuteAuditIds.Count) {
        throw 'Select 1–48 unique native citizen IDs for the route audit.'
    }
    $request.commuteAuditIds = $CommuteAuditIds
}
if ($TestLongWalkCommutes) { $request.testLongWalkCommutes = $true }
if ($AuditWardWorkTrips) { $request.auditWardWorkTrips = $true }
if ($LinkWithRoadSidewalks -and !$PedestrianLinksFile) { throw 'Specify anchors in PedestrianLinksFile.' }
if ($LinkWithRoadSidewalks) { $request.linkWithRoadSidewalks = $true }
if ($LinkWithinRoadSegments -and !$LinkWithRoadSidewalks) { throw 'Segment anchors require LinkWithRoadSidewalks.' }
if ($LinkWithinRoadSegments) { $request.linkWithinRoadSegments = $true }
if ($PedestrianLinksFile) {
    $links = Get-Content -LiteralPath $PedestrianLinksFile -Raw | ConvertFrom-Json -NoEnumerate
    $fields = if ($LinkWithinRoadSegments) { 4 } else { 2 }
    if ($links.Count -lt 1 -or $links.Count -gt 24 -or @($links | Where-Object {
        $_.Count -ne $fields -and !($LinkWithRoadSidewalks -and $_.Count -eq ($fields + 2))
    }).Count) {
        throw 'Provide 1–24 anchor pairs. With LinkWithinRoadSegments, use 0 for a node and 0.2–0.8 for a segment. A road with sidewalks may include an optional bend.'
    }
    $request.pedestrianLinks = $links
}
if ($PedestrianLegsFile) {
    $legs = Get-Content -LiteralPath $PedestrianLegsFile -Raw | ConvertFrom-Json -NoEnumerate
    if ($legs.Count -lt 1 -or $legs.Count -gt 24 -or @($legs | Where-Object { $_.Count -ne 2 }).Count) {
        throw 'Provide 1–24 pairs of existing road segment IDs as a JSON array.'
    }
    $request.pedestrianLegs = $legs
}

$destination = Join-Path $root 'native-employment-trial.json'
$consumed = Join-Path $root "native-employment-$CaseId.request.json"
$result = Join-Path $root "native-employment-$CaseId.json"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $consumed) -or (Test-Path -LiteralPath $result)) {
    throw 'A trial is pending or this case ID has already been used.'
}
$temporary = "$destination.$CaseId.tmp"
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporary, $destination)
[pscustomobject]$request
