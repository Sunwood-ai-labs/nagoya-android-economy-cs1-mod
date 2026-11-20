[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ManagedDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$HarmonyAssembly,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../.build/real-terrain'),
    [string]$CompilerPath
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

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v3.5/csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET/Framework/v3.5/csc.exe')
    )
    $CompilerPath = $compilerCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}
$compiler = Resolve-InputFile $CompilerPath 'C# compiler'

$referenceNames = @(
    'ICities.dll',
    'Assembly-CSharp.dll',
    'Assembly-CSharp-firstpass.dll',
    'ColossalManaged.dll',
    'UnityEngine.dll'
)
$references = @()
foreach ($name in $referenceNames) {
    $references += "/reference:$(Resolve-InputFile (Join-Path $managed $name) "Managed reference $name")"
}
$references += "/reference:$harmony"

$sourceDirectory = Join-Path $PSScriptRoot 'bridge/src'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File |
    Sort-Object Name | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw "No C# sources were found under $sourceDirectory" }

$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
$dll = Join-Path $output.FullName 'SkylinesAgentBridge.dll'
$compilerArgs = @('/nologo', '/target:library', '/optimize+', '/debug-', "/out:$dll") + $references + $sources
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "The optional real-terrain bridge build failed (compiler exit code $LASTEXITCODE)." }
if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw "The compiler reported success but produced no output: $dll" }

$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $dll).Path)
[PSCustomObject]@{
    status = 'built'
    output = (Resolve-Path -LiteralPath $dll).Path
    sha256 = $hash
    mvid = $assembly.ManifestModule.ModuleVersionId.ToString()
    sourceCount = $sources.Count
    managedDirectory = $managed
    harmonyAssembly = $harmony
} | ConvertTo-Json -Compress
