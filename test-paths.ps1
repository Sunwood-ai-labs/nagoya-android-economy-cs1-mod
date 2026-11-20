[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '.build'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v3.5/csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
}
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'A .NET Framework C# compiler is required for the standalone path checks.'
}
$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
$exe = Join-Path $output.FullName 'RuntimePathsCheck.exe'
& $compiler /nologo /target:exe "/out:$exe" (Join-Path $PSScriptRoot 'src/RuntimePaths.cs') (Join-Path $PSScriptRoot 'tests/RuntimePathsCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime path checks could not be compiled.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Runtime path checks failed.' }
