import struct, os, glob

# Search for the float ranges
# Typically hue is 0.0 to 1.0 or 0 to 360
# Brightness is often -1.0 to 1.0 or 0.0 to 2.0 or 0 to 1
# Contrast is often 0.5 to 1.5 or 0 to 2
# Let's search for a pattern of 4 Vector2s: (min, max) * 4
with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data0 = f.read()

# Search in sharedassets0 and resources
for fname, data in [("sharedassets0", data0)]:
    # Find any occurrences of 0.0, 1.0 or similar
    for i in range(0, len(data) - 32, 4):
        # 4 consecutive Vector2s
        vals = struct.unpack('<8f', data[i:i+32])
        # If vals look like: hue(0, 1 or 0, 360), bright(-1..1 or 0..2), cont(0..2), sat(0..2)
        if (vals[0] == 0.0 and (vals[1] == 1.0 or vals[1] == 360.0) and
            -2.0 <= vals[2] <= 0.0 and 0.5 <= vals[3] <= 2.0 and
            0.0 <= vals[4] <= 1.0 and 1.0 <= vals[5] <= 3.0 and
            0.0 <= vals[6] <= 1.0 and 1.0 <= vals[7] <= 3.0):
            print(f"[{fname}] at {i:x}: {vals}")
