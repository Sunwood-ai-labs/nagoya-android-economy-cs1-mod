# 航空写真と照合した名古屋駅前の整備

個別の区画で行った後続の整備は [区画別の比較・施工記録](nagoya-station-block-by-block.md) に保存した。

名古屋駅を中心に東西約1 km、南北約860 mを確認した。国土地理院の2020年8月撮影の空中写真を、現行マップと同じ平面直角座標（EPSG:6675）に合わせた。前回確認したGoogle マップの航空写真と、名古屋市の2026年4月版の工事資料も参照した。2020年の写真から舗装と建物の位置を読み取り、再整備計画は当時の写真と区別して扱った。 [地理院地図の写真](https://maps.gsi.go.jp/#18/35.17088/136.88156/&base=seamlessphoto&ls=seamlessphoto&disp=1) · [撮影期間](https://maps.gsi.go.jp/#18/35.17088/136.88156/&base=seamlessphoto&ls=seamlessphoto%7Cseamlessphoto_spec&disp=11) · [Google マップの航空写真](https://www.google.com/maps/@35.17088,136.88156,18z/data=!3m1!1e3) · [名古屋市の工事資料](https://www.city.nagoya.jp/_res/projects/default_project/_page_/001/009/861/jokyo2604.pdf)

[航空写真と実機の全体比較](images/416b68f8d50d971bc928321c.jpg) · [整備前の実機画面](images/df5109f272f38dd373e0c939.png) · [整備後の実機画面](images/ea5e61a6a18d56f6e9afa229.png) · [整備後の斜め画角](images/ee713d36c24d5c3fbcb6a4ce.png)

| 範囲 | 航空写真と前のゲーム画面の差 | ゲームで追加・確認した内容 | 画面中央の芝色の割合 |
|---|---|---|---|
| 東・桜通口 | タワーズ前に舗装広場、名駅通と桜通の車道があり、向かいまで建物が続く。ゲームでは細い歩道の間が広く芝地だった | 名駅通・桜通の舗装と交差部、駅前広場、大名古屋ビルヂングを補った。[比較写真](images/03be5ae5fc0b4aa35144a595.jpg) | 43.02% → 10.04% |
| 西・太閤通口と椿町 | 広場の外には細い通りと連続した建物がある。ゲームでは建物間が芝地のままだった | バス乗り場側の舗装、椿町側の生活道路の路面、小規模な建物を補った。航空写真にある学校のグラウンドは確保した。[比較写真](images/5be6b451b26b50f2f327c89d.jpg) | 29.08% → 5.29% |
| 北・牛島町方面 | 線路と作業用地、JPタワーなど駅北側の建物と街路が続く。ゲームでは線路が手前で途切れていた | 線路の延長部と敷地、JPタワー、街路を追加した。線路の見た目には調査範囲の境界がある。[比較写真](images/ab720318033753bf25073c3b.jpg) | 48.34% → 11.25% |
| 南・名鉄百貨店から広小路通 | ホーム南の線路と、線路東側の商業施設・車道に幅がある。ゲームでは線路の先や建物沿いが芝地だった | 名鉄百貨店本館の外形、建物沿いの舗装と線路の延長部を補った。本館は写真の形と地図の階数から近似した。[比較写真](images/82d0b653f98a6c4de0cd93e4.jpg) | 30.12% → 9.90% |

芝色の割合は、ゲームでカメラ設定をそろえた前後画像の同じ中央範囲に対し、色の条件を固定して計算した。植栽や屋上の一部も含む画面上の値で、航空写真の緑地面積とは別の指標。全体の俯瞰では31.02%から7.85%になった。 [撮影範囲と計算結果](evidence/native-real-terrain-20261120/station-density/capture-comparison.json)

OpenStreetMapの建物外形を現行のPLATEAUメッシュと照合し、重なりを避けて76棟分の外観を補った。車道・歩道・広場の追加面積は合わせて約36,125ゲームm²。線路脇の敷地は約3,119ゲームm²増えた。大名古屋ビルヂングとJPタワーの高さは事業者資料を使い、低層部と高層部の外形は地図・写真を見て近似した。 [生成時の記録](evidence/native-real-terrain-20261120/station-density/density-build.json) · [© OpenStreetMap contributors](https://www.openstreetmap.org/copyright) · [三菱地所の建物概要](https://office.mec.co.jp/search/detail/011752/) · [日本郵便の建物概要](https://www.post.japanpost.jp/notification/pressrelease/2015/00_honsha/1110_01_02.pdf)

ゲーム起動後に駅前の追加6メッシュと道路19,380区間を読み戻し、全体と東西南北の5画角を確認した。今回補った車道・歩道・線路は景観用で、そこを使う車・人・列車の経路は未実装。比較範囲の外側や桜通口から先の街区に芝地が残り、線路の敷地は整備範囲の端で色が切り替わる。 [実機の読み戻し](evidence/native-real-terrain-20261120/station-density/density-runtime-verified.json)

再現方法は `H:/CS1-PLATEAU/nagoya-station/real-terrain-20261120/ROAD-AND-BUILDING-VERIFICATION.md` に記録した。
