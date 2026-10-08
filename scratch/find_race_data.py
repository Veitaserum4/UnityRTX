import struct, os, glob

assets_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data"
for fpath in glob.glob(os.path.join(assets_dir, "*.assets")):
    with open(fpath, "rb") as f:
        data = f.read()
    # Search for race names like "Kitsune" or "Canine" or "Feline" or "Human" or "Bunny"
    for race in [b"Kitsune", b"Canine", b"Feline", b"Rabbit", b"Kobold", b"Drag", b"Deer"]:
        pos = data.find(race)
        if pos != -1:
            print(f"[{os.path.basename(fpath)}] Found {race.decode()} at {pos:x}")
            # dump nearby floats (vector2 ranges)
            for i in range(pos, pos + 400, 4):
                val = struct.unpack('<f', data[i:i+4])[0]
                if -100.0 < val < 100.0 and abs(val) > 0.001:
                    print(f"  float at +{i-pos}: {val}")
            break
