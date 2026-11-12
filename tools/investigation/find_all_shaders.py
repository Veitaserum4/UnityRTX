import os, glob, re

assets_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data"
for fpath in glob.glob(os.path.join(assets_dir, "*.assets")):
    with open(fpath, "rb") as f:
        data = f.read()
    for m in re.finditer(rb'Shader\x00([A-Za-z0-9_/ -]{3,60})\x00', data):
        sname = m.group(1).decode('latin1')
        print(f"[{os.path.basename(fpath)}] Shader: {sname}")
