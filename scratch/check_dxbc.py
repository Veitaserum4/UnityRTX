with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

pos = 0x2897a3
chunk = data[pos: pos + 32]
print([hex(b) for b in chunk])
size = int.from_bytes(chunk[24:28], 'little')
print("Size:", size)
