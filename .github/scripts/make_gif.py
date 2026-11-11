"""Собирает кадры танцующего Клоди (frame_0000.png …) в зацикленную анимацию GIF."""
import glob
import sys

from PIL import Image

src, out = sys.argv[1], sys.argv[2]
duration = int(sys.argv[3]) if len(sys.argv) > 3 else 50
files = sorted(glob.glob(src + "/frame_*.png"))
if not files:
    sys.exit("нет кадров в " + src)
frames = [Image.open(f).convert("RGB") for f in files]
# общая палитра на все кадры — без мерцания цветов
pal = frames[len(frames) // 2].quantize(colors=64, method=Image.Quantize.MEDIANCUT)
frames = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
frames[0].save(out, save_all=True, append_images=frames[1:], duration=duration, loop=0, optimize=True, disposal=1)
print(out, len(frames), "frames")
