# Optional real-terrain source

状態: **verified (source migration and local bridge build)**。この資料と
`optional/real-terrain/` は、`H:\CS1-PLATEAU\nagoya-station\real-terrain-20261120`
から実装のソースを保全するための任意コンポーネントです。メインの
`src/` にある Nagoya Android MOD DLL とは別系統で、Bridge DLL はここから
明示的にビルドします。

## 取り込んだもの

コピー対象はソースと実行スクリプトだけです。

- `bridge/src/`: C# 37 本（地形・駅・道路・建物・診断・API Bridge）
- `bridge/build-stage.ps1`: 外部ツリーの再現用ステージスクリプト
- `build.ps1`: `ICities.dll`、`Assembly-CSharp.dll`、
  `Assembly-CSharp-firstpass.dll`、`ColossalManaged.dll`、`UnityEngine.dll`、
  Harmony を明示パスで参照する、任意 Bridge 専用のパラメータ化ビルド
- `launch-nagoya.ps1`: 明示した `GamePath`、`DataRoot`、`BridgePath` を使い、
  起動前に Cities: Skylines の実行中プロセスと DLL ハッシュを検査するランチャー
- `scripts/`: Python 15 本、PowerShell の capture/verify 6 本

取り込んだソースとスクリプトのパス・SHA-256は、[source manifest](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/optional/real-terrain/SOURCE-MANIFEST.json) に保存しています。Gitのテキストと照合できるよう、改行をLFにそろえて計算します。`nagoya_map.py` の外部データルート指定と、ビルド・起動スクリプトは移植先に合わせて調整しました。

## Provenance とライセンス

原本ディレクトリ自体には `.git` がありませんでした。ローカルで確認できた
対応する作業ツリーは `D:\Prj\skylines-agent-bridge` で、origin は
`https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill.git` です。
同ツリーの `LICENSE` と本リポジトリのルート `LICENSE` は、Copyright 2026
Sunwood-ai-labs の MIT License です。外部原本はこのローカルミラーと
バイト単位では一致しないため、origin は所有者・ライセンスの provenance として
記録し、特定の原本 commit だとは断定しません。

## ビルド結果

2026-11-21、ローカル実インストールの次の参照で `optional/real-terrain/build.ps1` を実行しました。
この操作はソースから DLL を生成しただけで、ゲームへのインストール、起動、保存
データの変更は行っていません。

```text
ManagedDirectory = G:\SteamLibrary\steamapps\common\Cities_Skylines\Cities_Data\Managed
HarmonyAssembly  = %LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\CitiesHarmony\CitiesHarmony.Harmony.dll
Output           = D:\Prj\nagoya-android-economy-cs1-mod\.build\real-terrain\SkylinesAgentBridge.dll
sourceCount      = 37
sha256           = D41A0A3CF217FAAFD84809A995C61211EE072F501563F3B3CC1A300A123898CE
mvid             = f8d84b0d-bd6e-444a-a931-9feaf4878d77
```

生成 DLL は `.gitignore` の `*.dll` により追跡対象外です。実行する場合も、
インストール先へ DLL をコピーする処理はこのツリーにありません。ランチャーは
`DataRoot\Addons\Mods\SkylinesAgentBridge\SkylinesAgentBridge.dll` と
`BridgePath` を照合し、不一致なら起動を拒否します。`Cities.exe` が既に動作中の
場合も起動を拒否します。ランチャー自体は本作業中に実行していません。

## 外部データ・ルート

Python の既定動作は従来どおり、スクリプトと同じディレクトリを `ROOT` として
扱います。原本のデータ配置を再利用する場合は、各スクリプトを実行する前に
次の環境変数を設定してください。

```powershell
$env:NAGOYA_REAL_TERRAIN_DATA_ROOT = 'H:\CS1-PLATEAU\nagoya-station\real-terrain-20261120'
uv run --python 3.11 --with-requirements requirements-tools.txt .\optional\real-terrain\scripts\nagoya_map.py --help
```

`nagoya_citywide.py` などの関連スクリプトは `nagoya_map.ROOT` を共有するため、
この shim で原本のディレクトリ構成を維持できます。外部データ・ルートを指定
しない場合、ソースのみの取り込み先には入力がないため、適用処理は成功しません。

## 意図的に取り込んでいないもの

次のファイル群は容量・再配布・実データの理由でコピーしていません。

- `*.dll`、`*.bin`、`*.obj`、`*.png`/`*.jpg`、`*.crp`、ログ、`__pycache__`
- 原本にある `*.json`、`*.tsv` の計画・証跡・キャプチャ結果
- `terrain-dem-probe-20261120`、`citywide-zoning-*`、`runtime`、
  `tool-cache` などの外部入力ディレクトリ
- Steam/CS1 のゲーム本体、Harmony、Workshop/ローカルアセット、保存ゲーム

したがって、この移植だけで地形生成・駅前メッシュ生成・市街地の再現パイプ
ラインが完結するとは言いません。各 Python スクリプトの入力（DEM、道路・建物
TSV/JSON、画像/メッシュ、依存 Python パッケージ）と、Bridge が接続する
`http://127.0.0.1:32123` の実行中ゲームは、利用者側で用意する必要があります。
capture/verify スクリプトは source として保存しただけで、今回実行していません。
