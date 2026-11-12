import os, glob

assets_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data"
for fpath in glob.glob(os.path.join(assets_dir, "*.assets")):
    with open(fpath, "rb") as f:
        data = f.read()
    if b"_hueSlider" in data:
        print(f"[{os.path.basename(fpath)}] has _hueSlider")
