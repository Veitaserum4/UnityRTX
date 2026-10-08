import struct

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Let's check mat_impBody texture name
idx = data.find(b"mat_impBody")
if idx != -1:
    snippet = data[idx: idx + 600]
    import re
    print("mat_impBody strings:", re.findall(rb'[A-Za-z0-9_()]{3,40}', snippet))
