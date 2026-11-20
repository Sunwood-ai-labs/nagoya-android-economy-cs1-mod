"""Verify one game's before/after pair and archive it alongside the matching aerial crop."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
WORK = ROOT / 'station-precinct'
EVIDENCE = Path('D:/Prj/nagoya-android-economy-cs1-mod/docs/evidence/native-real-terrain-20261120/block-by-block')
GSI = 'https://maps.gsi.go.jp/#18/35.17088/136.88156/&base=seamlessphoto&ls=seamlessphoto&disp=1'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def archive(block):
    audit = WORK / 'density-audit'
    pack = WORK / 'blocks'
    plan = read(pack / f'block-{block}-plan.json')
    record = read(pack / f'block-{block}-runtime.json')
    source = read(audit / 'orthophoto-source.json')
    observations = audit / 'block-observations.json'
    observed = read(observations).get(block) if observations.is_file() else None
    if any(data['id'] != block for data in (plan, record)) or plan['bounds'] != record['bounds']:
        raise ValueError('Mismatched block IDs or bounds')
    dest = EVIDENCE / block
    dest.mkdir(parents=True, exist_ok=True)
    poses, photos = [], []
    for key in ('before', 'after'):
        spec = record[key]
        poses.append({k: v for k, v in spec['settings'].items() if k != 'name'})
        original = ROOT / spec['name']
        with Image.open(original) as capture:
            if capture.size != (1600, 900):
                raise ValueError(f'Wrong capture canvas for {block} {key}')
            capture.verify()
        target = dest / f'{key}.png'
        shutil.copy2(original, target)
        if digest(target) != spec['sha256']:
            raise ValueError(f'Capture hash mismatch for {block} {key}')
        with Image.open(target) as capture:
            photos.append(capture.convert('RGB').crop((388, 137, 1212, 764)))
    if poses[0] != poses[1]:
        raise ValueError(f'Camera moved in block {block}')

    xmin, _, _, zmax = source['gameBoundsXz']
    ppm = source['pixelsPerGameMeter']
    west, south, east, north = plan['bounds']
    with Image.open(audit / 'gsi-orthophoto.png') as full:
        aerial = full.convert('RGB').crop(((west - xmin)*ppm, (zmax - north)*ppm, (east - xmin)*ppm, (zmax - south)*ppm))
    aerial.save(dest / 'gsi-august-2020.jpg', quality=94)
    panel = Image.new('RGB', (1500, 475), '#151c23')
    labels = ('GSI aerial / August 2020', 'Cities: Skylines / before', 'Cities: Skylines / after')
    for i, (image, label) in enumerate(zip((aerial, *photos), labels)):
        ratio = min(480 / image.width, 394 / image.height)
        image = image.resize((round(ratio * image.width), round(ratio * image.height)), Image.Resampling.LANCZOS)
        x = i * 500
        ImageDraw.Draw(panel).text((x + 10, 11), f'{block} | {label}', fill='white')
        panel.paste(image, (x + (500 - image.width)//2, 36 + (394 - image.height)//2))
    ImageDraw.Draw(panel).text((10, 449), 'Aerial imagery: Geospatial Information Authority of Japan. Same CS1 camera settings before and after.', fill='white')
    panel.save(dest / 'comparison.jpg', quality=92)
    with Image.open(dest / 'comparison.jpg') as image:
        image.verify()
    for suffix in ('plan', 'runtime'):
        shutil.copy2(pack / f'block-{block}-{suffix}.json', dest / f'{suffix}.json')
    note = f"""# {block} {plan['name']}

## 整備前の照合

{plan['compare']} [国土地理院の航空写真]({GSI})

## 実機で反映した内容

{plan['work']}

同じ座標・ズーム・方位・角度で前後を撮影した。起動中のゲームが区画{block}のメッシュ{plan['renderObjects']}個、三角形{plan['triangles']}件を受け付けた。

[3枚の比較](comparison.jpg) · [航空写真の切り出し](gsi-august-2020.jpg) · [整備前](before.png) · [整備後](after.png) · [実機の応答と画像のハッシュ](runtime.json) · [座標と生成内容](plan.json)
"""
    if observed:
        note += f'\n## 前後の画面で確認したこと\n\n{observed}\n'
    (dest / 'README.md').write_text(note, encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('block', choices=[f'{i:02}' for i in range(1, 9)])
    archive(parser.parse_args().block)
