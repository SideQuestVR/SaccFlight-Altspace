"""Contact sheets of the seat pictures from FlightWorld/Flight/Test/Seat Leg Pictures.

    python Tools/seat_sheet.py [cut|out]   ->  Logs/seat_legs/sheet_<kind>_<n>.jpg (20 seats a sheet)
"""
import glob
import os
import sys

from PIL import Image, ImageDraw

kind = sys.argv[1] if len(sys.argv) > 1 else "cut"
files = sorted(glob.glob(f"Logs/seat_legs/*_{kind}.jpg"))
cols, rows, w, h = 5, 4, 384, 288
per = cols * rows
for sheet in range((len(files) + per - 1) // per):
    canvas = Image.new("RGB", (cols * w, rows * (h + 22)), "white")
    draw = ImageDraw.Draw(canvas)
    for i, f in enumerate(files[sheet * per:(sheet + 1) * per]):
        x, y = (i % cols) * w, (i // cols) * (h + 22)
        canvas.paste(Image.open(f).resize((w, h)), (x, y + 22))
        label = os.path.basename(f)[: -len(f"_{kind}.jpg")].replace("__", " / ")
        draw.text((x + 4, y + 4), label, fill="black")
    out = f"Logs/seat_legs/sheet_{kind}_{sheet + 1}.jpg"
    canvas.save(out, quality=85)
    print(out)
