# 名古屋実地形版の道路・建物検証

名古屋駅前の最新状態と空中写真との比較を、[駅前の航空写真比較](nagoya-station-aerial-comparison.md)にまとめた。

更地から国土地理院の標高を復元し、OSM道路とPLATEAU建物を配置した。元の地図は保存したまま、新しい地図を別名で保存している。作業の詳細と再現スクリプトは `H:/CS1-PLATEAU/nagoya-station/real-terrain-20261120` の `ROAD-AND-BUILDING-VERIFICATION.md` に記録した。

## 実機で再読込まで確認した範囲

- 道路が34グループに分かれていた。標高の判定を補間に替え、短いランプ1箇所を前後の勾配を見ながら補正した。短い一般道をつなぎ、1,869のランプ入力区間は一方通行の専用形状にした。
- `Nagoya-Real-Citywide-Ramps-20261120.crp` の再読込で、市内の道路8,463区間が一つのネットワークに入り、16区に用途付き建物2,225棟とベンチ48基が残ることを確認した。街路樹96本は配置時に確認した。市外との道路接続は未整備。[再読込結果](evidence/native-real-terrain-20261120/Nagoya-Real-Citywide-Ramps-20261120-reload-verified.json)
- PLATEAUの36,968棟分の形状を、地形と同じ座標で読み込んだ。建物の外観はランチャーで別途読み込み、シミュレーション用の用途付き建物はセーブに保存される。[名古屋駅の近景](images/96eb9a4397562ae0a9a27a75.png)
- 同じカメラでランプを比較し、途中に現れていたトンネル入口の形状が消え、カーブが連続したことを確認した。近くに短い行き止まりが残る。[ランプ修正前](images/e949bc9f8ecf30e35955b76f.png) [ランプ修正後](images/490141b72fde292674399732.png)

## 整備中

[街全体の画面](images/fa54b5be87e9ac16a049727b.png) では生活道路が足りず、建物のない区画も広い。16区の生活道路を追加した計画は12,544区間、用途付き建物の配置候補は4,290地点。全域モデルの変換済み一覧にはPLATEAUの738,033棟分を収録した。今後の確認には実機での読込み、保存と再読込、同じ画角での撮影を使う。

追加版の実機検証は、タイトル画面のParadox自動ログイン確認で中断している。確認済みのランプ修正版と、準備済みの生活道路・全域モデルの数値を区別して記録した。

その後、元のOSMにある生活道路を追加し、同じ道路データで照合できた交差点につなぐ計画を作った。道路は12,544区間から19,380区間、用途付き建物の候補は4,290地点から5,103地点。PLATEAU形状の重心から歩道付き道路までの距離は、計画上の中央値で46.98mから38.55mになった。追加版の配置、交差点の見た目、車線と建物のアクセスは実機でまだ確認していない。[16区別の計画比較](evidence/native-real-terrain-20261120/neighborhood-street-preflight.json)

道路の中心線を直線近似した計画上の検査では、どちらの版でも要確認の交差箇所は同じ27箇所だった。推定高低差の小さい6箇所を同じ画角から撮影する手順を追加した。20時53分の画面でもParadoxの自動ログイン確認が開いている。[要確認箇所](evidence/native-real-terrain-20261120/neighborhood-crossing-qa.json)

21時05分に、ランプ修正版を以前と同じBridgeと起動条件で開き直した。道路8,463区間とPLATEAU形状36,968棟分の読み込みを確認し、名駅を実機で撮り直した。実地形への全域モデルと追加道路の取り込みは未確認。[復帰後の画面](images/86f8006429583bd431659aa0.png)

## 実地形の拡張版を再読込

`Nagoya-Real-Citywide-Neighborhoods-20261120.crp` を別名で保存し、ゲームで開き直した。道路は19,380区間で市内の道路網は一つ。用途付き建物は5,018地点で、1地点は同じ用途と座標の建物に入れ替わった。外観モデルは `-AllModels` でPLATEAU形状738,033棟分を読み込んだ。高架が重なって見える箇所と、市外接続・電力・水道は残っている。[再読込の結果](evidence/native-real-terrain-20261120/neighborhoods-reload-verified.json) [広域の実機スクショ](images/805af2e372c0a8ce8bf83b21.png) [名駅周辺](images/a0cf3388b4d1027f61d0a41d.png)

## 電力・水・下水の開発用モード

名古屋の拡張版を `launch-nagoya.ps1 -Load Nagoya-Real-Citywide-Neighborhoods-20261120 -AllModels -UnlimitedUtilities` で開くと、MODが電力・水の要求に応え、下水を受け入れる。実機で両方の不足警告が5,019件から0件になり、モードのオフと再有効化も確認した。再現手順は実地形プロジェクトの記録に詳述した。[確認結果](evidence/native-real-terrain-20261120/unlimited-utilities-verified.json) [有効化後の実機画面](images/4505e55b7e1aab55dc5e5570.png)

## 名古屋駅の線路と駅前広場

名古屋駅の航空写真と構内図を確認し、OpenStreetMapの線路106区間とホーム12面の位置を地形に合わせた。太閤通口のバス停上屋、桜通口のタワーズ前の舗装、線路・ホームを実機に追加した。線路と通路は景観用で、桜通口の広場から街区へ向かう部分には芝地が残る。 [Google マップの航空写真](https://www.google.com/maps/@35.17088,136.88156,18z/data=!3m1!1e3) [JR東海の構内図](https://railway.jr-central.co.jp/shinkansenbusiness/_pdf/shinkansenbusiness_nagoya.pdf) [OpenStreetMap](https://www.openstreetmap.org/#map=18/35.17088/136.88156)

名古屋の拡張版を従来の `-AllModels -UnlimitedUtilities` に `-StationPrecinct` を加えて起動した。道路19,380区間と駅の6メッシュを含む描画オブジェクト1,905個をゲームから読み戻した。 [読み戻し](evidence/native-real-terrain-20261120/station-precinct-runtime-verified.json)

- 真上 [整備前](images/85ed37948beae01cbaa889ae.png) [整備後](images/c3d667365906f69af939b601.png)
- 斜め [整備前](images/a766007565f4bd2572357f7d.png) [整備後](images/6794cac075b9972af92a2f11.png)
- 駅前 [太閤通口](images/3fb1d18a1f5a76615b3a10c2.png) [桜通口](images/b504e29e8f772edbb3394a09.png)
