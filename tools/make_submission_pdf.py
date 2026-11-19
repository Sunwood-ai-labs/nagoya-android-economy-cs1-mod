"""Prepare the PLATEAU AWARD review slides from actual gameplay and JSON evidence."""

import argparse
from io import BytesIO
import json
import os
from pathlib import Path

from PIL import Image

from reportlab.lib.colors import HexColor
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen.canvas import Canvas

W, H = 960, 540
INK = "#122521"
DARK = "#122925"
MUTED = "#45635B"
PAPER = "#F1F5EF"
WHITE = "#FFFFFF"
MINT = "#CBE8DE"
AMBER = "#FFD16E"


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def render(output, before, after, followup, zoning, frames):
    if output.exists():
        raise FileExistsError(output)
    fonts = Path(os.environ["WINDIR"]) / "Fonts"
    pdfmetrics.registerFont(TTFont("jp", str(fonts / "meiryo.ttc"), subfontIndex=0))
    pdfmetrics.registerFont(TTFont("jp-b", str(fonts / "meiryob.ttc"), subfontIndex=0))
    deck = Canvas(str(output), pagesize=(W, H), pageCompression=1)
    deck.setTitle("PLATEAU Android Economy")
    deck.setSubject("名古屋の都市モデルを用いた人とアンドロイドの生活・雇用実験")

    def text(value, x, top, size=16, color=INK, bold=False):
        deck.setFont("jp-b" if bold else "jp", size)
        deck.setFillColor(HexColor(color))
        deck.drawString(x, H - top - size, str(value))

    def rect(x, top, w, h, color):
        deck.setFillColor(HexColor(color))
        deck.rect(x, H - top - h, w, h, stroke=0, fill=1)

    def image(path, x, top, w, h):
        with Image.open(path) as pixels:
            compressed = BytesIO()
            pixels = pixels.convert("RGB")
            pixels.thumbnail((1560, 880), Image.Resampling.LANCZOS)
            pixels.save(compressed, format="JPEG", quality=88, optimize=True)
        compressed.seek(0)
        source = ImageReader(compressed)
        iw, ih = source.getSize()
        scale = min(w / iw, h / ih)
        ww, hh = iw * scale, ih * scale
        deck.drawImage(source, x + (w - ww) / 2, H - top - (h + hh) / 2, ww, hh)

    def base(page, eyebrow, title):
        rect(0, 0, W, H, PAPER)
        text(eyebrow, 44, 28, 13, MUTED, True)
        text(title, 44, 52, 31, INK, True)
        rect(44, 505, 872, 1, "#C4D3C7")
        text("PLATEAU Android Economy · Cities: Skylines", 44, 514, 10, MUTED)
        text(f"{page} / 7", 872, 514, 10, MUTED)

    def note(lines, x, top, size=15, spacing=30, color=MUTED):
        for index, line in enumerate(lines):
            text(line, x, top + index * spacing, size, color)

    def metric(label, value, x, top):
        text(label, x, top, 15, MUTED)
        text(value, x, top + 27, 37, INK, True)

    people = after["androidHomeMembers"]
    jobs = after["androidWorkMembers"]
    movers = sum(row["commuteSamples"] > 0 for row in after["androids"])
    arrivals = sum(row["workSamples"] > 0 for row in followup["androids"])
    chargers = sum(row["chargeStarts"] > 0 for row in after["androids"])

    rect(0, 0, W, H, DARK)
    image(frames["wide"], 350, 0, 610, 540)
    rect(0, 0, 393, 540, DARK)
    rect(46, 50, 44, 5, AMBER)
    text("PLATEAU Android Economy", 46, 71, 24, MINT, True)
    text("アンドロイドが", 46, 148, 39, WHITE, True)
    text("暮らす街の雇用を", 46, 202, 39, WHITE, True)
    text("測る", 46, 256, 39, WHITE, True)
    note(["名古屋 × Cities: Skylines", "実ゲーム上の生活・雇用実験"], 46, 362, 16, 31, MINT)
    text("PLATEAU AWARD 2026 一般部門", 46, 477, 13, WHITE)
    deck.showPage()

    base(2, "01  実験の問い", "同じ街で、働き手の内訳はどう変わるか")
    image(frames["wide"], 44, 120, 525, 295)
    rect(597, 120, 319, 295, MINT)
    text("名古屋を共通の空間に", 621, 148, 21, INK, True)
    note(["PLATEAUの建物配置で街区を構成。",
          "CS1の住居・職場・道路を使って",
          "通勤と雇用を観測する。"], 621, 198, 14, 30)
    text("比較する介入", 621, 320, 16, INK, True)
    text("導入数・空き住居・充電条件", 621, 354, 14, MUTED)
    text("建物の出典  Project PLATEAU  |  道路  © OpenStreetMap contributors", 44, 448, 12, MUTED)
    deck.showPage()

    base(3, "02  MODの仕組み", "住居と職場を、CS1の市民IDで追う")
    for x, label, lines in [
        (44, "住む", ["空いている住宅ユニットに", "アンドロイドの市民IDを登録。"]),
        (339, "働く", ["CS1の就業結果から、", "職場ユニットへの所属を確認。"]),
        (634, "充電する", ["在宅時に電池を回復する。", "充電速度はMOD内の仮定。"])
    ]:
        rect(x, 147, 282, 203, WHITE)
        text(label, x + 22, 169, 26, INK, True)
        note(lines, x + 22, 237, 16, 33)
    note(["人の就業人数と、アンドロイドの就業人数を分けて記録。",
          "通勤は市民の移動状態と経路IDで判定する。"], 44, 396, 16, 28)
    deck.showPage()

    base(4, "03  実ゲームの計測", "導入直前と導入後のスナップショット")
    before_jobs = before["humansWithWorkplace"]
    after_jobs = after["humansWithWorkplace"]
    rect(44, 128, 872, 224, WHITE)
    for label, value, x in [("導入直前の人の就業", f"{before_jobs}人", 68),
                            ("導入後の人の就業", f"{after_jobs}人", 350),
                            ("導入後のAndroid就業", f"{jobs}体", 630)]:
        metric(label, value, x, 152)
    text(f"住宅登録 {people}体　　通勤経路 {movers}体　　充電開始 {chargers}体", 68, 273, 20, INK, True)
    note([f"追跡を継続し、{arrivals}体が職場に到着した記録も確認した。",
          "同じセーブを続けて観測した結果。時間経過や街の変化も含む。",
          "アンドロイド導入による純粋な雇用効果は、同じ期間の対照実験で検証する。"], 44, 385, 15, 32)
    deck.showPage()

    base(5, "04  実ゲーム画面", "街の中で、数値と行動を照合する")
    image(frames["street"], 44, 116, 425, 270)
    image(frames["life"], 492, 116, 424, 270)
    text("同じ歩道を使う人とアンドロイド", 44, 404, 15, INK, True)
    text("住居・雇用試験のゲーム内表示", 492, 404, 15, INK, True)
    text("出典  実際のCS1画面を収録。画像上の表示と実行ログを照合。", 44, 455, 12, MUTED)
    deck.showPage()

    base(6, "05  比較実験", "空き住居と求人を、街の制約として扱う")
    rect(44, 126, 872, 67, MINT)
    text("固定するもの", 65, 146, 18, INK, True)
    text("同じセーブ、観測期間、基本の都市条件", 323, 147, 17, INK)
    rect(44, 208, 872, 67, WHITE)
    text("一つずつ変更", 65, 228, 18, INK, True)
    text("導入の有無、台数、充電条件", 323, 229, 17, INK)
    rect(44, 290, 872, 67, WHITE)
    text("確認する結果", 65, 310, 18, INK, True)
    text("人とAndroidの就業、通勤、空き住居、充電待ち", 323, 311, 17, INK)
    note(["現在の雇用検証は駅周辺の試験。市全域の雇用効果は未検証。",
          "ゲーム内の結果を、現実の労働市場の予測には使用しない。"], 44, 401, 15, 31)
    deck.showPage()

    valid = [row for row in zoning["bindings"] if row.get("nativeIdentity") and row.get("nativeResidents", 0) > 0]
    site = valid[0]
    base(7, "06  PLATEAU建物の利用", "PLATEAU建物をCS1の住宅にする")
    image(frames["zoning"], 44, 122, 526, 296)
    rect(592, 122, 324, 296, MINT)
    text("低密度住宅", 615, 147, 23, INK, True)
    note([f"CS1建物ID  {site['buildingId']}", f"住宅区画  {site['nativeZoneCells']}セル",
          f"観測時の入居  {site['nativeResidents']}人"], 615, 203, 17, 39)
    text(f"{len(zoning['bindings'])}棟の候補から{len(valid)}棟で入居を確認。", 615, 353, 14, INK, True)
    text("元のPLATEAUモデルを表示し、CS1が住宅として住民を管理する。", 44, 444, 14, MUTED)
    deck.showPage()
    deck.save()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    before = load(args.raw / "employment-baseline.json")
    after = load(args.raw / "employment-v2-commutes.json")
    followup = load(args.raw / "employment-after.json")
    zoning = load(args.raw / "plateau-zone-lived.json")
    if not before["runId"] == after["runId"] == followup["runId"] or before["androidHomeMembers"] != 0 or after["androidHomeMembers"] <= 0:
        raise ValueError("Employment evidence must refer to one before/after scenario")
    frames = {name: args.raw / f"{name}.png" for name in ("wide", "street")}
    frames["life"] = args.raw / "commute-preview.png"
    frames["zoning"] = args.raw / "plateau-housing-20261119.png"
    render(args.output, before, after, followup, zoning, frames)


if __name__ == "__main__":
    main()
