with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

idx = data.find(b"mat_impBody")
# print 128 bytes after _MainTex
p_tex = data.find(b"_MainTex", idx)
print("Bytes after _MainTex:", [hex(b) for b in data[p_tex: p_tex + 40]])
