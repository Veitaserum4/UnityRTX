import os, glob

assets_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data"
for fpath in glob.glob(os.path.join(assets_dir, "*.assets")):
    with open(fpath, "rb") as f:
        data = f.read()
    idx = 0
    while True:
        idx = data.find(b"_Hue", idx)
        if idx == -1:
            break
        snippet = data[max(0, idx - 150): min(len(data), idx + 200)]
        # find printable ascii strings
        chars = "".join(chr(b) if 32 <= b <= 126 else " " for b in snippet)
        print(f"[{os.path.basename(fpath)}] at {idx}: {chars}")
        idx += 4
        if idx > 0: break # just first match per file
