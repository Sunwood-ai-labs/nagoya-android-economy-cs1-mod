# Nagoya Android Economy for Cities: Skylines

同じ名古屋の街でアンドロイドの導入有無・台数・充電口数を変え、CS1の住民や店舗にどう影響するかを比較するMODです。PLATEAUの建物配置を実験の空間に使います。

2026年11月18日の実ゲームでは、名古屋市内の56地域に168体を配置し、166棟を対象にMOD内の配送・充電を確認しました。CS1標準の店が出す商品注文と在庫はゲーム内で読み取れます。アンドロイドの到着でCS1標準の在庫が変わる処理と、全市での経済効果の比較は未実装です。

- [CS1の都市経済との接続と検証条件](docs/native-economy.md)
- [CS1標準の住宅・就業・通勤を使った生活試験](docs/android-life.md)
- [PLATEAU建物をCS1の住宅として割り当てた検証](docs/plateau-zoning.md)
- [168体の実ゲーム観測](docs/evidence/live-168-20261118.json)
- [CS1標準の商品需要と在庫の観測](docs/evidence/native-goods-baseline-20261118.json)

## Windowsでビルド

初代Cities: Skylinesの `Cities_Data/Managed`、ゲームと互換性のあるCitiesHarmonyのランタイムDLL、.NET Framework 3.5のC#コンパイラを使います。

```powershell
$game = 'G:\SteamLibrary\steamapps\common\Cities_Skylines'
$data = 'H:\CS1-PLATEAU\nagoya-station'
./build.ps1 -ManagedDirectory "$game\Cities_Data\Managed" `
    -HarmonyAssembly "$data\runtime\lib\CitiesHarmonyOfficial\CitiesHarmony\CitiesHarmony.Harmony.dll"
```

出力は `.build/NagoyaAndroidDemo.dll` です。ゲームを終了した状態で、既存DLLのバックアップを取ってから `%LOCALAPPDATA%/Colossal Order/Cities_Skylines/Addons/Mods/NagoyaAndroidDemo/` に置きます。ゲーム内でCitiesHarmonyとこのMODを有効にし、名古屋のセーブを読み込みます。

現在のシーンは `H:/CS1-PLATEAU/nagoya-station` にある `citywide-live-v1/network.json`、道路・歩道データ、建物データ、アンドロイドと充電器のアセットを参照します。稼働中の名古屋セットでは `citywide-live-v1/enabled.txt` を使ってシーンを有効にしています。これらのランタイムデータとゲーム内セーブは別途必要です。

## 配送の回帰試験

```powershell
$service = 'H:\CS1-PLATEAU\nagoya-station\citywide-live-v1\building-service-v1'
./test.ps1 -GraphPath "$service\routing.ncg" -RegionsPath "$service\regions.json"
```

この試験は、同じ実地経路で1〜3体／拠点の配送・回収・充電、荷物保存、配送先と充電器の重複割り当てを確認します。

実ゲームの観測は `py -3 tools/verify_live.py --root $data --report fleet-observation.json` で保存できます。CS1標準バスも照合する場合は、CS1操作ブリッジを起動して `--bridge-url http://127.0.0.1:32123` を追加します。

## ソースの出典

[cities-skylines1-agent-skill](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill) のローカルコミット `78b94841d589b6730f57ddf01dc24fdf18cf858f` から、稼働中のMODを構成する25個のC#ソースを移しました。元のMITライセンスを引き継いでいます。
