"""Save a coordinate-aligned GSI orthophoto for comparing the Nagoya Station game area."""
import io
import json
import math
from concurrent.futures import ThreadPoolExecutor
from urllib.request import urlopen

import numpy as np
from PIL import Image, ImageDraw
from pyproj import Transformer

from nagoya_map import ROOT
from prepare_refined_visuals import CENTER_X, CENTER_Z, SCALE

OUT = ROOT / "station-precinct" / "density-audit"
BOUNDS = (-1600, 790, -1240, 1100)
PIXELS_PER_METER = 4
ZOOM = 18
GSI = "https://cyberjapandata.gsi.go.jp/xyz/seamlessphoto"


def orthophoto():
    anchor = json.loads((ROOT.parent / "source" / "district-ground-footprints.json").read_bytes())
    xmin, zmin, xmax, zmax = BOUNDS
    transform = Transformer.from_crs(6675, 4326, always_xy=True)
    x = xmin + (np.arange((xmax - xmin) * PIXELS_PER_METER) + .5) / PIXELS_PER_METER
    z = zmax - (np.arange((zmax - zmin) * PIXELS_PER_METER) + .5) / PIXELS_PER_METER
    east, north = np.meshgrid(x / SCALE + CENTER_X + anchor["anchor_east_m"],
                              z / SCALE + CENTER_Z + anchor["anchor_north_m"])
    lon, lat = transform.transform(east, north)
    x = (lon + 180) / 360 * (256 << ZOOM)
    y = (1 - np.arcsinh(np.tan(np.deg2rad(lat))) / math.pi) / 2 * (256 << ZOOM)
    tx0, ty0 = math.floor(x.min() / 256), math.floor(y.min() / 256)
    tx1, ty1 = math.floor(x.max() / 256), math.floor(y.max() / 256)
    tiles = [(tx, ty) for tx in range(tx0, tx1 + 1) for ty in range(ty0, ty1 + 1)]
    cache = OUT / "gsi-tiles"
    cache.mkdir(parents=True, exist_ok=True)

    def fetch(tile):
        tx, ty = tile
        path = cache / f"{ZOOM}-{tx}-{ty}.jpg"
        if not path.exists():
            with urlopen(f"{GSI}/{ZOOM}/{tx}/{ty}.jpg", timeout=8) as response:
                data = response.read()
            with Image.open(io.BytesIO(data)) as image:
                if image.size != (256, 256):
                    raise ValueError(f"Unexpected GSI tile dimensions for {tile}")
                image.verify()
            path.write_bytes(data)
        with Image.open(path) as image:
            return tx, ty, image.convert("RGB")

    mosaic = Image.new("RGB", ((tx1 - tx0 + 1) * 256, (ty1 - ty0 + 1) * 256))
    with ThreadPoolExecutor(max_workers=8) as executor:
        for tx, ty, image in executor.map(fetch, tiles):
            mosaic.paste(image, ((tx - tx0) * 256, (ty - ty0) * 256))
    pixels = np.asarray(mosaic)
    local_x = np.rint(x - 256 * tx0).astype(int).clip(0, pixels.shape[1] - 1)
    local_y = np.rint(y - 256 * ty0).astype(int).clip(0, pixels.shape[0] - 1)
    return Image.fromarray(pixels[local_y, local_x]), tiles


def main():
    photo, tiles = orthophoto()
    photo.save(OUT / "gsi-orthophoto.png")
    photo = photo.convert("RGBA")
    grid = Image.new("RGBA", photo.size)
    draw = ImageDraw.Draw(grid)
    xmin, zmin, xmax, zmax = BOUNDS
    for x in range(xmin, xmax, 20):
        pixel = (x - xmin) * PIXELS_PER_METER
        draw.line((pixel, 0, pixel, photo.height), fill=(255, 228, 80, 105))
        draw.text((pixel + 2, 5), str(x), fill="white", stroke_width=1, stroke_fill="black")
    for z in range(800, zmax, 20):
        pixel = (zmax - z) * PIXELS_PER_METER
        draw.line((0, pixel, photo.width, pixel), fill=(255, 228, 80, 105))
        draw.text((3, pixel + 3), str(z), fill="white", stroke_width=1, stroke_fill="black")
    Image.alpha_composite(photo, grid).convert("RGB").save(OUT / "gsi-aligned-grid.jpg", quality=90)
    (OUT / "orthophoto-source.json").write_text(json.dumps({"source": GSI, "zoom": ZOOM, "tiles": tiles,
        "gameBoundsXz": BOUNDS, "pixelsPerGameMeter": PIXELS_PER_METER, "northUp": True}, indent=2) + "\n")


if __name__ == "__main__":
    main()
