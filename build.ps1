[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ManagedDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$HarmonyAssembly,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.build')
)

$ErrorActionPreference = 'Stop'

function Resolve-InputFile([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

if (!(Test-Path -LiteralPath $ManagedDirectory -PathType Container)) {
    throw "ManagedDirectory was not found: $ManagedDirectory"
}
$managed = (Resolve-Path -LiteralPath $ManagedDirectory).Path
$harmony = Resolve-InputFile $HarmonyAssembly 'HarmonyAssembly'
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v3.5/csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET/Framework/v3.5/csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (!$compiler) {
    throw 'The .NET Framework 3.5 C# compiler is required for the game-compatible build.'
}

$referenceNames = @('ICities.dll', 'Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')
$references = @()
foreach ($name in $referenceNames) {
    $references += "/reference:$(Resolve-InputFile (Join-Path $managed $name) "Managed reference $name")"
}
$references += "/reference:$harmony"

$sourceDirectory = Join-Path $PSScriptRoot 'src'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File |
    Sort-Object Name | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw "No C# sources were found under $sourceDirectory" }

$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
$dll = Join-Path $output.FullName 'NagoyaAndroidDemo.dll'
$compilerArgs = @('/nologo', '/target:library', '/optimize+', "/out:$dll") + $references + $sources
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "The Nagoya MOD build failed (compiler exit code $LASTEXITCODE)." }
if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw "The compiler reported success but produced no output: $dll" }
Get-FileHash -LiteralPath $dll -Algorithm SHA256
