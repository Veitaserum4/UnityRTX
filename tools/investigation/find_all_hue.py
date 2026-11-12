import os, glob

assets_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data"
for fpath in glob.glob(os.path.join(assets_dir, "*.assets")):
    with open(fpath, "rb") as f:
        data = f.read()
    idx = 0
    count = 0
    while True:
        idx = data.find(b"_Hue", idx)
        if idx == -1: break
        # look forward/backward for printable text
        snippet = data[max(0, idx - 500): min(len(data), idx + 100)]
        printable = "".join(chr(b) if 32 <= b <= 126 else " " for b in snippet)
        print(f"[{os.path.basename(fpath)}] offset {idx}:\n  {printable}\n")
        idx += 4
        count += 1
        if count >= 5: break
