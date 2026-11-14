using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    // Creating the Remix material: OpaqueEXT, translucent, emissive and the OpenRemix extensions.
    public partial class RemixMaterialManager
    {
        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        
        /// <param name="replace">Create it even if a material with its hash exists: OpenRemix
        /// replaces that one in place (a live settings change).</param>
        private IntPtr CreateRemixMaterialSimple(ref MaterialTextureData matData, int materialId, bool replace = false)
        {
            if (createMaterialFunc == null)
                return IntPtr.Zero;
                
            try
            {
                // Ensure all pending texture uploads are processed before creating material
                ProcessPendingTextureUploads();

                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                ulong matHash = GenerateMaterialHash(matData);

                lock (apiLock)
                {
                    if (!replace && materialHandleByHash.TryGetValue(matHash, out IntPtr existingHandle) && existingHandle != IntPtr.Zero)
                    {
                        return existingHandle;
                    }
                }
                
                string cleanMatName = matData.materialName;
                if (!string.IsNullOrEmpty(cleanMatName))
                {
                    cleanMatName = cleanMatName.Replace(" (Instance)", "").Replace(" Instance", "").Replace("(Clone)", "").Trim();
                }

                if (verboseTextureLogging != null && verboseTextureLogging.Value)
                {
                    logger.LogInfo($"[HashDebug-Material] materialId={materialId} rawName='{matData.materialName}' cleanedName='{cleanMatName}' emColor=({matData.emissiveColor.r:F3},{matData.emissiveColor.g:F3},{matData.emissiveColor.b:F3}) matHash=0x{matHash:X16}");
                }
                
                // Use debug placeholder for materials with no albedo texture
                if (albedoPath == null)
                {
                    EnsureDebugTexture();
                    if (debugTextureHandle != IntPtr.Zero)
                    {
                        albedoPath = DebugTextureHashPath;
                        lock (placeholderMaterialNames)
                            placeholderMaterialNames.Add(matData.materialName ?? $"mat_{materialId}");
                    }
                }
                else if (albedoPath != DebugTextureHashPath)
                {
                    // Verify albedo texture actually exists in OpenRemix runtime
                    ulong h = (ulong)matData.albedoHandle.ToInt64();
                    bool exists;
                    lock (uploadedTextureHashes) { exists = uploadedTextureHashes.Contains(h); }
                    if (!exists)
                    {
                        EnsureDebugTexture();
                        albedoPath = debugTextureHandle != IntPtr.Zero ? DebugTextureHashPath : null;
                    }
                }

                // Verify normal texture actually exists in OpenRemix runtime
                if (normalPath != null)
                {
                    ulong h = (ulong)matData.normalHandle.ToInt64();
                    bool exists;
                    lock (uploadedTextureHashes) { exists = uploadedTextureHashes.Contains(h); }
                    if (!exists)
                    {
                        normalPath = null;
                    }
                }

                // Verify emissive texture actually exists in OpenRemix runtime
                string emissivePath = GetTexturePathFromHandle(matData.emissiveHandle);
                if (emissivePath != null)
                {
                    ulong h = (ulong)matData.emissiveHandle.ToInt64();
                    bool exists;
                    lock (uploadedTextureHashes) { exists = uploadedTextureHashes.Contains(h); }
                    if (!exists)
                    {
                        emissivePath = null;
                    }
                }

                if (verboseTextureLogging.Value)
                    logger.LogInfo($"[MaterialCreate] '{matData.materialName}': albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}, emissive={emissivePath ?? "none"}, emColor=({matData.emissiveColor.r:F3},{matData.emissiveColor.g:F3},{matData.emissiveColor.b:F3}), emIntensity={matData.emissiveIntensity:F3}, alphaMode={matData.alphaMode}, transKind={matData.translucentKind}");
                
                GCHandle extHandle;
                GCHandle emissionExtHandle = default;
                GCHandle remapExtHandle = default;
                GCHandle portalExtHandle = default;
                GCHandle viewEffectsHandle = default;
                GCHandle paintFinishHandle = default;
                IntPtr roughnessPathPtr = IntPtr.Zero, metallicPathPtr = IntPtr.Zero;
                IntPtr extPtr;
                IntPtr emissionExtPtr = IntPtr.Zero;
                RemixAPI.remixapi_MaterialInfoTranslucentEXT_Raw transExt = default;
                RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw opaqueExt = default;

                if (matData.suppressLightGeneration)
                {
                    var emissionExt = new RemixAPI.remixapi_MaterialInfoEmissionEXT
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_EMISSION_EXT,
                        pNext = IntPtr.Zero,
                        suppressLightGeneration = 1
                    };
                    emissionExtHandle = GCHandle.Alloc(emissionExt, GCHandleType.Pinned);
                    emissionExtPtr = emissionExtHandle.AddrOfPinnedObject();
                }

                if (matData.translucentKind != TranslucentKind.None)
                {
                    float transR = IsFinite(matData.transmittanceColor.r) ? Mathf.Clamp01(matData.transmittanceColor.r) : 1f;
                    float transG = IsFinite(matData.transmittanceColor.g) ? Mathf.Clamp01(matData.transmittanceColor.g) : 1f;
                    float transB = IsFinite(matData.transmittanceColor.b) ? Mathf.Clamp01(matData.transmittanceColor.b) : 1f;
                    float ior = IsFinite(matData.refractiveIndex) && matData.refractiveIndex >= 1f ? matData.refractiveIndex : 1.5f;
                    float dist = IsFinite(matData.transmittanceDistance) && matData.transmittanceDistance >= 0f ? matData.transmittanceDistance : 0f;

                    transExt = new RemixAPI.remixapi_MaterialInfoTranslucentEXT_Raw
                    {
                        sType = (int)RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_TRANSLUCENT_EXT,
                        pNext = emissionExtPtr,
                        transmittanceTexture = IntPtr.Zero,
                        refractiveIndex = ior,
                        transmittanceColor = new RemixAPI.remixapi_Float3D
                        {
                            x = transR,
                            y = transG,
                            z = transB
                        },
                        transmittanceMeasurementDistance = dist,
                        thinWallThickness_hasvalue = matData.translucentKind == TranslucentKind.Glass ? 1 : 0,
                        thinWallThickness_value = matData.translucentKind == TranslucentKind.Glass ? 0.005f : 0.0f,
                        useDiffuseLayer = 0
                    };
                    extHandle = GCHandle.Alloc(transExt, GCHandleType.Pinned);
                    extPtr = extHandle.AddrOfPinnedObject();

                    logger.LogInfo($"[TranslucentEXT] '{matData.materialName}': created {matData.translucentKind} (IOR={transExt.refractiveIndex:F3}, dist={transExt.transmittanceMeasurementDistance:F1}m, thinWall={transExt.thinWallThickness_hasvalue}, color=({transExt.transmittanceColor.x:F2},{transExt.transmittanceColor.y:F2},{transExt.transmittanceColor.z:F2}))");
                }
                else
                {
                    float rNorm = (!string.IsNullOrEmpty(matData.materialName) && matData.materialName.IndexOf("blood", StringComparison.OrdinalIgnoreCase) >= 0) ? 0.15f : matData.roughness;
                    float clampedRoughness = Mathf.Clamp(IsFinite(rNorm) ? rNorm : 0.7f, 0.04f, 1.0f);
                    float clampedMetallic = Mathf.Clamp01(IsFinite(matData.metallic) ? matData.metallic : 0.0f);
                    float clampedOpacity = matData.alphaMode == AlphaMode.Opaque ? 1.0f : (IsFinite(matData.albedoColor.a) ? Mathf.Clamp01(matData.albedoColor.a) : 1.0f);
                    float albedoR = IsFinite(matData.albedoColor.r) ? Mathf.Max(0f, matData.albedoColor.r) : 1f;
                    float albedoG = IsFinite(matData.albedoColor.g) ? Mathf.Max(0f, matData.albedoColor.g) : 1f;
                    float albedoB = IsFinite(matData.albedoColor.b) ? Mathf.Max(0f, matData.albedoColor.b) : 1f;

                    // Build the OpaqueEXT using the blittable Raw struct for safe pNext chaining
                    opaqueExt = new RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw
                    {
                        sType = (int)RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_EXT,
                        pNext = emissionExtPtr,
                        roughnessTexture = IntPtr.Zero,
                        metallicTexture = IntPtr.Zero,
                        anisotropy = 0.0f,
                        albedoConstant_x = albedoR,
                        albedoConstant_y = albedoG,
                        albedoConstant_z = albedoB,
                        opacityConstant = clampedOpacity,
                        roughnessConstant = clampedRoughness,
                        metallicConstant = clampedMetallic,
                        thinFilmThickness_hasvalue = 0,
                        thinFilmThickness_value = 0.0f,
                        alphaIsThinFilmThickness = 0,
                        heightTexture = IntPtr.Zero,
                        displaceIn = 0.0f,
                        useDrawCallAlphaState = 0,
                        blendType_hasvalue = 0,
                        blendType_value = 0,
                        invertedBlend = 0,
                        // VkCompareOp: 0=NEVER, 6=GREATER_OR_EQUAL, 7=ALWAYS
                        alphaTestType = 7, // kAlways — default: pass all pixels (no alpha test)
                        alphaReferenceValue = 0,
                        displaceOut = 0.0f
                    };
                    
                    // Configure alpha based on detected mode
                    switch (matData.alphaMode)
                    {
                        case AlphaMode.Cutout:
                            // VkCompareOp 6 = GREATER_OR_EQUAL: pass pixels with alpha >= reference
                            opaqueExt.alphaTestType = 6;
                            opaqueExt.alphaReferenceValue = matData.alphaCutoff > 0
                                ? (byte)Mathf.Clamp((int)(matData.alphaCutoff * 255f), 1, 255)
                                : (byte)127;
                            break;
                        case AlphaMode.Blend:
                            // Alpha-blended (kept in USD captures; native rendering uses the alpha test below)
                            opaqueExt.blendType_hasvalue = 1; // remixapi_Bool.True
                            opaqueExt.blendType_value = 1;
                            // OpenRemix: enable alpha testing (GREATER_OR_EQUAL) so near-zero alpha texels are discarded in acceptCandidate
                            opaqueExt.alphaTestType = 6;
                            if (matData.suppressLightGeneration)
                            {
                                // Particle/trail instances carry remixapi_InstanceInfoBlendEXT (alphaBlendEnabled = 1 -> hit.blended = true).
                                // Keep opacityConstant = 1.0f so OpenRemix does NOT set material->translucent = true (hit.tinted = true),
                                // which would disable coverageBlended and multiply background rays by dark edge RGB (black outlines).
                                // Use alphaReferenceValue = 2 (~0.008 alpha) so very subtle smoke/steam particles are not hard-clipped while 0-alpha corners are discarded.
                                opaqueExt.alphaReferenceValue = 2;
                                opaqueExt.opacityConstant = 1.0f;
                            }
                            else
                            {
                                opaqueExt.alphaReferenceValue = matData.alphaCutoff > 0
                                    ? (byte)Mathf.Clamp((int)(matData.alphaCutoff * 255f), 1, 255)
                                    : (byte)16;
                                if (opaqueExt.opacityConstant >= 1.0f)
                                {
                                    opaqueExt.opacityConstant = 0.999f;
                                }
                            }
                            break;
                    }
                    
                    // Log all OpaqueEXT values being sent to Remix
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"[OpaqueEXT] '{matData.materialName}': " +
                        $"sType={opaqueExt.sType} " +
                        $"albedo=({opaqueExt.albedoConstant_x:F3},{opaqueExt.albedoConstant_y:F3},{opaqueExt.albedoConstant_z:F3}) " +
                        $"opacity={opaqueExt.opacityConstant:F3} " +
                        $"roughness={opaqueExt.roughnessConstant:F3} metallic={opaqueExt.metallicConstant:F3} " +
                        $"alphaTestType={opaqueExt.alphaTestType} alphaRef={opaqueExt.alphaReferenceValue} " +
                        $"blendHasValue={opaqueExt.blendType_hasvalue} blendValue={opaqueExt.blendType_value} " +
                        $"invertedBlend={opaqueExt.invertedBlend} useDrawCallAlpha={opaqueExt.useDrawCallAlphaState} " +
                        $"structSize={System.Runtime.InteropServices.Marshal.SizeOf<RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw>()}");
                    
                    // Per-texel roughness and metallic (remixapi_MaterialPbrRemapEXT): Unity's own maps
                    // (DetectUnityPbrMaps), else AutoPBR's roughness detail from the albedo (RefineInferredPbr).
                    string roughnessPath = null, metallicPath = null;
                    var roughnessChannel = new RemixAPI.remixapi_MaterialPbrChannelEXT { exponent = 1f, scale = 1f };
                    var metallicChannel = new RemixAPI.remixapi_MaterialPbrChannelEXT { exponent = 1f, scale = 1f };
                    bool realAlbedo = albedoPath != null && albedoPath != DebugTextureHashPath;
                    string mapPath = GetTexturePathFromHandle(matData.pbrMapHandle);
                    if (mapPath != null)
                    {
                        bool uploaded;
                        lock (uploadedTextureHashes) { uploaded = uploadedTextureHashes.Contains((ulong)matData.pbrMapHandle.ToInt64()); }
                        if (!uploaded) mapPath = null;
                    }
                    if (mapPath != null && matData.pbrMapMetallic)
                    {
                        metallicPath = mapPath;
                        metallicChannel = new RemixAPI.remixapi_MaterialPbrChannelEXT
                        {
                            channel = 0, exponent = 1f, bias = matData.pbrMetalBias, scale = matData.pbrMetalScale
                        };
                    }
                    // The albedo's alpha is smoothness only in the texture as Unity has it, not a tinted or composited copy.
                    bool plainAlbedo = realAlbedo && matData.albedoTex != null &&
                                       textureHashCache.TryGetValue(matData.albedoTex.GetInstanceID(), out ulong plainHash) &&
                                       plainHash == (ulong)matData.albedoHandle.ToInt64();
                    string smoothnessPath = mapPath != null && matData.pbrMapSmoothness ? mapPath
                                          : matData.smoothnessInAlbedoAlpha && plainAlbedo ? albedoPath : null;
                    if (smoothnessPath != null)
                    {
                        roughnessPath = smoothnessPath;
                        roughnessChannel = new RemixAPI.remixapi_MaterialPbrChannelEXT
                        {
                            channel = 3, invert = 1, exponent = 1f, bias = matData.pbrRoughBias, scale = matData.pbrRoughScale
                        };
                    }
                    else if (matData.hasRoughnessDetail && matData.hasInferredPbr && ConfigAutoPBR != null && ConfigAutoPBR.Value && realAlbedo)
                    {
                        roughnessPath = albedoPath;
                        roughnessChannel = new RemixAPI.remixapi_MaterialPbrChannelEXT
                        {
                            channel = 1, // green
                            invert = 1,  // brighter texels smoother
                            exponent = 1f,
                            bias = matData.roughnessDetailBias,
                            scale = matData.roughnessDetailScale
                        };
                    }
                    if (roughnessPath != null || metallicPath != null)
                    {
                        var remapExt = new RemixAPI.remixapi_MaterialPbrRemapEXT
                        {
                            sType = (int)RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_PBR_REMAP_EXT,
                            pNext = opaqueExt.pNext,
                            roughness = roughnessChannel,
                            metallic = metallicChannel
                        };
                        remapExtHandle = GCHandle.Alloc(remapExt, GCHandleType.Pinned);
                        if (roughnessPath != null)
                        {
                            roughnessPathPtr = Marshal.StringToHGlobalUni(roughnessPath);
                            opaqueExt.roughnessTexture = roughnessPathPtr;
                        }
                        if (metallicPath != null)
                        {
                            metallicPathPtr = Marshal.StringToHGlobalUni(metallicPath);
                            opaqueExt.metallicTexture = metallicPathPtr;
                        }
                        opaqueExt.pNext = remapExtHandle.AddrOfPinnedObject();
                    }

                    // A ray portal: its surfaces open it; the opaque material is their look where
                    // it doesn't open. A registered portal (RayPortals' own) stands alone:
                    // its draw places the portal over the surfaces opening it.
                    bool standalonePortal = matData.rayPortalIndex >= 0 && matData.rayPortalStandalone;
                    // What a view portal's screen shows takes the game shader's effects.
                    if (matData.viewChromaticAberration > 0f && !standalonePortal)
                    {
                        var viewEffects = new RemixAPI.remixapi_MaterialInfoViewEffectsEXT
                        {
                            sType = RemixAPI.REMIXAPI_STRUCT_TYPE_MATERIAL_VIEW_EFFECTS_EXT,
                            pNext = opaqueExt.pNext,
                            tint = new RemixAPI.remixapi_Float3D(1f, 1f, 1f),
                            saturation = 1f,
                            contrast = 0f,
                            chromaticAberration = Mathf.Min(matData.viewChromaticAberration, 1f / 240f)
                        };
                        viewEffectsHandle = GCHandle.Alloc(viewEffects, GCHandleType.Pinned);
                        opaqueExt.pNext = viewEffectsHandle.AddrOfPinnedObject();
                    }
                    if (matData.paintFinish && !standalonePortal)
                    {
                        var finish = new RemixAPI.remixapi_MaterialInfoPaintFinishEXT
                        {
                            sType = RemixAPI.REMIXAPI_STRUCT_TYPE_MATERIAL_PAINT_FINISH_EXT,
                            pNext = opaqueExt.pNext,
                            enabled = 1
                        };
                        paintFinishHandle = GCHandle.Alloc(finish, GCHandleType.Pinned);
                        opaqueExt.pNext = paintFinishHandle.AddrOfPinnedObject();
                    }
                    if (matData.rayPortalIndex >= 0)
                    {
                        var portalExt = new RemixAPI.remixapi_MaterialInfoPortalEXT
                        {
                            sType = (int)RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_PORTAL_EXT,
                            pNext = standalonePortal ? IntPtr.Zero : opaqueExt.pNext,
                            rayPortalIndex = (byte)matData.rayPortalIndex,
                            rotationSpeed = 0f
                        };
                        portalExtHandle = GCHandle.Alloc(portalExt, GCHandleType.Pinned);
                        if (!standalonePortal)
                            opaqueExt.pNext = portalExtHandle.AddrOfPinnedObject();
                    }

                    extHandle = GCHandle.Alloc(opaqueExt, GCHandleType.Pinned);
                    extPtr = standalonePortal ? portalExtHandle.AddrOfPinnedObject() : extHandle.AddrOfPinnedObject();
                }

                try
                {
                    // Emissive color: Remix expects a normalized direction color + scalar intensity.
                    // Compute combined intensity = colorBrightness * separateIntensity, then normalize color.
                    float emR = IsFinite(matData.emissiveColor.r) ? Mathf.Max(0f, matData.emissiveColor.r) : 0f;
                    float emG = IsFinite(matData.emissiveColor.g) ? Mathf.Max(0f, matData.emissiveColor.g) : 0f;
                    float emB = IsFinite(matData.emissiveColor.b) ? Mathf.Max(0f, matData.emissiveColor.b) : 0f;
                    float emIntensity = IsFinite(matData.emissiveIntensity) ? Mathf.Max(0f, matData.emissiveIntensity) : 0f;

                    float emMaxCh = Mathf.Max(emR, Mathf.Max(emG, emB));
                    float emCombinedIntensity = emMaxCh > 0f ? emIntensity * emMaxCh : emIntensity;
                    // Unity's emission is what it shows unlit; Remix radiance in the scene's light units.
                    // A backdrop (a panoramic sky) keeps the value Unity shows: it lights nothing, and the
                    // game shows it as drawn whatever the scene's light.
                    if ((EmissionInLightUnits == null || EmissionInLightUnits.Value) && !matData.emissionAsShown && !matData.unlitSheet)
                        emCombinedIntensity *= UnlitRadianceScale;
                    if (!IsFinite(emCombinedIntensity) || emCombinedIntensity < 0f) emCombinedIntensity = 0f;
                    
                    var materialInfo = new RemixAPI.remixapi_MaterialInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO,
                        pNext = extPtr,
                        hash = matHash,
                        albedoTexture = albedoPath,
                        normalTexture = normalPath,
                        tangentTexture = null,
                        emissiveTexture = emissivePath,
                        emissiveIntensity = emCombinedIntensity,
                        emissiveColorConstant = new RemixAPI.remixapi_Float3D {
                            x = emMaxCh > 0f ? emR / emMaxCh : 0f,
                            y = emMaxCh > 0f ? emG / emMaxCh : 0f,
                            z = emMaxCh > 0f ? emB / emMaxCh : 0f
                        },
                        spriteSheetRow = 1,
                        spriteSheetCol = 1,
                        spriteSheetFps = 0,
                        filterMode = (byte)(matData.filterMode <= 1 ? matData.filterMode : 1),
                        wrapModeU = (byte)(matData.wrapModeU <= 3 ? matData.wrapModeU : 1),
                        wrapModeV = (byte)(matData.wrapModeV <= 3 ? matData.wrapModeV : 1)
                    };
                    
                    IntPtr materialHandle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createMaterialFunc(ref materialInfo, out materialHandle);
                        if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS && materialHandle != IntPtr.Zero)
                        {
                            materialHandleByHash[matHash] = materialHandle;
                        }
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create Remix material for '{matData.materialName}': {result}");
                        return IntPtr.Zero;
                    }
                    
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"Created Remix material '{matData.materialName}' with hash 0x{matHash:X}, handle: 0x{materialHandle.ToInt64():X}");
                    return materialHandle;
                }
                finally
                {
                    if (emissionExtHandle.IsAllocated) emissionExtHandle.Free();
                    if (remapExtHandle.IsAllocated) remapExtHandle.Free();
                    if (portalExtHandle.IsAllocated) portalExtHandle.Free();
                    if (viewEffectsHandle.IsAllocated) viewEffectsHandle.Free();
                    if (paintFinishHandle.IsAllocated) paintFinishHandle.Free();
                    if (roughnessPathPtr != IntPtr.Zero) Marshal.FreeHGlobal(roughnessPathPtr);
                    if (metallicPathPtr != IntPtr.Zero) Marshal.FreeHGlobal(metallicPathPtr);
                    extHandle.Free();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception creating material: {ex.Message}");
                return IntPtr.Zero;
            }
        }
    }
}
