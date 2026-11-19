# 名古屋全域のCS1建設

全市で人とアンドロイドが同じ住宅・職場・交通を使うため、建物の用途とCS1で構築する道路・区画を結び付ける。CS1の建物数と地図面積の制限に合わせ、建物ごとのIDを残しながら同じ用途の街区を集約する。既存の駅周辺のセーブから独立した、左側通行の `NagoyaCitywide-Construction` を使う。

## 配置データと実機の検証

道路の元データから13,648ノードと13,677区間を構築用に書き出した。データ上では全ノードがつながり、立体交差375箇所で中心線間に5m以上の高さ差を取り、勾配を16%以内に収めた。橋の幅やゲーム内の接続、車両挙動は実機で確認する。

用途別の区画候補は2,228箇所。738,033件のPLATEAUモデルを用途別に集約した741,740件の関連のうち、405,006件が候補に到達する。残り336,734件の用途には追加の道路や区画が必要。住宅・職場系以外の学校や設備は、その用途に合う施設の接続を別途確認する。

`NativeCityRoadBuilder` と `NativeCityZoneBuilder` はCS1の標準道路と区画をシミュレーションスレッドで作成する。実行前に都市・タイル・道路本数・入力ハッシュを照合する。`NativeCityLedger` がセーブに建物と道路の対応を格納し、`NativeCityRoster` が再読込後に有効な区画、入居者、職場登録、実際の通勤経路を区ごとに読む。ロード後の同一性確認に失敗した古いIDは、稼働集計に含めない。

## ローカルで再生成

下記は、使用するデータを用意するCLI。データは `H:/CS1-PLATEAU/nagoya-station` に置く。道路の高さ調整にはSciPyを使用する。

1. `tools/compile_native_city_roads.py` で原道路の区間と交差点をCS1形式に分割・整形する。
2. `tools/fit_native_road_clearance.py` で勾配と交差部の高さを調整する。調整済みの `native-roads-clearance-adjusted.tsv` を使う。
3. `tools/compile_native_city_sites.py` に調整済みの道路とPLATEAU用途のCSVを渡す。出力 `native-zoning-requests.tsv` は、CS1に構築済みの道路IDと照合してから適用する。
4. `tools/audit_native_zoning_gaps.py` で道路の追加が必要な街区を出力する。

ゲーム側は `citywide-zoning-v3/enabled.txt` を置き、名前を指定した新規都市だけで平坦化を行う。道路を適用する前に新規都市を別名保存し、`stage_native_city_roads.ps1` を実行モードなしで事前検査する。同じ入力で `-Execute` を指定して適用し、道路を保存したあと `stage_native_city_zones.ps1` に実際の道路の実行IDとセーブ名を渡す。観測には `road-runtime.json`、`zone-runtime.json`、`native-city-roster.json` を使う。

一般商品を扱う店舗を補う場合は、未建設か消失した一般商業区画だけを含む計画を作り、既存道路のセーブで `stage_native_city_zones.ps1 -SavedRoadNetwork -RequireGoodsShops` を使う。商業区画には、CS1が `Goods` を仕入れ品とするレベル1の建物を選ぶ。1×1の給油所への置き換わりを避けるため、この用途では2セル以上の店舗を使う。建物と用途指定が有効なPLATEAUグループは既設として扱い、配置先は道路に接する空きセルで照合する。

千種区と昭和区で各6棟、中区で5棟、中村区で4棟を配置した。建設直前と直後を同じゲーム時刻で照合し、既存717棟の商業施設と道路網を確認した。候補28件のうち7件は、元の道路に必要な幅と奥行きの空き区画がなかった。稼働後には、4区すべてで一般商品を受け入れられる店舗を確認した。[配置した建物IDと稼働確認](evidence/native-goods-retail-repair-20261119.json)
