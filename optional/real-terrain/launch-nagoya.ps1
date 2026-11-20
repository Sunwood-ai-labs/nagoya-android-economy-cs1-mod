[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$GamePath = 'G:\SteamLibrary\steamapps\common\Cities_Skylines\Cities.exe',
    [ValidateNotNullOrEmpty()][string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'Colossal Order/Cities_Skylines'),
    [ValidateNotNullOrEmpty()][string]$BridgePath = (Join-Path $PSScriptRoot '../../.build/real-terrain/SkylinesAgentBridge.dll'),
    [ValidatePattern('^Nagoya-Real-[A-Za-z0-9-]+$')][string]$Load,
    [switch]$LocalAssetsOnly,
    [switch]$AllModels,
    [switch]$UnlimitedUtilities,
    [switch]$StationPrecinct,
    [switch]$VerifiedRamps
)

$ErrorActionPreference = 'Stop'

if (Get-Process -Name Cities -ErrorAction SilentlyContinue) {
    throw 'Cities: Skylines is already running; refusing to launch.'
}

function Resolve-InputFile([string]$Path, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label was not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

if (!(Test-Path -LiteralPath $DataRoot -PathType Container)) { throw "DataRoot was not found: $DataRoot" }
$game = Resolve-InputFile $GamePath 'GamePath'
$bridge = Resolve-InputFile $BridgePath 'BridgePath'
$data = (Resolve-Path -LiteralPath $DataRoot).Path

# This launcher never installs or copies a DLL. It only verifies the explicit
# candidate against the already-installed bridge under the selected data root.
$installed = Join-Path $data 'Addons/Mods/SkylinesAgentBridge/SkylinesAgentBridge.dll'
$installed = Resolve-InputFile $installed 'Installed bridge'
if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash) {
    throw 'The installed bridge does not match BridgePath; refusing to launch.'
}

if ($VerifiedRamps -and ($Load -ne 'Nagoya-Real-Citywide-Ramps-20261120' -or $LocalAssetsOnly -or $AllModels -or $UnlimitedUtilities)) {
    throw 'The verified startup profile requires the ramps map with its original assets and model set.'
}

$arguments = @('-nagoya-real-terrain')
if ($LocalAssetsOnly) { $arguments += '-noWorkshop' }
if ($AllModels) {
    if (!$Load) { throw 'The full PLATEAU model set must be loaded with a saved Nagoya map.' }
    $arguments += '-nagoya-full-buildings'
}
if ($UnlimitedUtilities) {
    if (!$Load) { throw 'Unlimited utilities require a saved Nagoya map.' }
    $arguments += '-nagoya-unlimited-utilities'
}
if ($StationPrecinct) {
    if (!$AllModels -or $Load -ne 'Nagoya-Real-Citywide-Neighborhoods-20261120') {
        throw 'Station precinct requires the citywide neighborhoods map with all models.'
    }
    $arguments += '-nagoya-station-precinct'
}
if ($Load) {
    $map = Join-Path $data "Maps/$Load.crp"
    if (!(Test-Path -LiteralPath $map -PathType Leaf)) { throw "Local map package was not found at $map" }
    $arguments += "-nagoya-load=$Load"
}

$env:SteamAppId = '255710'
$env:SteamGameId = '255710'
$process = Start-Process -FilePath $game -WorkingDirectory (Split-Path -Parent $game) -ArgumentList $arguments -WindowStyle Hidden -PassThru
[PSCustomObject]@{ Pid = $process.Id; Map = $Load; GamePath = $game; DataRoot = $data; BridgePath = $bridge }
