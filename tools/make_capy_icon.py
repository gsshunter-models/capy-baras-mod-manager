"""Encode the project mascot as a multi-resolution Windows icon; preserve its alpha."""
from pathlib import Path
from PIL import Image

desktop = Path(__file__).resolve().parents[1] / 'Desktop'
mascot = Image.open(desktop / 'Assets' / 'capybara-orange.png').convert('RGBA')
if mascot.getchannel('A').getextrema()[0] != 0:
    raise ValueError('The mascot must have a transparent background.')
mascot.thumbnail((236, 236), Image.Resampling.LANCZOS)
icon = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
icon.alpha_composite(mascot, ((256 - mascot.width) // 2, (256 - mascot.height) // 2))
icon.save(desktop / 'app.ico', format='ICO', sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])
print('Created capybara icon at 16, 24, 32, 48, 64, 128 and 256 pixels.')
