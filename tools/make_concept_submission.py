"""Typeset the Nagoya 2050 competition deck from CS1 captures and native readings."""

import argparse
import io
import json
import os
from pathlib import Path

from PIL import Image, ImageOps
from reportlab.lib.colors import HexColor
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen.canvas import Canvas


W, H = 960, 540
NIGHT, INK = "#131D24", "#17222A"
PAPER, LIGHT = "#F5F4EF", "#FCFCF9"
MINT, SOFT_MINT = "#A9EFDB", "#DAEFE7"
LIME, GRAY, RULE = "#DFFD8D", "#4C5F64", "#CDD9D3"
PLATEAU = "https://www.mlit.go.jp/plateau/"
OSM = "https://www.openstreetmap.org/copyright"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


class Deck:
    def __init__(self, output):
        fonts = Path(os.environ["WINDIR"]) / "Fonts"
        pdfmetrics.registerFont(TTFont("Japanese", str(fonts / "meiryo.ttc"), subfontIndex=0))
        pdfmetrics.registerFont(TTFont("Japanese Bold", str(fonts / "meiryob.ttc"), subfontIndex=0))
        self.canvas = Canvas(str(output), pagesize=(W, H), pageCompression=1)
        self.canvas.setTitle("名古屋2050 | PLATEAU Android Economy")
        self.canvas.setSubject("アンドロイドと人が暮らす、Cities: Skylinesの名古屋")

    def box(self, x, y, w, h, color):
        self.canvas.setFillColor(HexColor(color))
        self.canvas.rect(x, H - y - h, w, h, fill=1, stroke=0)

    def text(self, value, x, y, size=18, color=INK, bold=False, width=None, latin=False):
        font = "Helvetica-Bold" if latin else "Japanese Bold" if bold else "Japanese"
        max_width = W - x - 10 if width is None else width
        if pdfmetrics.stringWidth(value, font, size) > max_width or y + size > H - 8:
            raise ValueError(f"Text exceeds safe layout at {x}, {y}: {value}")
        self.canvas.setFont(font, size)
        self.canvas.setFillColor(HexColor(color))
        self.canvas.drawString(x, H - y - size, value)

    def lines(self, values, x, y, size=18, spacing=None, **style):
        for i, value in enumerate(values):
            self.text(value, x, y + i * (spacing or size * 1.5), size, **style)

    def photo(self, path, x, y, w, h, center=(.5, .5), crop=None):
        with Image.open(path) as pixels:
            pixels = pixels.convert("RGB")
            if crop:
                pixels = pixels.crop(crop)
            image = ImageOps.fit(pixels, (round(w * 1.65), round(h * 1.65)),
                                 method=Image.Resampling.LANCZOS, centering=center)
            buffer = io.BytesIO()
            image.save(buffer, "JPEG", quality=88, optimize=True)
        buffer.seek(0)
        self.canvas.drawImage(ImageReader(buffer), x, H - y - h, w, h, mask="auto")

    def header(self, no, title, sub=None):
        self.box(0, 0, W, H, PAPER)
        self.text(title, 44, 34, 34, bold=True)
        if sub:
            self.text(sub, 46, 90, 15, GRAY)
        self.box(44, 496, 872, 1, RULE)
        self.text("PLATEAU ANDROID ECONOMY", 44, 508, 10, GRAY, latin=True)
        self.text(f"{no:02d} / 06", 877, 508, 10, GRAY, latin=True)

    def page(self):
        self.canvas.showPage()

    def link(self, text, url, x, y, size=11, color=SOFT_MINT):
        self.text(text, x, y, size, color)
        width = pdfmetrics.stringWidth(text, "Japanese", size)
        self.canvas.linkURL(url, (x, H - y - size - 4, x + width, H - y + 2), relative=0)


