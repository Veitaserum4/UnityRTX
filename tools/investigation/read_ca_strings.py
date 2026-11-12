with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

pos = 0x289604
end = 0x296540
import re
strings = re.findall(rb'[A-Za-z0-9_. -]{3,60}', data[pos:end])
print([s.decode('latin1') for s in strings if any(x in s.lower() for x in [b'hue', b'sat', b'bright', b'cont', b'hsv', b'rgb', b'color'])][:50])
