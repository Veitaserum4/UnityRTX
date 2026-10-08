import re

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data0 = f.read()

# Search for Terrain shaders or materials
matches = re.findall(rb'(?:Nature|Terrain|Custom|Hidden)/[A-Za-z0-9_/ -]+', data0)
print(set(m.decode('latin1') for m in matches if 'terrain' in m.decode('latin1').lower()))
