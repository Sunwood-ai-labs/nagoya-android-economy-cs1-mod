# 名古屋駅前の区画別整備記録

駅前を8区画に分け、国土地理院の2020年8月撮影写真をゲームの座標へ変換して区画ごとに切り出した。名古屋市の工事資料も参照した。西口は写真の撮影後に改修が進んでいるため、航空写真は位置関係を読むために使った。 [地理院地図の航空写真](https://maps.gsi.go.jp/#18/35.17088/136.88156/&base=seamlessphoto&ls=seamlessphoto&disp=1) · [名古屋市の工事資料](https://www.city.nagoya.jp/_res/projects/default_project/_page_/001/009/861/jokyo2604.pdf) · [© OpenStreetMap contributors](https://www.openstreetmap.org/copyright)

比較画像は左から航空写真、整備前のゲーム、整備後のゲーム。ゲームの前後で中心座標・方位・角度・ズームをそろえた。航空写真は記録した区画境界で切り出し、ゲーム画像にはカメラの画角に入る周辺も写る。

| 区画 | 整備の対象 | 証拠と作業記録 |
|---|---|---|
| 01 桜通口ロータリー・タワーズ前 | 横断帯と広場の舗装帯 | [3枚の比較](images/a998feab1a754df9f98939fe.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/01/README.md) |
| 02 大名古屋ビルヂング・桜通東側 | 桜通と周辺の横断帯 | [3枚の比較](images/1c280b3b8cae339ce872a03a.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/02/README.md) |
| 03 太閤通口バスのりば | 待合上屋と舗装の縁 | [3枚の比較](images/6469ce0b136b29e32f55a1ba.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/03/README.md) |
| 04 椿町・駅西銀座 | 平面駐車場の舗装と枠線 | [3枚の比較](images/7a6595f60c3836443ff7cf51.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/04/README.md) |
| 05 JPタワー・牛島町 | JPタワーの屋上面と着陸帯の模様 | [3枚の比較](images/dd5a74b257a7822c1347f524.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/05/README.md) |
| 06 駅北の線路脇 | 並行線路の間に砂利面と点検帯 | [3枚の比較](images/40f455d41b83c4c7d8f17fc1.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/06/README.md) |
| 07 名鉄百貨店本館・名駅通南側 | 屋上の設備帯と横断帯 | [3枚の比較](images/ea2963a3cd748cf5dfd2ead3.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/07/README.md) |
| 08 広小路通・線路の南側 | 線路脇の敷地と保守通路 | [3枚の比較](images/20b0c726e4d958efaf7e3569.jpg) · [詳細](evidence/native-real-terrain-20261120/block-by-block/08/README.md) |

区画ごとに整備前を撮影し、ゲームでその区画のメッシュを読み込み、同じ画角で再撮影して次へ進んだ。各詳細ページに航空写真、前後の原寸PNG、撮影時刻、区画座標、追加内容を保存した。椿町には工事用地などの開いた敷地が残り、南側では線路敷の色の境目が見える。横断帯・駐車場・線路の追加部分は景観用。

名古屋マップを開き直し、8区画すべてが自動で復元されたことをゲームから読み戻した。JPタワーの屋上は別の画角でも撮り直し、着陸帯全体が見えることを確認した。 [再起動後の読み戻し](evidence/native-real-terrain-20261120/block-by-block/verified-blocks-readback.json) · [再起動後のJPタワー](images/7deb00dc8b19c90b73261e48.png)
