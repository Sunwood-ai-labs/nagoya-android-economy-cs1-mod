$ErrorActionPreference='Stop'
$taskManaged='G:\SteamLibrary\steamapps\common\Cities_Skylines\Cities_Data\Managed'
$taskHarmony=Join-Path $env:LOCALAPPDATA 'Colossal Order/Cities_Skylines/Addons/Mods/CitiesHarmony/CitiesHarmony.Harmony.dll'
$taskOutput=Join-Path $PSScriptRoot 'SkylinesAgentBridge.dll'
$taskSources=@(Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs'|Select-Object -ExpandProperty FullName)
& "$env:WINDIR\Microsoft.NET\Framework64\v3.5\csc.exe" /nologo /target:library /optimize+ /debug- "/out:$taskOutput" "/reference:$taskManaged\ICities.dll" "/reference:$taskManaged\Assembly-CSharp.dll" "/reference:$taskManaged\Assembly-CSharp-firstpass.dll" "/reference:$taskManaged\ColossalManaged.dll" "/reference:$taskManaged\UnityEngine.dll" "/reference:$taskHarmony" $taskSources
if($LASTEXITCODE -ne 0){throw 'Station bridge stage-v3 compile failed; inspect compiler error before another build'}
$hash=(Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash
$assembly=[Reflection.Assembly]::LoadFile((Resolve-Path $taskOutput).Path)
[PSCustomObject]@{sha256=$hash;mvid=$assembly.ManifestModule.ModuleVersionId.ToString();sourceCount=$taskSources.Count}|ConvertTo-Json -Compress
