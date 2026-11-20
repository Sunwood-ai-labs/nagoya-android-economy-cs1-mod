[CmdletBinding()]
param(
    # Leave both paths empty for the portable, source-checkout fixture.  Supplying
    # both paths keeps the original real-data regression mode.
    [string]$GraphPath = '',
    [string]$RegionsPath = '',
    [int]$ExpectedRegions = -1,
    [int]$ExpectedInitialParcels = -1,
    [string]$FleetSizes = '1,2,3',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.build')
)

$ErrorActionPreference = 'Stop'

function Resolve-InputFile([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

$portable = [string]::IsNullOrWhiteSpace($GraphPath) -and [string]::IsNullOrWhiteSpace($RegionsPath)
if (!$portable -and ([string]::IsNullOrWhiteSpace($GraphPath) -or [string]::IsNullOrWhiteSpace($RegionsPath))) {
    throw 'GraphPath and RegionsPath must be supplied together, or both omitted for the portable fixture.'
}

$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
if ($portable) {
    $fixture = Join-Path $PSScriptRoot 'tests/fixtures'
    $GraphPath = Join-Path $output.FullName 'portable-routing.ncg'
    $RegionsPath = Join-Path $fixture 'regions.json'
    $encodedPath = Join-Path $fixture 'routing.ncg.b64'
    if (!(Test-Path -LiteralPath $encodedPath -PathType Leaf)) {
        throw "Portable routing fixture is missing: $encodedPath"
    }
    try {
        $encoded = ([IO.File]::ReadAllText($encodedPath)).Trim()
        [IO.File]::WriteAllBytes($GraphPath, [Convert]::FromBase64String($encoded))
    }
    catch {
        throw "Portable routing fixture could not be materialized: $($_.Exception.Message)"
    }
    if ($ExpectedRegions -lt 0) { $ExpectedRegions = 2 }
    if ($ExpectedInitialParcels -lt 0) { $ExpectedInitialParcels = 6 }
}

$graph = Resolve-InputFile $GraphPath 'GraphPath'
$regions = Resolve-InputFile $RegionsPath 'RegionsPath'
if ($FleetSizes -notmatch '^\d+(,\d+)*$') {
    throw "FleetSizes must be a comma-separated list of positive integers: $FleetSizes"
}

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v3.5/csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework/v3.5/csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (!$compiler) {
    throw 'A .NET Framework C# compiler (v3.5 or v4.0) is required for the offline regression.'
}

$exe = Join-Path $output.FullName 'FleetCheck.exe'
$sources = @('src/CitywideRoutingCore.cs', 'src/JsonData.cs', 'src/RegionPlan.cs',
    'src/CitywideSimulation.cs', 'tests/FleetCheck.cs') | ForEach-Object {
    Resolve-InputFile (Join-Path $PSScriptRoot $_) "Source file $_"
}
& $compiler /nologo /target:exe /optimize+ "/out:$exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'The fleet check could not be compiled.' }

$checkArgs = @($graph, $regions, "--fleet-sizes=$FleetSizes")
if ($ExpectedRegions -ge 0) { $checkArgs += "--expected-regions=$ExpectedRegions" }
if ($ExpectedInitialParcels -ge 0) { $checkArgs += "--expected-initial-parcels=$ExpectedInitialParcels" }
& $exe @checkArgs
if ($LASTEXITCODE -ne 0) { throw 'The fleet check failed.' }