def render(args):
    android = read(args.raw / "citywide-androids.json")
    wards = read(args.raw / "citywide-wards.json")
    runs = read(args.evidence)
    photos = args.prior
    fresh = args.raw / "cs1-nagoya-live.jpg"
    if wards["wardsWithHumanAndAndroidPopulationAndJobs"] != 16 or android["androidHomeMembers"] <= 0:
        raise ValueError("Current capabilities require evidence across all 16 wards")
    deck = Deck(args.output)

    deck.box(0, 0, W, H, NIGHT)
    deck.photo(photos / "01-nagoya.png", 429, 0, 531, 540, center=(.5, .51), crop=(630, 26, 1410, 1048))
    deck.box(42, 52, 41, 5, LIME)
    deck.text("PLATEAU ANDROID ECONOMY", 42, 72, 16, MINT, latin=True)
    deck.text("名古屋 2050", 40, 128, 29, MINT, True)
    deck.lines(("隣人は、", "アンドロイド。"), 40, 182, 46, 67, color=LIGHT, bold=True, width=386)
    deck.lines(("人とアンドロイドが住み、働く街をつくる。", "Cities: Skylinesで試す、名古屋の未来。"),
               43, 347, 15, 29, color=SOFT_MINT, width=374)
    deck.text("PLATEAU AWARD 2026 一般部門", 43, 483, 13, LIGHT)
    deck.box(689, 501, 244, 26, NIGHT)
    deck.text("CS1ゲーム画面・名古屋駅周辺の先行デモ", 700, 507, 10, LIGHT)
    deck.page()

    deck.header(2, "住む。働く。充電する。", "アンドロイドの一日を、街の中で追いかける。")
    deck.photo(photos / "02-coexistence.png", 44, 126, 451, 219, crop=(458, 570, 858, 825), center=(.52, .65))
    deck.photo(photos / "03-employment.png", 510, 126, 406, 219, center=(.49, .46), crop=(632, 142, 1399, 576))
    deck.box(44, 320, 227, 25, NIGHT)
    deck.text("名古屋駅周辺・歩道での先行デモ", 55, 326, 11, LIGHT)
    deck.box(510, 320, 221, 25, NIGHT)
    deck.text("名古屋駅周辺・街路の先行デモ", 520, 326, 11, LIGHT)
    for x, heading, body in (
        (44, "家に住む", ("人と同じ街の住宅に", "アンドロイドも入居する。")),
        (338, "街で働く", ("職場に配属され、", "ゲームの経路で通勤する。")),
        (631, "家で充電する", ("移動で電池を使い、", "帰宅すると充電できる。")),
    ):
        deck.box(x, 370, 268, 2, RULE)
        deck.text(heading, x, 389, 20, INK, True)
        deck.lines(body, x, 424, 15, 23, color=GRAY)
    deck.page()

    deck.header(3, "名古屋16区が、舞台になる。", "知っている街並みに、住む場所と働く場所を重ねる。")
    deck.photo(fresh, 44, 130, 602, 330, center=(.46, .48))
    deck.box(44, 433, 243, 27, NIGHT)
    deck.text("CS1ゲーム画面・全域版の一部", 55, 440, 11, LIGHT)
    deck.box(664, 130, 252, 330, NIGHT)
    for y, label, value, detail in (
        (153, "まちの形", "738,033", "表示カタログのPLATEAU建物ID"),
        (248, "つながる道路", f'{wards["activeNativeRoads"]:,}', "CS1に敷設・読込した道路区間"),
        (343, "暮らしの場所", f'{wards["activeNativeZonedSites"]:,}', "稼働時の有効な用途付き建物"),
    ):
        deck.text(label, 682, y, 13, MINT, True)
        deck.text(value, 682, y + 20, 32, LIGHT, latin=True)
        deck.text(detail, 683, y + 58, 11, SOFT_MINT)
    deck.text("PLATEAUの建物を、街区ごとにCS1の住宅・店舗・工業・オフィスにつなぐ。", 45, 468, 14, GRAY)
    deck.page()

    deck.header(4, "暮らしが、仕事につながる。", "同じ街を動かし、住民の所属先と移動を確認した。")
    deck.box(44, 130, 872, 136, NIGHT)
    deck.text("アンドロイド", 65, 150, 15, MINT, True)
    deck.text(f'{android["androidHomeMembers"]:,}', 64, 179, 53, LIGHT, latin=True)
    deck.text("体が入居", 174, 214, 19, SOFT_MINT)
    deck.text(f'{android["androidWorkMembers"]:,}', 363, 179, 53, LIGHT, latin=True)
    deck.text("体が職場に登録", 472, 214, 19, SOFT_MINT)
    deck.text("16", 716, 179, 54, LIME, latin=True)
    deck.text("区で共存", 787, 214, 19, SOFT_MINT)
    traffic = wards["nativeTraffic"]
    for x, title, value, desc in (
        (44, "通勤", str(runs["androidsWithConfirmedCommute"]), "経路を確認した個体"),
        (338, "充電", str(runs["androidsWithObservedChargeStart"]), "充電開始を確認した個体"),
        (632, "交通", f'{traffic["passengerCars"] + traffic["cargoTrucks"] + traffic["buses"]:,}', "乗用車・トラック・バスの台数"),
    ):
        deck.box(x, 286, 284, 125, SOFT_MINT)
        deck.text(title, x + 15, 298, 14, GRAY, True)
        deck.text(value, x + 14, 320, 37, INK, latin=True)
        deck.text(desc, x + 15, 373, 13, INK)
    deck.text(f'人の住民 {android["nativeHumanResidents"]:,}人も同じ都市に入居。就業と入居を全16区で確認。', 45, 430, 14, INK)
    deck.text("2026年11月19日のCS1実測。通勤・充電は別の観測区間の累計、入居・職場・交通は観測時点。", 45, 461, 11, GRAY)
    deck.page()

    deck.header(5, "街を変えたら、暮らしはどう変わる？", "同じ名古屋を使って、違う街のつくり方を試す。")
    deck.photo(photos / "04-plateau-housing.png", 44, 126, 405, 329, center=(.51, .43), crop=(521, 145, 1172, 775))
    deck.box(44, 429, 252, 26, NIGHT)
    deck.text("名古屋駅周辺・PLATEAUモデルの先行デモ", 54, 436, 10, LIGHT)
    for y, title, body in (
        (132, "住宅を増やしてみる", ("新しい住民を迎えると、どの区に", "人とアンドロイドが集まるだろう。")),
        (245, "職場の配置を変えてみる", ("駅の周りに仕事を集めると、", "通勤の距離や人の就業はどう変わるか。")),
        (358, "配送のルートを考える", ("工場と店舗をつなぐ配送を試作中。", "実際に届き、店舗の在庫が動くかを確かめる。")),
    ):
        deck.box(470, y, 446, 2, RULE)
        deck.text(title, 471, y + 13, 22, INK, True)
        deck.lines(body, 472, y + 48, 15, 23, color=GRAY, width=444)
    deck.page()

    deck.box(0, 0, W, H, NIGHT)
    deck.photo(photos / "04-plateau-housing.png", 568, 0, 392, 540, center=(.65, .5), crop=(522, 15, 1175, 875))
    deck.text("PLATEAU ANDROID ECONOMY", 44, 53, 16, MINT, latin=True)
    deck.lines(("あなたなら、", "どんな共存の街を", "つくる？"), 43, 120, 44, 62, color=LIGHT, bold=True, width=518)
    deck.lines(("街づくりゲームの中で、2050年の選択を試す。", "まちづくりのワークショップでも、", "知っている場所から将来を話し始められる。"),
               46, 324, 15, 25, color=SOFT_MINT, width=501)
    deck.box(46, 421, 485, 1, "#49646C")
    deck.link("3D都市モデル　国土交通省 Project PLATEAU・名古屋市 2022年度", PLATEAU, 46, 439)
    deck.link("道路データ　© OpenStreetMap contributors", OSM, 46, 460)
    deck.text("Cities: Skylines の独自MOD / CS1ゲーム画面", 46, 482, 11, SOFT_MINT)
    deck.text("06 / 06", 477, 510, 10, MINT, latin=True)
    deck.box(706, 501, 227, 26, NIGHT)
    deck.text("CS1ゲーム画面・名古屋駅周辺の先行デモ", 714, 507, 10, LIGHT)
    deck.page()
    deck.canvas.save()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("raw", "prior", "evidence", "output"):
        parser.add_argument("--" + key, required=True, type=Path)
    render(parser.parse_args())
