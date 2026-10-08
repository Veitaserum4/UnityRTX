with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

idx = 136132
snippet = data[max(0, idx - 1000): idx]
# Find strings
import re
print(re.findall(rb'[A-Za-z0-9_/ -]{4,40}', snippet))
