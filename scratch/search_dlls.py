import os, glob

managed_dir = r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed"
for fpath in glob.glob(os.path.join(managed_dir, "*.dll")):
    with open(fpath, "rb") as f:
        data = f.read()
    if b"ColorAdjust" in data:
        print(f"[{os.path.basename(fpath)}] contains ColorAdjust")
