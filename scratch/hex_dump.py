with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

idx = 136132
# print hex and ascii from idx-200 to idx+400
for i in range(idx - 100, idx + 300, 16):
    chunk = data[i:i+16]
    hex_str = " ".join(f"{b:02x}" for b in chunk)
    ascii_str = "".join(chr(b) if 32 <= b <= 126 else "." for b in chunk)
    print(f"{i:08x}: {hex_str:<48} | {ascii_str}")
