import math

def apply_hue(r, g, b, hue_deg):
    if abs(hue_deg) < 0.001 or abs(hue_deg - 360.0) < 0.001:
        return r, g, b
    angle = math.radians(hue_deg)
    cosA = math.cos(angle)
    sinA = math.sin(angle)
    # k = (1/sqrt(3), 1/sqrt(3), 1/sqrt(3))
    k = 0.57735026919
    # Rodrigues formula: v*cosA + cross(k, v)*sinA + k*dot(k, v)*(1 - cosA)
    # cross(k, v) = (k*b - k*g, k*r - k*b, k*g - k*r)
    cx = k * (g - b) # wait, cross((k,k,k), (r,g,b)): y*bz - z*by = k*b - k*g
    # cross:
    # x = ky*vz - kz*vy = k*(b - g)
    # y = kz*vx - kx*vz = k*(r - b)
    # z = kx*vy - ky*vx = k*(g - r)
    dotKV = (r + g + b) * k
    omc = 1.0 - cosA
    
    rx = r * cosA + k * (b - g) * sinA + k * dotKV * omc
    ry = g * cosA + k * (r - b) * sinA + k * dotKV * omc
    rz = b * cosA + k * (g - r) * sinA + k * dotKV * omc
    return rx, ry, rz

def apply_hsbc(r, g, b, hue, bright, contrast, sat):
    # 1. Hue
    r, g, b = apply_hue(r, g, b, hue)
    # 2. Contrast: (col - 0.5) * contrast + 0.5
    r = (r - 0.5) * contrast + 0.5
    g = (g - 0.5) * contrast + 0.5
    b = (b - 0.5) * contrast + 0.5
    # 3. Brightness
    r += bright
    g += bright
    b += bright
    # 4. Saturation
    lum = 0.299 * r + 0.587 * g + 0.114 * b
    r = lum + (r - lum) * sat
    g = lum + (g - lum) * sat
    b = lum + (b - lum) * sat
    return max(0.0, min(1.0, r)), max(0.0, min(1.0, g)), max(0.0, min(1.0, b))

# Test identity
print("Identity (0.8, 0.4, 0.2):", apply_hsbc(0.8, 0.4, 0.2, 0.0, 0.0, 1.0, 1.0))
# Test hue shift 120 deg
print("Hue 120 (1, 0, 0):", apply_hsbc(1.0, 0.0, 0.0, 120.0, 0.0, 1.0, 1.0))
# Test hue shift 240 deg
print("Hue 240 (1, 0, 0):", apply_hsbc(1.0, 0.0, 0.0, 240.0, 0.0, 1.0, 1.0))
