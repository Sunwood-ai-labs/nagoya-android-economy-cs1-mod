[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManagedDirectory,
    [Parameter(Mandatory)][string]$HarmonyAssembly,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.build')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v3.5/csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework 3.5 x64 C# compiler is required.'
}
$references = @(
    foreach ($name in @('ICities.dll', 'Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
        "/reference:$((Resolve-Path -LiteralPath (Join-Path $ManagedDirectory $name)).Path)"
    }
    "/reference:$((Resolve-Path -LiteralPath $HarmonyAssembly).Path)"
)
$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
$dll = Join-Path $output.FullName 'NagoyaAndroidDemo.dll'
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName
& $compiler /nologo /target:library /optimize+ "/out:$dll" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'The Nagoya MOD build failed.' }
Get-FileHash -LiteralPath $dll -Algorithm SHA256
