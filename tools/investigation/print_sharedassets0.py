with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

idx = 131492
snippet = data[max(0, idx - 1500): min(len(data), idx + 200)]
chars = "".join(chr(b) if 32 <= b <= 126 else " " for b in snippet)
print(chars)
