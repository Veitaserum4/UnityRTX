import struct

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Let's search for all materials in sharedassets0 that have _Hue
import re
for m in re.finditer(rb'mat_[A-Za-z0-9_()]+', data):
    m_name = m.group(0).decode()
    idx = m.start()
    for prop in [b"_Hue", b"_Brightness", b"_Contrast", b"_Saturation"]:
        p_pos = data.find(prop, idx, idx + 1000)
        if p_pos != -1:
            name_len = len(prop)
            aligned = (name_len + 3) & ~3
            val = struct.unpack('<f', data[p_pos + aligned: p_pos + aligned + 4])[0]
            print(f"{m_name} {prop.decode()} = {val}")
        else:
            break
