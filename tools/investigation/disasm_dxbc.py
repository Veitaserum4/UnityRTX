import ctypes, os

d3dcompiler = ctypes.windll.LoadLibrary("d3dcompiler_47.dll")
D3DDisassemble = d3dcompiler.D3DDisassemble
D3DDisassemble.argtypes = [
    ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint,
    ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)
]
D3DDisassemble.restype = ctypes.c_long

with open(r"C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\sharedassets0.assets", "rb") as f:
    data = f.read()

# Let's search for "DXBC" after 0x289604
pos = 0x289604
while True:
    d = data.find(b"DXBC", pos, 0x296540)
    if d == -1: break
    size = int.from_bytes(data[d+24:d+28], 'little')
    if size < 50000:
        blob = data[d:d+size]
        p_blob = ctypes.create_string_buffer(blob)
        p_disasm = ctypes.c_void_p()
        hr = D3DDisassemble(p_blob, len(blob), 0, None, ctypes.byref(p_disasm))
        if hr == 0:
            # ID3D10Blob: GetBufferPointer, GetBufferSize
            # in COM: vtable[3] is GetBufferPointer, vtable[4] is GetBufferSize
            vtable = ctypes.cast(p_disasm, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            GetBufferPointer = ctypes.WINFUNCTYPE(ctypes.c_char_p, ctypes.c_void_p)(vtable[3])
            text = GetBufferPointer(p_disasm).decode('ascii', errors='ignore')
            print(f"=== DXBC at {hex(d)} (size {size}) ===")
            print("\n".join(text.splitlines()[:50]))
            break
    pos = d + 4
