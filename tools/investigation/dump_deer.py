import struct

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

pos = 0x63722b
for i in range(pos, pos + 300, 4):
    val = struct.unpack('<f', data[i:i+4])[0]
    if -10.0 <= val <= 360.0 and abs(val) > 0.0001:
        print(f"  +{i-pos:3d}: {val}")
