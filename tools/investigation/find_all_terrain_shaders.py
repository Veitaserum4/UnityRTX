import os, glob, re

assets_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data"
for fpath in glob.glob(os.path.join(assets_dir, "*.assets")):
    with open(fpath, "rb") as f:
        data = f.read()
    matches = set(re.findall(rb'(?:Nature|Terrain|Custom|Hidden|Unlit|Standard)/[A-Za-z0-9_/ -]+', data))
    terrains = [m.decode('latin1') for m in matches if 'terrain' in m.decode('latin1').lower()]
    if terrains:
        print(f"[{os.path.basename(fpath)}]: {terrains}")
