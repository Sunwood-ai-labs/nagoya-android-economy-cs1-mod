[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GraphPath,
    [Parameter(Mandatory)][string]$RegionsPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.build')
)

$ErrorActionPreference = 'Stop'
$graph = (Resolve-Path -LiteralPath $GraphPath).Path
$regions = (Resolve-Path -LiteralPath $RegionsPath).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v3.5/csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework 3.5 x64 C# compiler is required.'
}
$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
$exe = Join-Path $output.FullName 'FleetCheck.exe'
$sources = @('src/CitywideRoutingCore.cs', 'src/JsonData.cs', 'src/RegionPlan.cs',
    'src/CitywideSimulation.cs', 'tests/FleetCheck.cs') | ForEach-Object {
    Join-Path $PSScriptRoot $_
}
& $compiler /nologo /target:exe /optimize+ "/out:$exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'The fleet check could not be compiled.' }
& $exe $graph $regions
if ($LASTEXITCODE -ne 0) { throw 'The fleet check failed.' }
