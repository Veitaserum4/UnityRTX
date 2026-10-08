# Can we search for template values in sharedassets or resources?
with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Let's search for "PlayerAppearanceStruct" or floats around Hue
