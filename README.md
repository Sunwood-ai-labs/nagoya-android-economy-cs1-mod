# Android Skyline | 名古屋2050

同じ名古屋の街でアンドロイドの導入有無・台数・充電口数を変え、CS1の住民や店舗にどう影響するかを比較するMODです。PLATEAUの建物配置を実験の空間に使います。

名古屋16区を対象にした建設セーブで、人4,391人とアンドロイド505体の入居、全区で両方の職場登録を確認しました。CS1に敷設した道路は1万8,604区間です。熱田区では工場から店舗への配送が1件成立し、CS1標準の在庫が工場で100減り、店舗で100増えました。同じセーブを約4日進める試験で、アンドロイドの増員条件による入居・職場登録数の違いも確認しました。

- [PLATEAU AWARD 応募資料（PDF）](docs/submission/Android-Skyline-Nagoya2050.pdf)
- [CS1の都市経済との接続と検証条件](docs/native-economy.md)
- [CS1標準の住宅・就業・通勤を使った生活試験](docs/android-life.md)
- [PLATEAU建物をCS1の住宅として割り当てた検証](docs/plateau-zoning.md)
- [名古屋全域の建物用途とCS1への接続計画](docs/citywide-zoning.md)
- [全域建設のデータと実機での適用手順](docs/native-city-construction.md)
- [道路・上下水道・電力と入居・就業の実測](docs/evidence/native-network-expansion-20261119.json)
- [工場から店舗への配送と在庫変化](docs/evidence/native-delivery-20261119.json)
- [市内の複数区での配送と経路の検証](docs/evidence/native-delivery-coverage-20261119.json)
- [工場から店舗への配送、自宅への帰宅・充電の実測](docs/evidence/native-delivery-return-20261119.json)
- [CS1標準の発注を使った配送と区別の実績](docs/evidence/native-goods-requests-20261119.json)
- [店舗が扱う商品と、配送できた区の実測](docs/evidence/native-retail-stock-20261119.json)
- [同じセーブでの雇用比較](docs/evidence/native-employment-matched-20261119.json)
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
