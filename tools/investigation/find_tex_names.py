with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Search for texture name of imp body or player body
import re
tex_names = re.findall(rb'tex_[A-Za-z0-9_]+|texture_[A-Za-z0-9_]+', data)
print(set(tex_names[:40]))
