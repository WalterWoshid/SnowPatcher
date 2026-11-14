"""Export and check the icon. Dependencies: pip install Pillow resvg-py.

Use --update-assets to refresh the app and installer icons after approving a design.
"""
import argparse
import io
import json
import math
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET

import resvg_py
from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageStat


SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def points(text):
    numbers = [float(n) for n in re.findall(r'-?\d+(?:\.\d+)?', text)]
    return list(zip(numbers[::2], numbers[1::2]))


def rotate(point, degrees):
    x, y = point
    angle = math.radians(degrees)
    return x * math.cos(angle) - y * math.sin(angle), x * math.sin(angle) + y * math.cos(angle)


def error(left, right):
    return max(min(math.dist(p, q) for q in right) for p in left)


def wrench_bounds(data, angle=45):
    tokens = re.findall(r'[MLCAZ]|-?\d+(?:\.\d+)?', data.split('Z')[0])
    position = (0, 0)
    values = []
    while tokens:
        command = tokens.pop(0)
        count = {'M': 2, 'L': 2, 'C': 6, 'A': 7}[command]
        numbers = [float(tokens.pop(0)) for _ in range(count)]
        endpoint = tuple(numbers[-2:])
        if command == 'C':
            controls = [rotate(p, angle) for p in (position, tuple(numbers[:2]), tuple(numbers[2:4]), endpoint)]
            times = {0, 1}
            for axis in (0, 1):
                p, q, r, s = [point[axis] for point in controls]
                a, b, c = -p + 3*q - 3*r + s, 2*(p - 2*q + r), q - p
                if abs(a) < 1e-12:
                    roots = [-c / b] if abs(b) > 1e-12 else []
                else:
                    discriminant = b*b - 4*a*c
                    roots = [(-b + sign * math.sqrt(discriminant)) / (2*a) for sign in (-1, 1)] if discriminant >= 0 else []
                times.update(t for t in roots if 0 < t < 1)
            values.extend(tuple(sum(controls[i][axis] * factor for i, factor in enumerate(((1-t)**3, 3*(1-t)**2*t, 3*(1-t)*t*t, t**3))) for axis in (0, 1)) for t in times)
        elif command == 'A':
            # The exposed circular ring includes all four diagonal extrema.
            cx, cy = rotate((0, 90), angle)
            radius = numbers[0]
            values.extend(((cx-radius, cy-radius), (cx+radius, cy+radius)))
        else:
            values.append(rotate(endpoint, angle))
        position = endpoint
    return [min(p[0] for p in values), min(p[1] for p in values), max(p[0] for p in values), max(p[1] for p in values)]


