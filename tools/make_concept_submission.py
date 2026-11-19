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
PROJECT_TITLE = "Android Skyline"
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
        self.canvas.setTitle(f"{PROJECT_TITLE} | 名古屋2050")
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
        self.text(PROJECT_TITLE, 44, 508, 10, GRAY, latin=True)
        self.text(f"{no:02d} / 04", 877, 508, 10, GRAY, latin=True)

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
    if (wards["wardsWithHumanAndAndroidPopulationAndJobs"] != 16
            or android["androidHomeMembers"] <= 0
            or runs["androidsWithObservedWorkArrival"] <= 0
            or runs["androidsWithObservedChargeStart"] <= 0):
        raise ValueError("Current capabilities require evidence across all 16 wards")
    delivery_caption = ("工場と店を結ぶ配送は試作中。",)
    if args.delivery:
        delivery = read(args.delivery)
        sent, received = delivery["sender"], delivery["receiver"]
        amount = delivery["confirmedGoodsTransferred"]
        if not amount > 0 or sent["stockBefore"] - sent["stockAfter"] != amount or received["stockAfter"] - received["stockBefore"] != amount:
            raise ValueError("Delivery must change both real stock buffers by the same amount")
        delivery_caption = (f'{delivery["ward"]}では、工場から店への', "配送を1件確認。")
    if args.delivery_coverage:
        coverage = read(args.delivery_coverage)
        wards_delivered = set(coverage["coveredAcrossRecordedSessions"])
        events = coverage["deliveryEvents"]
        if not 0 < len(wards_delivered) <= 16 or not events or any(
            event["ward"] not in wards_delivered or not event["stockAdded"] > 0
            or event["factoryBefore"] - event["factoryAfter"] != event["stockAdded"]
            or event["shopAfter"] - event["shopBefore"] != event["stockAdded"]
            for event in events
        ):
            raise ValueError("Coverage must agree with real goods transfers")
        delivery_caption = (
            f"{len(wards_delivered)}区で、工場から店への",
            "アンドロイド配送を確認。",
        )
    if args.return_evidence:
        evidence = read(args.return_evidence)["sameSessionCycle"]
        moving, charging = evidence["returnJourney"], evidence["atHomeCharging"]
        if (moving["citizenId"] != evidence["delivery"]["android"]
                or moving["citizenId"] != charging["citizenId"]
                or moving["shopReturnSource"] != evidence["delivery"]["to"]
                or charging["shopReturnsArrived"] <= moving["shopReturnsArrived"]
                or charging["location"] != "Home" or not charging["charging"]
                or charging["battery"] <= moving["battery"]):
            raise ValueError("Return and charging must be observed for the same courier")
        delivery_caption += ("配送後の帰宅・充電も確認。",)
    deck = Deck(args.output)

    deck.box(0, 0, W, H, NIGHT)
    deck.photo(photos / "01-nagoya.png", 429, 0, 531, 540, center=(.5, .51), crop=(630, 26, 1410, 1048))
    deck.box(42, 52, 41, 5, LIME)
    deck.text(PROJECT_TITLE, 41, 73, 37, MINT, latin=True, width=380)
    deck.text("名古屋2050", 41, 127, 23, MINT, True)
    deck.lines(("隣人は、", "アンドロイド。"), 40, 183, 46, 67, color=LIGHT, bold=True, width=386)
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

    deck.header(3, "この街は、名古屋からできている。", "PLATEAUの建物と実際の道路をもとに、名古屋16区をゲームの舞台に。")
    deck.photo(fresh, 44, 132, 585, 325, center=(.46, .48))
    deck.box(44, 430, 223, 27, NIGHT)
    deck.text("CS1ゲーム画面・全域版の一部", 55, 437, 11, LIGHT)
    deck.box(649, 132, 267, 325, NIGHT)
    deck.text("ゲームで確かめたこと", 669, 154, 20, MINT, True, width=227)
    deck.lines(("全16区で、人も", "アンドロイドも", "住み、働く。"), 669, 207, 25, 35,
               color=LIGHT, bold=True, width=228)
    deck.box(669, 334, 227, 1, "#49646C")
    deck.lines(delivery_caption, 670, 352, 13, 21, color=SOFT_MINT, width=227)
    deck.text("住む家、働く職場、商品が届く店。PLATEAUの建物が、暮らしの場所になる。",
              45, 469, 13, GRAY)
    deck.page()

    deck.box(0, 0, W, H, NIGHT)
    deck.photo(photos / "04-plateau-housing.png", 558, 0, 402, 540, center=(.65, .5), crop=(522, 15, 1175, 875))
    deck.text(PROJECT_TITLE, 44, 45, 17, MINT, latin=True)
    deck.lines(("あなたなら、", "どんな街にする？"), 43, 105, 44, 64, color=LIGHT, bold=True, width=505)
    deck.lines(("駅前に仕事を集めたら、誰がそこで働く？", "アンドロイドが増えたら、人の仕事はどう変わる？"),
               46, 257, 17, 35, color=SOFT_MINT, width=480)
    deck.text("街づくりゲームの中で、2050年の選択を試す。", 46, 350, 16, LIME, True, width=480)
    deck.box(46, 415, 485, 1, "#49646C")
    deck.link("3D都市モデル　国土交通省 Project PLATEAU・名古屋市 2022年度", PLATEAU, 46, 433)
    deck.link("道路データ　© OpenStreetMap contributors", OSM, 46, 454)
    deck.text("Cities: Skylines の独自MOD / CS1ゲーム画面", 46, 477, 11, SOFT_MINT)
    deck.text("04 / 04", 477, 510, 10, MINT, latin=True)
    deck.box(706, 501, 227, 26, NIGHT)
    deck.text("CS1ゲーム画面・名古屋駅周辺の先行デモ", 714, 507, 10, LIGHT)
    deck.page()
    deck.canvas.save()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("raw", "prior", "evidence", "output"):
        parser.add_argument("--" + key, required=True, type=Path)
    parser.add_argument("--delivery", type=Path)
    parser.add_argument("--delivery-coverage", type=Path)
    parser.add_argument("--return-evidence", type=Path)
    render(parser.parse_args())
