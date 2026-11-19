using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    // Texture uploads: Unity textures as they are, tinted, masked, particle (brightness as coverage) and SDF variants.
    public partial class RemixMaterialManager
    {
        /// <summary>
        /// Get or create a 1x1 solid-color texture for materials that only use _Color with no _MainTex.
        /// Defers the actual upload to the render thread like regular textures.
        /// Returns the provisional handle (hash as IntPtr).
        /// </summary>
        private IntPtr GetOrCreateSolidColorTexture(Color color)
        {
            byte r = (byte)(Mathf.Clamp01(color.r) * 255f);
            byte g = (byte)(Mathf.Clamp01(color.g) * 255f);
            byte b = (byte)(Mathf.Clamp01(color.b) * 255f);
            byte a = (byte)(Mathf.Clamp01(color.a) * 255f);
            uint colorKey = (uint)(r | (g << 8) | (b << 16) | (a << 24));
            
            lock (pendingTextureLock)
            {
                if (solidColorTextureCache.TryGetValue(colorKey, out IntPtr cached))
                    return cached;
            }
            
            byte[] pixels = new byte[] { r, g, b, a };
            ulong hash = XXHash64.ComputeHash(pixels, 0, 4);
            // Mix in a sentinel so solid-color hashes don't collide with real texture hashes
            hash ^= 0x50C0_10C0_10C0_10C0UL;
            if (hash == 0) hash = 1;
            
            lock (pendingTextureLock)
            {
                pendingTextureUploads.Enqueue(new PendingTextureUpload
                {
                    texId = -1,
                    tintedCacheKey = 0,
                    pixelData = pixels,
                    hash = hash,
                    width = 1,
                    height = 1,
                    mipLevels = 1,
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                });
                
                var handle = new IntPtr((long)hash);
                solidColorTextureCache[colorKey] = handle;
                return handle;
            }
        }
        
        /// <summary>
        /// Prepare a Unity texture for Remix upload. Pixel data is captured on the calling thread,
        /// but the actual Remix API call is deferred to the render thread via ProcessPendingTextureUploads().
        /// Returns a provisional handle (the texture hash cast to IntPtr).
        /// </summary>
        /// <param name="linearData">The texture holds data (metallic, smoothness): GPU readbacks
        /// keep its values instead of encoding them as sRGB.</param>
        public IntPtr UploadUnityTexture(Texture2D unityTexture, bool isNormalMap = false, bool linearData = false)
        {
            if (unityTexture == null || createTextureFunc == null)
                return IntPtr.Zero;
                
            int texId = unityTexture.GetInstanceID();
            
            // Check cache and pending queue (synchronized with render thread)
            lock (pendingTextureLock)
            {
                if (textureCache.TryGetValue(texId, out IntPtr cachedHandle))
                    return cachedHandle;
                if (pendingTextureIds.Contains(texId))
                {
                    textureHashCache.TryGetValue(texId, out ulong ph);
                    return ph != 0 ? new IntPtr((long)ph) : IntPtr.Zero;
                }
            }
            
            try
            {
                // Get raw texture data
                byte[] pixelData;
                byte[] hashSourceData;
                RemixAPI.remixapi_Format format;
                uint actualMipLevels = (uint)UnityCompat.MipmapCount(unityTexture);
                
                // DXT5nm normal maps pack X in alpha and Y in green. Raw DXT5 upload
                // would pass the packed channels to Remix unchanged, so we must decompress
                // and unswizzle. Readable textures use GetPixels32 (CPU); non-readable use GPU readback.
                bool isDXT5nm = isNormalMap &&
                    (unityTexture.format == TextureFormat.DXT5 || unityTexture.format == TextureFormat.DXT5Crunched);
                
                // Readable DXT5nm: decompress on CPU to avoid GPU stall
                if (isDXT5nm && UnityCompat.IsReadable(unityTexture))
                {
                    Color32[] pixels = unityTexture.GetPixels32();
                    pixelData = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        byte x = pixels[i].a; // alpha = X
                        byte y = pixels[i].g; // green = Y
                        pixelData[i * 4 + 0] = x;
                        pixelData[i * 4 + 1] = y;
                        pixelData[i * 4 + 2] = zLookup[x * 256 + y];
                        pixelData[i * 4 + 3] = 255;
                    }
                    hashSourceData = pixelData;
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"Unpacked readable DXT5nm normal map '{unityTexture.name}' ({unityTexture.width}x{unityTexture.height})");
                }
                // Handle non-readable textures via GPU readback
                else if (!UnityCompat.IsReadable(unityTexture))
                {
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"Texture '{unityTexture.name}' is not readable - forcing GPU readback");
                    
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        unityTexture.width, unityTexture.height, 0,
                        RenderTextureFormat.ARGB32, isNormalMap || linearData ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                    
                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(unityTexture, tmp);
                    RenderTexture.active = tmp;
                    
                    Texture2D readableTexture = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, isNormalMap || linearData);
                    readableTexture.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readableTexture.Apply();
                    
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(tmp);
                    
                    pixelData = readableTexture.GetRawTextureData();
                    
                    // DXT5nm: X stored in alpha, Y stored in green.
                    // Reconstruct standard tangent-space normal map: R=X, G=Y, B=Z, A=255.
                    if (isDXT5nm)
                    {
                        for (int i = 0; i < pixelData.Length; i += 4)
                        {
                            byte x = pixelData[i + 3]; // alpha = X
                            byte y = pixelData[i + 1]; // green = Y
                            pixelData[i + 0] = x;      // R = X
                            pixelData[i + 1] = y;      // G = Y (unchanged)
                            pixelData[i + 2] = zLookup[x * 256 + y]; // B = Z
                            pixelData[i + 3] = 255;
                        }
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"Unpacked DXT5nm normal map '{unityTexture.name}' ({unityTexture.width}x{unityTexture.height})");
                    }
                    
                    hashSourceData = pixelData;
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                    
                    actualMipLevels = 1; // GPU readback only gives top mip
                    UnityEngine.Object.Destroy(readableTexture);
                }
                else if (unityTexture.format == TextureFormat.RGB24)
                {
                    // Convert RGB24 to RGBA32
                    Color32[] pixels = unityTexture.GetPixels32();
                    pixelData = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixelData[i * 4 + 0] = pixels[i].r;
                        pixelData[i * 4 + 1] = pixels[i].g;
                        pixelData[i * 4 + 2] = pixels[i].b;
                        pixelData[i * 4 + 3] = 255;
                    }
                    hashSourceData = pixelData;
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                }
                else
                {
                    // Use raw data for supported formats
                    pixelData = unityTexture.GetRawTextureData();
                    hashSourceData = pixelData;
                    
                    switch (unityTexture.format)
                    {
                        case TextureFormat.RGBA32:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                            break;
                        case TextureFormat.BGRA32:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_UNORM;
                            break;
                        case TextureFormat.DXT1:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC1_RGB_UNORM;
                            break;
                        case TextureFormat.DXT5:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM;
                            break;
                        case TextureFormat.BC7:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC7_UNORM;
                            break;
                        case TextureFormat.BC5:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC5_UNORM;
                            break;
                        default:
                            // Fallback to GPU readback to blit any unsupported format into standard RGBA32
                            if (verboseTextureLogging.Value)
                                logger.LogInfo($"Format {unityTexture.format} for '{unityTexture.name}' not directly supported, using GPU readback fallback");
                            
                            RenderTexture tmpGpu = RenderTexture.GetTemporary(
                                unityTexture.width, unityTexture.height, 0,
                                RenderTextureFormat.ARGB32, isNormalMap || linearData ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                            RenderTexture prevActive = RenderTexture.active;
                            Graphics.Blit(unityTexture, tmpGpu);
                            RenderTexture.active = tmpGpu;
                            Texture2D convTex = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, isNormalMap || linearData);
                            convTex.ReadPixels(new Rect(0, 0, tmpGpu.width, tmpGpu.height), 0, 0);
                            convTex.Apply();
                            RenderTexture.active = prevActive;
                            RenderTexture.ReleaseTemporary(tmpGpu);
                            pixelData = convTex.GetRawTextureData();
                            hashSourceData = pixelData;
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                            actualMipLevels = 1;
                            UnityEngine.Object.Destroy(convTex);
                            break;
                    }
                }
                
                // Check alpha channel content for uncompressed RGBA formats
                bool hasAlpha = false;
                if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM)
                {
                    hasAlpha = SampleAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                }
                else if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_UNORM)
                {
                    hasAlpha = SampleAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                }
                else if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM)
                {
                    hasAlpha = SampleAlphaBC3(pixelData);
                }
                // BC1 (DXT1) has only 1-bit punch-through alpha, treat as opaque
                
                if (hasAlpha)
                {
                    texturesWithAlpha.Add(texId);
                    
                    // Check for genuine cutout transparency: large regions of near-zero alpha.
                    // This distinguishes real transparency (decal backgrounds at alpha=0) from
                    // smoothness-as-alpha packing (Standard shader Opaque mode, values ~50-230).
                    bool hasCutoutAlpha;
                    if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM)
                        hasCutoutAlpha = SampleCutoutAlphaBC3(pixelData);
                    else
                        hasCutoutAlpha = SampleCutoutAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                    if (hasCutoutAlpha)
                        texturesWithCutoutAlpha.Add(texId);
                }
                
                // Compute XXH64 hash
                ulong textureHash = XXHash64.ComputeHash(hashSourceData, 0, hashSourceData.Length);
                if (textureHash == 0) textureHash = 1;
                
                textureHashCache[texId] = textureHash;
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed XXH64 hash for '{unityTexture.name}': 0x{textureHash:X16} hasAlpha={hasAlpha} fmt={unityTexture.format}");
                
                // Queue for deferred upload on the render thread
                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = texId,
                        tintedCacheKey = 0,
                        pixelData = pixelData,
                        hash = textureHash,
                        width = (uint)unityTexture.width,
                        height = (uint)unityTexture.height,
                        mipLevels = actualMipLevels,
                        format = format
                    });
                    pendingTextureIds.Add(texId);
                }
                
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Queued texture '{unityTexture.name}' for render thread upload (hash: 0x{textureHash:X16})");
                
                return new IntPtr((long)textureHash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading texture '{unityTexture.name}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Upload an emissive texture. OpenRemix multiplies it by emissiveColorConstant *
        /// emissiveIntensity on the GPU (scene.glsl), so it is not pre-tinted.
        /// Returns (handle, hash) for the uploaded texture.
        /// </summary>
        private (IntPtr handle, ulong hash) UploadTintedEmissiveTexture(Texture2D tex, Color emissiveColor)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);
            var handle = UploadUnityTexture(tex);
            textureHashCache.TryGetValue(tex.GetInstanceID(), out ulong hash);
            return (handle, hash);
        }

        /// <summary>
        /// Upload a particle/trail sprite texture with automatic black-background alpha extraction
        /// and premultiplied/dark-matte edge de-fringing so transparent particles never render with black outlines.
        /// Cached per Texture2D in OpenRemix (where tint is applied in scene.glsl).
        /// </summary>
        private (IntPtr handle, ulong hash) UploadParticleTexture(Texture2D tex, Material mat, Color albedoColor,
            float coverageScale, ParticleBlendKind kind, bool gammaCoverage = false)
        {
            // coverageScale: the texels' alpha scale (a faint additive glow covers little).
            int coverageKey = Mathf.Clamp(Mathf.RoundToInt(coverageScale * 255f), 1, 255);
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);

            bool isAdditiveShader = kind == ParticleBlendKind.Additive;

            long cacheKey = unchecked((long)0x5000000000000000L ^ ((long)tex.GetInstanceID() << 28) ^ ((long)kind << 24) ^ ((long)(255 - coverageKey) << 40) ^ (gammaCoverage ? 1L << 52 : 0L));

            lock (pendingTextureLock)
            {
                if (tintedTextureCache.TryGetValue(cacheKey, out var cached))
                    return cached;
                if (pendingTintedHashes.TryGetValue(cacheKey, out ulong pHash))
                    return (new IntPtr((long)pHash), pHash);
            }

            try
            {
                Color32[] pixels;
                if (UnityCompat.IsReadable(tex))
                {
                    pixels = tex.GetPixels32();
                }
                else
                {
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(tex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
                    readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    UnityEngine.Object.Destroy(readable);
                }

                int totalPixels = pixels.Length;
                int opaqueAlphaCount = 0;
                long sumR = 0, sumG = 0, sumB = 0;
                long weightSum = 0;

                for (int i = 0; i < totalPixels; i++)
                {
                    Color32 c = pixels[i];
                    if (c.a >= 250) opaqueAlphaCount++;
                    int maxCh = Math.Max(c.r, Math.Max(c.g, c.b));
                    if (c.a > 16 && maxCh > 16)
                    {
                        long wA = c.a;
                        sumR += c.r * wA;
                        sumG += c.g * wA;
                        sumB += c.b * wA;
                        weightSum += wA;
                    }
                }

                // Check border pixels to see if an alpha-less texture has a black background (additive sprite)
                int w = tex.width;
                int h = tex.height;
                int darkBorderCount = 0;
                int borderSamples = 0;
                if (w > 2 && h > 2)
                {
                    int stepX = Math.Max(1, w / 16);
                    int stepY = Math.Max(1, h / 16);
                    for (int x = 0; x < w; x += stepX)
                    {
                        Color32 cTop = pixels[x];
                        Color32 cBot = pixels[(h - 1) * w + x];
                        if (Math.Max(cTop.r, Math.Max(cTop.g, cTop.b)) <= 20) darkBorderCount++;
                        if (Math.Max(cBot.r, Math.Max(cBot.g, cBot.b)) <= 20) darkBorderCount++;
                        borderSamples += 2;
                    }
                    for (int y = 0; y < h; y += stepY)
                    {
                        Color32 cLeft = pixels[y * w];
                        Color32 cRight = pixels[y * w + (w - 1)];
                        if (Math.Max(cLeft.r, Math.Max(cLeft.g, cLeft.b)) <= 20) darkBorderCount++;
                        if (Math.Max(cRight.r, Math.Max(cRight.g, cRight.b)) <= 20) darkBorderCount++;
                        borderSamples += 2;
                    }
                }

                bool hasNoAlphaChannel = opaqueAlphaCount >= (int)(totalPixels * 0.92f);
                bool hasDarkBorder = borderSamples > 0 && darkBorderCount >= (int)(borderSamples * 0.65f);
                bool multiply = kind == ParticleBlendKind.Multiply || kind == ParticleBlendKind.MultiplyDouble;
                bool treatAsBlackBgAdditive = isAdditiveShader || (kind == ParticleBlendKind.Alpha && hasNoAlphaChannel && hasDarkBorder);

                byte avgR = weightSum > 0 ? (byte)Mathf.Clamp((int)(sumR / weightSum), 16, 255) : (byte)180;
                byte avgG = weightSum > 0 ? (byte)Mathf.Clamp((int)(sumG / weightSum), 16, 255) : (byte)180;
                byte avgB = weightSum > 0 ? (byte)Mathf.Clamp((int)(sumB / weightSum), 16, 255) : (byte)180;

                byte[] pixelData = new byte[totalPixels * 4];
                for (int i = 0; i < totalPixels; i++)
                {
                    Color32 c = pixels[i];
                    byte r = c.r;
                    byte g = c.g;
                    byte b = c.b;
                    byte a = c.a;
                    int maxCh = Math.Max(r, Math.Max(g, b));

                    if (multiply)
                    {
                        // A multiply darkens what is behind by its colour (lerped to no change by its
                        // alpha; the double one's no change is grey): shown as black, covering by
                        // how much it darkens. (Where a double multiply brightens it shows nothing.)
                        int neutral = kind == ParticleBlendKind.MultiplyDouble ? 128 : 255;
                        int cMax = neutral + (maxCh - neutral) * a / 255;
                        int dark = kind == ParticleBlendKind.MultiplyDouble ? 255 - 2 * cMax : 255 - cMax;
                        r = g = b = 0;
                        a = (byte)Mathf.Clamp(dark, 0, 255);
                    }
                    else if (kind == ParticleBlendKind.Premultiplied)
                    {
                        // Premultiplied colour over: covers by alpha, its colour rgb / alpha; colour
                        // beyond its alpha adds light (covering by it instead).
                        int cover = Math.Max(a, maxCh);
                        if (cover <= 2)
                        {
                            r = avgR; g = avgG; b = avgB; a = 0;
                        }
                        else
                        {
                            r = (byte)Math.Min(255, r * 255 / cover);
                            g = (byte)Math.Min(255, g * 255 / cover);
                            b = (byte)Math.Min(255, b * 255 / cover);
                            a = (byte)cover;
                        }
                    }
                    else if (treatAsBlackBgAdditive)
                    {
                        // Convert black-background luminance into alpha and moderately un-premultiply RGB
                        // so soft edges are translucent rather than dark black outlines
                        int effAlpha = (maxCh * a) / 255;
                        if (effAlpha <= 6)
                        {
                            r = avgR; g = avgG; b = avgB; a = 0;
                        }
                        else
                        {
                            a = (byte)Math.Min(255, effAlpha);
                            if (maxCh > 0 && maxCh < 255)
                            {
                                float scale = Math.Min(255f / maxCh, 2.5f);
                                r = (byte)Mathf.Clamp(Mathf.RoundToInt(r * scale), 0, 255);
                                g = (byte)Mathf.Clamp(Mathf.RoundToInt(g * scale), 0, 255);
                                b = (byte)Mathf.Clamp(Mathf.RoundToInt(b * scale), 0, 255);
                            }
                        }
                    }
                    else
                    {
                        // Standard alpha-channel particle sprite: invisible texels take the sprite's
                        // average colour so bilinear filtering doesn't draw dark outlines; visible ones
                        // keep their authored colour and alpha (dark smoke stays dark, faint stays faint).
                        if (a <= 2)
                        {
                            r = avgR;
                            g = avgG;
                            b = avgB;
                            a = 0;
                        }
                    }

                    pixelData[i * 4 + 0] = r;
                    pixelData[i * 4 + 1] = g;
                    pixelData[i * 4 + 2] = b;
                    if (gammaCoverage && a > 0 && a < 255)
                        a = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * Mathf.Pow(a / 255f, 2.2f)), 0, 255);
                    pixelData[i * 4 + 3] = coverageKey == 255 ? a : (byte)(a * coverageKey / 255);
                }

                texturesWithAlpha.Add(tex.GetInstanceID());
                texturesWithCutoutAlpha.Add(tex.GetInstanceID());

                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;
                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = cacheKey,
                        pixelData = pixelData,
                        hash = hash,
                        width = (uint)tex.width,
                        height = (uint)tex.height,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                    pendingTintedKeys.Add(cacheKey);
                    pendingTintedHashes[cacheKey] = hash;
                }

                return (new IntPtr((long)hash), hash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading particle texture '{tex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }

        /// <summary>
        /// Upload an albedo texture pre-tinted by the material color and optionally remapped.
        /// RTX Remix's shader ignores albedoConstant when a texture is present,
        /// so we bake the color multiplication (and greyscale remapping) into the texture pixels.
        /// Returns (handle, hash) for the uploaded texture.
        /// </summary>
        // A texture's top-level pixels (a GPU copy when it isn't readable).
        private static Color32[] ReadPixels32(Texture2D tex, bool linear)
        {
            if (UnityCompat.IsReadable(tex))
                return tex.GetPixels32();
            var tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, tmp);
            RenderTexture.active = tmp;
            var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, linear);
            readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            var pixels = readable.GetPixels32();
            UnityEngine.Object.Destroy(readable);
            return pixels;
        }

        /// <summary>
        /// The albedo recoloured by a ColorMask (its formula above), at the mask's resolution
        /// when the base is the mask's alpha, else the albedo's (mask sampled nearest). Also,
        /// with metalFromColorAlpha, a linear PBR map: red the metal amount, alpha smoothness
        /// (the material's roughness where no metal, metalRoughness where full metal); its
        /// handle zero when no texel is metal.
        /// </summary>
        private (IntPtr handle, ulong hash, IntPtr metalHandle) UploadMaskTintedAlbedoTexture(Texture2D tex, ColorMask colorMask, float baseRoughness = 0.7f)
        {
            if (colorMask.mask == null || createTextureFunc == null || (tex == null && !colorMask.baseInMaskAlpha))
                return (IntPtr.Zero, 0, IntPtr.Zero);
            long cacheKey = unchecked((long)0x4200000000000000L ^ ((long)(tex != null ? tex.GetInstanceID() : 0) << 28) ^ (uint)colorMask.Hash());
            long metalKey = cacheKey ^ 0x0100000000000000L;
            lock (pendingTextureLock)
            {
                bool haveAlbedo = tintedTextureCache.TryGetValue(cacheKey, out var cached);
                if (!haveAlbedo && pendingTintedHashes.TryGetValue(cacheKey, out ulong pHash))
                {
                    cached = (new IntPtr((long)pHash), pHash);
                    haveAlbedo = true;
                }
                if (haveAlbedo)
                {
                    IntPtr metal = IntPtr.Zero;
                    if (tintedTextureCache.TryGetValue(metalKey, out var m)) metal = m.Item1;
                    else if (pendingTintedHashes.TryGetValue(metalKey, out ulong mHash)) metal = new IntPtr((long)mHash);
                    return (cached.Item1, cached.Item2, metal);
                }
            }

            try
            {
                bool linearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
                var maskTex = colorMask.mask;
                // The mask as the shader samples it (sRGB-decoded when its texture is sRGB).
                var mask = ReadPixels32(maskTex, linear: true);
                int mw = maskTex.width, mh = maskTex.height;
                Color32[] basePixels = colorMask.baseInMaskAlpha ? null : ReadPixels32(tex, linear: true);
                int w = colorMask.baseInMaskAlpha ? mw : tex.width, h = colorMask.baseInMaskAlpha ? mh : tex.height;
                Color c1 = colorMask.color1, c2 = colorMask.color2, c3 = colorMask.color3;
                if (linearSpace)
                {
                    c1 = new Color(c1.linear.r, c1.linear.g, c1.linear.b, c1.a);
                    c2 = new Color(c2.linear.r, c2.linear.g, c2.linear.b, c2.a);
                    c3 = new Color(c3.linear.r, c3.linear.g, c3.linear.b, c3.a);
                }
                float k = colorMask.weight;
                float smoothBase = 1f - Mathf.Clamp01(baseRoughness), smoothMetal = 1f - Mathf.Clamp01(colorMask.metalRoughness);
                byte[] pixelData = new byte[w * h * 4];
                byte[] metalData = colorMask.metalFromColorAlpha ? new byte[w * h * 4] : null;
                bool anyMetal = false;
                for (int y = 0; y < h; y++)
                {
                    int my = Mathf.Min(mh - 1, (int)((y + 0.5f) * mh / h));
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        var m = mask[my * mw + Mathf.Min(mw - 1, (int)((x + 0.5f) * mw / w))];
                        float mr = m.r / 255f, mg = m.g / 255f, mb = m.b / 255f;
                        // The base, in the shader's working space.
                        float br, bg, bb;
                        if (colorMask.baseInMaskAlpha)
                        {
                            br = bg = bb = m.a / 255f;
                        }
                        else
                        {
                            br = basePixels[i].r / 255f; bg = basePixels[i].g / 255f; bb = basePixels[i].b / 255f;
                        }
                        if (!linearSpace && colorMask.baseInMaskAlpha == false)
                        {
                            // read linear: back to the gamma values a gamma-space shader works in
                            br = Mathf.LinearToGammaSpace(br); bg = Mathf.LinearToGammaSpace(bg); bb = Mathf.LinearToGammaSpace(bb);
                        }
                        float r = br, g = bg, b = bb;
                        r = Mathf.Lerp(r, br * c1.r, k * mr); g = Mathf.Lerp(g, bg * c1.g, k * mr); b = Mathf.Lerp(b, bb * c1.b, k * mr);
                        r = Mathf.Lerp(r, br * c2.r, k * mg); g = Mathf.Lerp(g, bg * c2.g, k * mg); b = Mathf.Lerp(b, bb * c2.b, k * mg);
                        r = Mathf.Lerp(r, br * c3.r, k * mb); g = Mathf.Lerp(g, bg * c3.g, k * mb); b = Mathf.Lerp(b, bb * c3.b, k * mb);
                        // Stored as sRGB bytes (albedo textures are read as sRGB).
                        if (linearSpace)
                        {
                            r = Mathf.LinearToGammaSpace(Mathf.Clamp01(r)); g = Mathf.LinearToGammaSpace(Mathf.Clamp01(g)); b = Mathf.LinearToGammaSpace(Mathf.Clamp01(b));
                        }
                        pixelData[i * 4 + 0] = (byte)(Mathf.Clamp01(r) * 255f);
                        pixelData[i * 4 + 1] = (byte)(Mathf.Clamp01(g) * 255f);
                        pixelData[i * 4 + 2] = (byte)(Mathf.Clamp01(b) * 255f);
                        pixelData[i * 4 + 3] = colorMask.baseInMaskAlpha ? (byte)255 : basePixels[i].a;
                        if (metalData != null)
                        {
                            float metal = 1f - (1f - mr * c1.a) * (1f - mg * c2.a) * (1f - mb * c3.a);
                            if (metal > 0.004f) anyMetal = true;
                            metalData[i * 4 + 0] = (byte)(Mathf.Clamp01(metal) * 255f);
                            metalData[i * 4 + 3] = (byte)(Mathf.Lerp(smoothBase, smoothMetal, Mathf.Clamp01(metal)) * 255f);
                        }
                    }
                }

                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;
                ulong metalHash = 0;
                if (anyMetal)
                {
                    metalHash = XXHash64.ComputeHash(metalData, 0, metalData.Length);
                    if (metalHash == 0) metalHash = 1;
                }
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed colour-masked albedo (mask '{maskTex.name}', base {(colorMask.baseInMaskAlpha ? "mask alpha" : tex.name)}): 0x{hash:X16}, metal {(anyMetal ? $"0x{metalHash:X16}" : "none")}");

                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = cacheKey,
                        pixelData = pixelData,
                        hash = hash,
                        width = (uint)w,
                        height = (uint)h,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                    pendingTintedKeys.Add(cacheKey);
                    pendingTintedHashes[cacheKey] = hash;
                    if (anyMetal)
                    {
                        pendingTextureUploads.Enqueue(new PendingTextureUpload
                        {
                            texId = -1,
                            tintedCacheKey = metalKey,
                            pixelData = metalData,
                            hash = metalHash,
                            width = (uint)w,
                            height = (uint)h,
                            mipLevels = 1,
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                        });
                        pendingTintedKeys.Add(metalKey);
                        pendingTintedHashes[metalKey] = metalHash;
                    }
                }
                return (new IntPtr((long)hash), hash, anyMetal ? new IntPtr((long)metalHash) : IntPtr.Zero);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading colour-masked albedo '{(tex != null ? tex.name : colorMask.mask.name)}': {ex.Message}");
                return (IntPtr.Zero, 0, IntPtr.Zero);
            }
        }

        // Overlay blend with the colour as the blend layer (GameProfile.GetOverlayTint).
        private static float Overlay(float texel, float colour) =>
            colour > 0.5f ? 1f - (2f - 2f * colour) * (1f - texel) : 2f * texel * colour;

        private (IntPtr handle, ulong hash) UploadTintedAlbedoTexture(Texture2D tex, Color albedoColor, Vector2? remap, Vector4? hsbc = null,
                                                                       Color? overlay = null)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);

            // If color is white, no remap, and no hsbc, fast path to standard upload
            if (albedoColor.r >= 0.98f && albedoColor.g >= 0.98f && albedoColor.b >= 0.98f && !remap.HasValue && !hsbc.HasValue && !overlay.HasValue)
            {
                var handle = UploadUnityTexture(tex);
                int texId = tex.GetInstanceID();
                textureHashCache.TryGetValue(texId, out ulong h);
                return (handle, h);
            }

            // Convert linear color to gamma if running in Linear color space so multiplying with sRGB pixel bytes is correct
            Color gammaColor = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? albedoColor.gamma : albedoColor;
            float tintR = Mathf.Clamp01(gammaColor.r);
            float tintG = Mathf.Clamp01(gammaColor.g);
            float tintB = Mathf.Clamp01(gammaColor.b);

            int tintKey = ((int)(tintR * 255f) << 16) | ((int)(tintG * 255f) << 8) | (int)(tintB * 255f);
            int remapKey = remap.HasValue ? (((int)(remap.Value.x * 100f) << 8) | (int)(remap.Value.y * 100f)) : 0;
            int hsbcKey = 0;
            if (hsbc.HasValue)
            {
                int hInt = Mathf.RoundToInt(hsbc.Value.x * 10f) & 0xFFFF;
                int bInt = (Mathf.RoundToInt(hsbc.Value.y * 100f) + 128) & 0xFF;
                int cInt = Mathf.RoundToInt(hsbc.Value.z * 100f) & 0xFF;
                int sInt = Mathf.RoundToInt(hsbc.Value.w * 100f) & 0xFF;
                hsbcKey = (hInt << 16) | (bInt << 8) | (cInt ^ sInt);
            }
            long cacheKey = unchecked((long)0x4100000000000000L ^ ((long)tex.GetInstanceID() << 28) ^ ((long)remapKey << 20) ^ (uint)tintKey ^ ((long)hsbcKey << 32));
            if (overlay.HasValue)
            {
                Color32 o = overlay.Value;
                cacheKey = unchecked(cacheKey * 1099511628211L ^ ((o.r << 16) | (o.g << 8) | o.b) ^ 0x0F000000L);
            }

            lock (pendingTextureLock)
            {
                if (tintedTextureCache.TryGetValue(cacheKey, out var cached))
                    return cached;
                if (pendingTintedHashes.TryGetValue(cacheKey, out ulong pHash))
                    return (new IntPtr((long)pHash), pHash);
            }

            try
            {
                Color32[] pixels;
                if (UnityCompat.IsReadable(tex))
                {
                    pixels = tex.GetPixels32();
                }
                else
                {
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(tex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
                    readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    UnityEngine.Object.Destroy(readable);
                }

                byte tR = (byte)(tintR * 255f);
                byte tG = (byte)(tintG * 255f);
                byte tB = (byte)(tintB * 255f);
                bool hasTint = (tintR < 0.98f || tintG < 0.98f || tintB < 0.98f);
                bool hasRemap = remap.HasValue;
                bool hasHsbc = hsbc.HasValue;

                float hsbcH = hasHsbc ? hsbc.Value.x : 0f;
                float hsbcBr = hasHsbc ? hsbc.Value.y : 0f;
                float hsbcC = hasHsbc ? hsbc.Value.z : 1f;
                float hsbcS = hasHsbc ? hsbc.Value.w : 1f;

                bool doHue = hasHsbc && Mathf.Abs(hsbcH) > 0.001f;
                float cosA = 1f, sinA = 0f, omc = 0f;
                if (doHue)
                {
                    float angleDeg = Mathf.Abs(hsbcH) <= 1.0f ? hsbcH * 360f : hsbcH;
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    cosA = Mathf.Cos(angleRad);
                    sinA = Mathf.Sin(angleRad);
                    omc = 1.0f - cosA;
                }
                const float k = 0.577350269f;
                bool doContrast = hasHsbc && Mathf.Abs(hsbcC - 1f) > 0.001f;
                bool doBrightness = hasHsbc && Mathf.Abs(hsbcBr) > 0.001f;
                bool doSaturation = hasHsbc && Mathf.Abs(hsbcS - 1f) > 0.001f;

                float rMin = remap.HasValue ? remap.Value.x : 0f;
                float rMax = remap.HasValue ? remap.Value.y : 1f;

                byte[] pixelData = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    float r = pixels[i].r / 255f;
                    float g = pixels[i].g / 255f;
                    float b = pixels[i].b / 255f;

                    if (hasRemap)
                    {
                        r = Mathf.Clamp01(Mathf.Lerp(rMin, rMax, r));
                        g = Mathf.Clamp01(Mathf.Lerp(rMin, rMax, g));
                        b = Mathf.Clamp01(Mathf.Lerp(rMin, rMax, b));
                    }

                    if (doHue)
                    {
                        float dotKV = (r + g + b) * k;
                        float nr = r * cosA + k * (b - g) * sinA + k * dotKV * omc;
                        float ng = g * cosA + k * (r - b) * sinA + k * dotKV * omc;
                        float nb = b * cosA + k * (g - r) * sinA + k * dotKV * omc;
                        r = nr;
                        g = ng;
                        b = nb;
                    }

                    if (doContrast)
                    {
                        r = (r - 0.5f) * hsbcC + 0.5f;
                        g = (g - 0.5f) * hsbcC + 0.5f;
                        b = (b - 0.5f) * hsbcC + 0.5f;
                    }

                    if (doBrightness)
                    {
                        r += hsbcBr;
                        g += hsbcBr;
                        b += hsbcBr;
                    }

                    if (doSaturation)
                    {
                        float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                        r = lum + (r - lum) * hsbcS;
                        g = lum + (g - lum) * hsbcS;
                        b = lum + (b - lum) * hsbcS;
                    }

                    if (hasTint)
                    {
                        r *= tintR;
                        g *= tintG;
                        b *= tintB;
                    }

                    if (overlay.HasValue)
                    {
                        r = Overlay(r, overlay.Value.r);
                        g = Overlay(g, overlay.Value.g);
                        b = Overlay(b, overlay.Value.b);
                    }

                    pixelData[i * 4 + 0] = (byte)(Mathf.Clamp01(r) * 255f);
                    pixelData[i * 4 + 1] = (byte)(Mathf.Clamp01(g) * 255f);
                    pixelData[i * 4 + 2] = (byte)(Mathf.Clamp01(b) * 255f);
                    pixelData[i * 4 + 3] = pixels[i].a;
                }

                // Check alpha channel for cutout transparency
                bool hasCutoutAlpha = SampleCutoutAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                if (hasCutoutAlpha)
                {
                    texturesWithCutoutAlpha.Add(tex.GetInstanceID());
                }

                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;

                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed tinted albedo hash for '{tex.name}' tint=({tintR:F2},{tintG:F2},{tintB:F2}) remap={(remap.HasValue ? remap.Value.ToString() : "none")} hsbc={(hsbc.HasValue ? hsbc.Value.ToString() : "none")}: 0x{hash:X16}");

                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = cacheKey,
                        pixelData = pixelData,
                        hash = hash,
                        width = (uint)tex.width,
                        height = (uint)tex.height,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                    pendingTintedKeys.Add(cacheKey);
                    pendingTintedHashes[cacheKey] = hash;
                }

                return (new IntPtr((long)hash), hash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading tinted albedo '{tex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }

        
        /// <summary>
        /// Upload an SDF atlas as an emissive texture by converting alpha → tinted RGB.
        /// Alpha8 SDF atlases blit to RGBA as (0,0,0,glyph), so the standard upload
        /// produces a black RGB texture. This method reads the alpha channel and writes
        /// it into RGB multiplied by the given tint color.
        /// </summary>
        private (IntPtr handle, ulong hash) UploadSDFEmissiveTexture(Texture2D tex, Color tint)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);
            
            // Cache key: texture ID ^ tint color hash, shifted to avoid collision with tinted emissive cache
            byte tR = (byte)(Mathf.Clamp01(tint.r) * 255f);
            byte tG = (byte)(Mathf.Clamp01(tint.g) * 255f);
            byte tB = (byte)(Mathf.Clamp01(tint.b) * 255f);
            int tintKey = (tR << 16) | (tG << 8) | tB;
            long cacheKey = ~((long)tex.GetInstanceID() << 24) ^ (uint)tintKey; // bitwise NOT to separate from tinted cache
            
            lock (pendingTextureLock)
            {
                if (tintedTextureCache.TryGetValue(cacheKey, out var cached))
                    return cached;
                if (pendingTintedHashes.TryGetValue(cacheKey, out ulong pHash))
                    return (new IntPtr((long)pHash), pHash);
            }
            
            try
            {
                Color32[] pixels;
                if (UnityCompat.IsReadable(tex))
                {
                    pixels = tex.GetPixels32();
                }
                else
                {
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(tex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                    readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    UnityEngine.Object.Destroy(readable);
                }
                
                // Convert: use max(r, g, b, a) as luminance to handle both Alpha8 (RGB=0, A=glyph)
                // and RGBA atlases. Multiply by tint color.
                byte[] pixelData = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    // The distance field to glyph coverage: the edge at 0.5, a short ramp across it.
                    float field = Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, Mathf.Max(pixels[i].b, pixels[i].a))) / 255f;
                    byte lum = (byte)(Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.58f, field)) * 255f);
                    pixelData[i * 4 + 0] = (byte)((lum * tR) / 255);
                    pixelData[i * 4 + 1] = (byte)((lum * tG) / 255);
                    pixelData[i * 4 + 2] = (byte)((lum * tB) / 255);
                    pixelData[i * 4 + 3] = lum; // alpha = glyph coverage, tested at 0.5
                }
                
                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;
                
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed SDF emissive hash for '{tex.name}' tint=({tR},{tG},{tB}): 0x{hash:X16}");
                
                // Queue for deferred upload on the render thread
                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = cacheKey,
                        pixelData = pixelData,
                        hash = hash,
                        width = (uint)tex.width,
                        height = (uint)tex.height,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                    pendingTintedKeys.Add(cacheKey);
                    pendingTintedHashes[cacheKey] = hash;
                }
                
                return (new IntPtr((long)hash), hash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading SDF emissive '{tex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }
        
        /// <summary>
        /// Get texture hash string from handle (for material creation)
        /// Remix expects the hash that was used when uploading the texture
        /// </summary>
        public string GetTexturePathFromHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return null;
            
            // The handle IS the texture hash
            ulong handleValue = (ulong)handle.ToInt64();
            return $"0x{handleValue:X16}";
        }
    }
}
