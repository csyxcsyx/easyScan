"""Render the project's simple geometric application symbol to a Windows ICO."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, 255, 255), radius=64, fill="#2c2c2c")
for points in [((64, 100), (64, 64), (100, 64)), ((156, 64), (192, 64), (192, 100)),
               ((192, 156), (192, 192), (156, 192)), ((100, 192), (64, 192), (64, 156))]:
    draw.line(points, fill="white", width=12, joint="curve")
for y, end in [(100, 156), (128, 156), (156, 136)]:
    draw.line((100, y, end, y), fill="white", width=8)
image.save(root / "src/EasyScan.Desktop/Assets/easyScan.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
