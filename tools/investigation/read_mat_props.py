import struct

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

idx = 0x21274 # mat_armor(cutout)
# In Unity serialized Material:
# m_SavedProperties:
#   m_TexEnvs: array
#   m_Floats: array
#   m_Colors: array

pos = data.find(b"_Hue", idx - 500, idx + 2000)
while pos != -1 and pos < idx + 2000:
    name_len = int.from_bytes(data[pos-4:pos], 'little')
    name = data[pos:pos+name_len].decode('ascii', errors='ignore')
    # float value is right after aligned name
    aligned = (name_len + 3) & ~3
    val = struct.unpack('<f', data[pos + aligned: pos + aligned + 4])[0]
    print(f"Property: {name} = {val}")
    pos = data.find(b"_", pos + aligned + 4, idx + 2000)