def check(svg):
    root = ET.fromstring(svg)
    elements = {element.get('id'): element for element in root.iter() if element.get('id')}
    if root.get('viewBox') != '0 0 256 256' or elements['snowflake'].get('transform') != 'translate(128 128)':
        raise ValueError('The snowflake must remain centered in the 256-unit viewport.')
    arm = points(elements['snowflake-arm'].get('d'))
    uses = list(elements['snowflake'])
    expected = [None] + [f'rotate({n})' for n in range(60, 360, 60)]
    if [use.get('transform') for use in uses] != expected or any(use.get('href') != '#snowflake-arm' for use in uses):
        raise ValueError('The snowflake must repeat one arm six times at 60-degree intervals.')
    snow = [rotate(p, n) for n in range(0, 360, 60) for p in arm]
    arm_error = error([(-x, y) for x, y in arm], arm)
    rotation_error = error([rotate(p, 60) for p in snow], snow)
    transform = elements['wrench-placement'].get('transform')
    values = [float(n) for n in re.findall(r'-?\d+(?:\.\d+)?', transform)]
    cx, cy, scale, tx, ty, angle = values
    local = wrench_bounds(elements['wrench'].get('d'), angle)
    bounds = [cx + scale*(local[0]+tx), cy + scale*(local[1]+ty), cx + scale*(local[2]+tx), cy + scale*(local[3]+ty)]
    center_error = math.dist(((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2), (128, 128))
    extent_error = abs(max(bounds[2] - bounds[0], bounds[3] - bounds[1]) - 212)
    if max(arm_error, rotation_error) > 1e-7 or max(center_error, extent_error) > 1e-5:
        raise ValueError(f'Icon geometry failed: {arm_error=}, {rotation_error=}, {center_error=}, {extent_error=}')
    if 'M 10,90 A 10,10 0 1,0 -10,90 A 10,10 0 1,0 10,90 Z' not in elements['wrench'].get('d'):
        raise ValueError('The wrench ring must remain a centered circular hole.')
    outline = points(elements['wrench'].get('d').split('Z')[0])
    # The upper three vertices are covered by the handle.
    ring = [
        (17*math.cos(math.radians(-135-22.5*i)), 90+17*math.sin(math.radians(-135-22.5*i))) for i in range(13)]
    ring_error = error(ring, outline)
    if ring_error > 1e-8:
        raise ValueError('The 16-sided ring must share the circular hole center.')
    head = [(x, y) for x, y in outline if y < 0]
    head_width = max(x for x, y in head) - min(x for x, y in head)
    if elements['wrench-clearance'].tag.split('}')[-1] != 'mask':
        raise ValueError('The transparent wrench clearance is missing.')
    silhouette = elements['wrench-silhouette'].get('d')
    if silhouette != elements['wrench'].get('d').split('Z')[0] + 'Z':
        raise ValueError('The wrench cutout no longer matches its silhouette.')
    cutout = list(elements['wrench-clearance'])[-1]
    if cutout.get('href') != '#wrench-silhouette' or cutout.get('transform') != transform:
        raise ValueError('The wrench cutout is not aligned with the foreground.')
    if any(element.tag.endswith(('image', 'script', 'filter')) for element in root.iter()):
        raise ValueError('Only vector geometry belongs in the master icon.')
    return {'snowflakeArmMirrorError': arm_error, 'snowflakeRotationError': rotation_error,
            'wrenchIntentionallyAsymmetric': True, 'largerHook': 'upper-left', 'wrenchCenterError': center_error,
            'wrenchExtentError': extent_error, 'wrenchBounds': bounds, 'ringRadius': 10,
            'ringOuterRadius': 17, 'ringFacets': 16, 'ringToHeadWidthRatio': 34/head_width,
            'ringConcentricError': ring_error}


def render(svg, size):
    png = resvg_py.svg_to_bytes(svg_string=svg.decode() if isinstance(svg, bytes) else svg, width=size, height=size)
    image = Image.open(io.BytesIO(png)).convert('RGBA')
    if image.size != (size, size) or image.getpixel((0, 0))[3] != 0:
        raise ValueError('The SVG did not render at the expected size with transparency.')
    return image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / 'artifacts/icons')
    parser.add_argument('--update-assets', action='store_true', help='Replace the app ICO and PNG with the verified exports.')
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1] / 'assets/icons/snowpatcher.svg'
    svg = source.read_bytes()
    report = check(svg)
    # Check the actual raster output independently of the coordinate checks.
    ET.register_namespace('', 'http://www.w3.org/2000/svg')
    for name in ('snowflake-layer',):
        isolated = ET.fromstring(svg)
        for element in list(isolated):
            if element.get('id') in ('snowflake-layer', 'wrench-placement') and element.get('id') != name:
                isolated.remove(element)
            if element.get('id') == name:
                element.attrib.pop('mask', None)
        alpha = render(ET.tostring(isolated), 1024).getchannel('A')
        reflected = alpha.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        difference = ImageStat.Stat(ImageChops.difference(alpha, reflected)).sum[0]
        raster_error = difference / ImageStat.Stat(alpha).sum[0]
        if raster_error > 0.005:
            raise ValueError(f'{name} rendered asymmetrically: {raster_error}')
        report[name + 'RasterMirrorError'] = raster_error
    output = args.output
    output.mkdir(parents=True, exist_ok=True)
    images = {}
    for size in SIZES:
        # Supersampling keeps slanted edges clean at native tray sizes.
        image = render(svg, size * 4).resize((size, size), Image.Resampling.LANCZOS)
        image.save(output / f'snowpatcher-{size}.png')
        images[size] = image
    images[256].save(output / 'snowpatcher.ico', format='ICO', sizes=[(n, n) for n in SIZES],
                     append_images=[images[n] for n in SIZES if n != 256])
    with Image.open(output / 'snowpatcher.ico') as icon:
        if icon.ico.sizes() != {(n, n) for n in SIZES}:
            raise ValueError('The ICO is missing a requested resolution.')
        for size in SIZES:
            frame = icon.ico.getimage((size, size)).convert('RGBA')
            if frame.tobytes() != images[size].tobytes():
                raise ValueError(f'The ICO frame differs from the PNG at {size}px.')
    font_path = Path('C:/Windows/Fonts/segoeui.ttf')
    font = ImageFont.truetype(str(font_path), 18) if font_path.exists() else ImageFont.load_default()
    board = Image.new('RGB', (960, 560), '#f0f0f0')
    draw = ImageDraw.Draw(board)
    for x, background, label in ((0, '#f0f0f0', 'Light background'), (480, '#20252b', 'Dark background')):
        draw.rectangle((x, 0, x + 479, 559), fill=background)
        color = '#303030' if x == 0 else '#e7e7e7'
        draw.text((x + 30, 20), label, font=font, fill=color)
        board.paste(images[256], (x + 112, 70), images[256])
        for offset, size in enumerate((16, 20, 24, 32, 48, 64)):
            left = x + 24 + offset * 74
            board.paste(images[size], (left + (64 - size) // 2, 405 + (64 - size) // 2), images[size])
            draw.text((left + 20, 489), str(size), font=font, fill=color)
    board.save(output / 'preview.png')
    report['exportedSizes'] = list(SIZES)
    (output / 'geometry.json').write_text(json.dumps(report, indent=2) + '\n')
    if args.update_assets:
        shutil.copyfile(output / 'snowpatcher.ico', source.with_suffix('.ico'))
        shutil.copyfile(output / 'snowpatcher-128.png', source.with_suffix('.png'))
    print(f'Geometry verified; SVG rendered and PNG/ICO exports saved to {output}')


if __name__ == '__main__':
    main()
