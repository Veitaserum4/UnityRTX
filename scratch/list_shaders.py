import re

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Shaders usually have "m_Name" and shader keywords or SubShader
# Search for strings starting with "Custom/" or "Standard" or "Hidden/"
matches = re.findall(rb'(?:Custom|Hidden|Legacy Shaders|Unlit|Standard|Particles)/[A-Za-z0-9_/ -]+', data)
for m in sorted(set(matches)):
    print(m.decode('latin1'))
