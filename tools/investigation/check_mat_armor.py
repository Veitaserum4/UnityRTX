import struct

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

idx = 0x21274 # mat_armor(cutout)

for prop in [b"_Hue", b"_Saturation", b"_Brightness", b"_Contrast", b"_ColorTint"]:
    pos = data.find(prop, idx, idx + 1000)
    if pos != -1:
        name_len = len(prop)
        aligned = (name_len + 3) & ~3
        val = struct.unpack('<f', data[pos + aligned: pos + aligned + 4])[0]
        print(f"mat_armor(cutout) {prop.decode()} = {val}")
