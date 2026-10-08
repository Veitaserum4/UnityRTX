import os, glob

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Look for strings containing "Hue" and "Saturation" or HLSL code
import re
for m in re.finditer(rb'(?:Hue|saturation|contrast|brightness)', data, re.IGNORECASE):
    idx = m.start()
    snippet = data[max(0, idx - 100): min(len(data), idx + 200)]
    printable = "".join(chr(b) if 32 <= b <= 126 else " " for b in snippet)
    if "float" in printable.lower() or "half" in printable.lower() or "cbuffer" in printable.lower() or "hsv" in printable.lower():
        print(f"Offset {idx}: {printable}")
