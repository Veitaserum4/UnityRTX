with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

pos = 0x289604
# Look for DXBC in next 50000 bytes
dxbc_pos = data.find(b"DXBC", pos, pos + 50000)
print(f"DXBC found at: {hex(dxbc_pos) if dxbc_pos != -1 else 'Not found'}")
if dxbc_pos != -1:
    # let's see how many DXBC blocks
    curr = pos
    while True:
        d = data.find(b"DXBC", curr, pos + 50000)
        if d == -1: break
        # read length from DXBC header (offset 24 is size)
        size = int.from_bytes(data[d+24:d+28], 'little')
        print(f"  DXBC at {hex(d)}, size: {size}")
        curr = d + 4
