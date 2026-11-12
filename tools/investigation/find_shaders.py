with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll", "rb") as f:
    data = f.read()

import re
matches = set(re.findall(rb'[A-Za-z0-9_/-]{3,40}(?:Shader|shader)', data))
print([m.decode('latin1') for m in matches])
