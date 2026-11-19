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
    [switch]$ShareMixedHouseholds
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
if ($resume.title -cne $CheckpointName -or (Get-FileHash -LiteralPath $save).Hash -ne $CheckpointSha256) {
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
