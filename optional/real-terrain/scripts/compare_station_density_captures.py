"""Record comparable field-of-view screenshots and a game-only grass color estimate."""
import json

import numpy as np
from PIL import Image, ImageDraw

from audit_station_orthophoto import BOUNDS, OUT, PIXELS_PER_METER
from nagoya_map import ROOT

VIEWS = {
    'overview': BOUNDS,
    'east': (-1410, 900, -1285, 1020),
    'west': (-1570, 895, -1450, 1012),
    'north': (-1508, 985, -1372, 1100),
    'south': (-1450, 795, -1310, 922),
}


def grass_percentage(image):
    rgb = np.asarray(image).astype(np.int16)
    hsv = np.asarray(image.convert('HSV'))
    r, g, b = (rgb[:, :, i] for i in range(3))
    hue, saturation, value = (hsv[:, :, i] for i in range(3))
    return round(100 * float(((hue >= 28) & (hue <= 69) & (saturation >= 62) & (value >= 28)
                             & (g >= r + 4) & (g >= b + 10)).mean()), 2)


def main():
    photo = Image.open(OUT / 'gsi-orthophoto.png')
    xmin, _, _, zmax = BOUNDS
    statistics = {}
    for key, extent in VIEWS.items():
        west, south, east, north = extent
        crop = (round((west - xmin) * PIXELS_PER_METER), round((zmax - north) * PIXELS_PER_METER),
                round((east - xmin) * PIXELS_PER_METER), round((zmax - south) * PIXELS_PER_METER))
        aerial = photo.crop(crop).convert('RGB')
        filenames = {phase: f'nagoya-station-density-{phase}-{key}.png' for phase in ('before', 'after')}
        screenshots = {phase: Image.open(ROOT / name).convert('RGB') for phase, name in filenames.items()}
        if any(image.size != (1600, 900) for image in screenshots.values()):
            raise ValueError('Expected the same native screenshot canvas')
        # Use one matching central crop before and after. Exclude the overlay, status bar and far corners.
        screen_crop = (390, 170, 1280, 740)
        values = {phase: grass_percentage(img.crop(screen_crop)) for phase, img in screenshots.items()}
        statistics[key] = {'aerialBoundsXZ': extent, 'captureFiles': filenames, 'cropPixels': screen_crop,
                           'grassColoredPixelsPercent': values,
                           'percentagePointReduction': round(values['before'] - values['after'], 2)}
        sheet = Image.new('RGB', (1560, 570), '#161c23')
        draw = ImageDraw.Draw(sheet)
        panel = (290, 10, 1310, 801)
        for i, (label, img) in enumerate((('GSI orthophoto / August 2020', aerial), ('Cities: Skylines / before', screenshots['before'].crop(panel)),
                                          ('Cities: Skylines / after', screenshots['after'].crop(panel)))):
            width = 500
            img.thumbnail((width, 500), Image.Resampling.LANCZOS)
            x = i * 520 + 10
            draw.text((x, 11), f'{key.upper()}  |  {label}', fill='white')
            sheet.paste(img, (x + (width - img.width) // 2, 37 + (500 - img.height) // 2))
        draw.text((10, 546), 'Aerial imagery: Geospatial Information Authority of Japan. Game views use identical camera settings per pair.', fill='white')
        sheet.save(OUT / f'gsi-cs1-{key}.jpg', quality=92)
    (OUT / 'capture-comparison.json').write_text(json.dumps({'metric': 'Visible grass-colored pixels in a constant game screenshot crop; a proxy, not ground area.',
                                                             'views': statistics}, indent=2) + '\n')


if __name__ == '__main__':
    main()
