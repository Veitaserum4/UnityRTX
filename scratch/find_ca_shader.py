with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

pos = 0
while True:
    pos = data.find(b"Unlit/ColorAdjust", pos)
    if pos == -1: break
    print(f"Found Unlit/ColorAdjust at {pos:x}")
    snippet = data[pos: pos + 2000]
    # find any readable text
    lines = snippet.split(b"\x00")
    for l in lines:
        if len(l) > 10 and all(32 <= b <= 126 or b in (10, 13, 9) for b in l):
            print("  TEXT:", l.decode('latin1'))
    pos += 10
