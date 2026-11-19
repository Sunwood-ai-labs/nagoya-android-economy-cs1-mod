# Android Skyline | 名古屋2050

同じ名古屋の街でアンドロイドの導入有無・台数・充電口数を変え、CS1の住民や店舗にどう影響するかを比較するMODです。PLATEAUの建物配置を実験の空間に使います。

全16区で、人とアンドロイドの入居とCS1標準の職場登録を確認しました。名古屋の道路データから敷設した道路は20,679区間で、有効な用途付き区画は5,956件です。工場と店舗の実際の在庫で照合したアンドロイド配送は、試験を通算して全16区に到着しました。配送後に同じアンドロイドが自宅へ戻って充電するところまで追跡しました。

同じセーブをゲーム内で96時間進めた試験では、アンドロイドを追加しない場合に人1,856人、133体が追加で入居した場合に人1,839人が職場に登録されていました。両条件で人の住民数にも差が出ました。

- [PLATEAU AWARD 応募資料（PDF）](docs/submission/Android-Skyline-Nagoya2050.pdf)
- [CS1の都市経済との接続と検証条件](docs/native-economy.md)
- [CS1標準の住宅・就業・通勤を使った生活試験](docs/android-life.md)
- [PLATEAU建物をCS1の住宅として割り当てた検証](docs/plateau-zoning.md)
- [名古屋全域の建物用途とCS1への接続計画](docs/citywide-zoning.md)
- [ゲームを進めた後も有効な区画とPLATEAU建物IDの照合](docs/evidence/native-multi-frontage-zoning-20261119.json)
- [全域建設のデータと実機での適用手順](docs/native-city-construction.md)
- [道路・上下水道・電力と入居・就業の実測](docs/evidence/native-network-expansion-20261119.json)
- [工場から店舗への配送と在庫変化](docs/evidence/native-delivery-20261119.json)
- [市内の複数区での配送と経路の検証](docs/evidence/native-delivery-coverage-20261119.json)
- [工場から店舗への配送、自宅への帰宅・充電の実測](docs/evidence/native-delivery-return-20261119.json)
- [CS1標準の発注を使った配送と区別の実績](docs/evidence/native-goods-requests-20261119.json)
- [店舗が扱う商品と、配送できた区の実測](docs/evidence/native-retail-stock-20261119.json)
- [PLATEAUの商業区画に追加した店舗と配送記録](docs/evidence/native-goods-retail-repair-20261119.json)
- [アンドロイドの採用・通勤・配送・帰宅を追った実測](docs/evidence/native-local-factory-hiring-20261119.json)
- [6区での出入口修復とCS1の道路アクセス](docs/evidence/native-frontages-20261119.json)
- [CS1の出入口で経路を選んだ配送と天白区の到着記録](docs/evidence/native-entrance-routing-20261119.json)
- [昭和区で警察と一般商品を扱う商店5棟の稼働を確認](docs/evidence/native-showa-goods-and-police-20261119.json)
- [配送未確認の5区を対象にした通勤と配車の試験](docs/evidence/native-commute-first-deliveries-20261119.json)
- [通勤の再試行と15区での用途付き建物の復旧](docs/evidence/native-travel-and-zoning-recovery-20261119.json)
- [工場に到着した従業員による瑞穂区の配送と帰宅](docs/evidence/native-onsite-dispatch-20261119.json)
- [名東区での一般商店2棟への配送4便](docs/evidence/native-four-ward-short-deliveries-20261119.json)
- [千種区での入居・就業と新設工場からの配送](docs/evidence/native-local-goods-neighborhoods-20261119.json)
- [昭和区・東区の配送と帰宅、全16区の在庫移動の記録](docs/evidence/native-advance-neighborhood-employment-20261119.json)
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
