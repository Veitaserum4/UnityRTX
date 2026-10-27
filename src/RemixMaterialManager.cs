using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Manages materials and textures for Remix - handles upload, caching, and material creation
    /// </summary>
    public class RemixMaterialManager
    {
        private readonly ManualLogSource logger;
        private readonly TextureCategoryManager textureCategoryManager;
        private readonly BepInEx.Configuration.ConfigEntry<bool> captureTextures;
        private readonly BepInEx.Configuration.ConfigEntry<bool> captureMaterials;
        private readonly BepInEx.Configuration.ConfigEntry<bool> verboseTextureLogging;
        private readonly object apiLock;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_CreateTexture createTextureFunc;
        private RemixAPI.PFN_remixapi_DestroyTexture destroyTextureFunc;
        private RemixAPI.PFN_remixapi_CreateMaterial createMaterialFunc;
        private RemixAPI.PFN_remixapi_DestroyMaterial destroyMaterialFunc;
        private RemixAPI.PFN_remixapi_AddTextureHash addTextureHashFunc;
        private RemixAPI.PFN_remixapi_RemoveTextureHash removeTextureHashFunc;
        
        // Cache for uploaded textures - maps Unity texture instance ID to Remix handle
        private Dictionary<int, IntPtr> textureCache = new Dictionary<int, IntPtr>();

        // Debug texture for meshes with no albedo — visible in Remix so it can be categorized/hidden
        private IntPtr debugTextureHandle = IntPtr.Zero;
        private ulong debugTextureHash;
        private const string DebugTextureHashPath = "0xDEBB0600DEBB0600"; // stable sentinel
        
        // DXT5nm Z reconstruction: zLookup[x * 256 + y] = byte Z given X,Y normal components.
        // Precomputed once to avoid per-pixel sqrt during normal map unpacking.
        private static readonly byte[] zLookup = BuildZLookup();
        private static byte[] BuildZLookup()
        {
            var t = new byte[256 * 256];
            for (int x = 0; x < 256; x++)
            {
                float nx = x / 127.5f - 1f;
                float nx2 = nx * nx;
                for (int y = 0; y < 256; y++)
                {
                    float ny = y / 127.5f - 1f;
                    float nz2 = 1f - nx2 - ny * ny;
                    float nz = nz2 > 0f ? (float)Math.Sqrt(nz2) : 0f;
                    t[x * 256 + y] = (byte)(nz * 127.5f + 127.5f);
                }
            }
            return t;
        }
        
        // Cache for texture hashes - maps Unity texture instance ID to XXH64 hash
        private Dictionary<int, ulong> textureHashCache = new Dictionary<int, ulong>();
        
        // Track which textures have meaningful alpha (not all-opaque)
        private HashSet<int> texturesWithAlpha = new HashSet<int>();
        
        // Track textures with genuine cutout transparency (large regions of near-zero alpha).
        // Distinguished from smoothness-as-alpha (gradual values) used in Standard shader Opaque mode.
        private HashSet<int> texturesWithCutoutAlpha = new HashSet<int>();
        
        private static readonly string[] AlbedoTextureProps = new string[]
        {
            "_MainTex", "_BaseMap", "_BaseTexture", "_Texture1", "_Texture", "_Albedo",
            "_Diffuse", "_ColorMap", "_BaseColorMap", "_TopTex", "_TextureSample0",
            "_TextureSample1", "_Texture2", "_Texture3", "_TextureSample2", "_TextureSample3", "_Tex"
        };

        private static readonly string[] NormalTextureProps = new string[]
        {
            "_BumpMap", "_Normal", "_NormalMap", "_DetailNormalMap", "_BumpOutline", "_BumpFace"
        };

        // Cache for materials - maps Unity material instance ID to Remix material handle
        private Dictionary<int, IntPtr> materialCache = new Dictionary<int, IntPtr>();

        // Cache for created Remix material handles by content hash (prevents duplicate CreateMaterial calls for cloned Unity materials)
        private readonly Dictionary<ulong, IntPtr> materialHandleByHash = new Dictionary<ulong, IntPtr>();

        // Track materials that fell back to the debug placeholder texture (no albedo)
        private readonly HashSet<string> placeholderMaterialNames = new HashSet<string>();
        
        // Cache for solid-color 1x1 textures — maps RGBA32 key to (provisional handle = hash as IntPtr)
        private Dictionary<uint, IntPtr> solidColorTextureCache = new Dictionary<uint, IntPtr>();
        
        // Cache for tinted emissive textures - maps (texId ^ tintColorKey) to (handle, hash)
        private Dictionary<long, (IntPtr handle, ulong hash)> tintedTextureCache = new Dictionary<long, (IntPtr, ulong)>();
        
        // Deferred texture upload queue — main thread prepares pixel data, render thread calls Remix API.
        // Prevents cross-thread DXVK lock contention between CreateTexture (s_mutex→devLock) and Present (devLock→submission).
        private struct PendingTextureUpload
        {
            public int texId;            // Unity texture instance ID (-1 for tinted/SDF entries)
            public long tintedCacheKey;  // Tinted texture cache key (0 for regular textures)
            public byte[] pixelData;
            public ulong hash;
            public uint width, height, mipLevels;
            public RemixAPI.remixapi_Format format;
        }
        private readonly Queue<PendingTextureUpload> pendingTextureUploads = new Queue<PendingTextureUpload>();
        private readonly HashSet<int> pendingTextureIds = new HashSet<int>();
        private readonly HashSet<long> pendingTintedKeys = new HashSet<long>();
        private readonly Dictionary<long, ulong> pendingTintedHashes = new Dictionary<long, ulong>();
        private readonly object pendingTextureLock = new object();
        
        // Alpha handling modes detected from Unity materials
        public enum AlphaMode
        {
            Opaque,     // No alpha
            Cutout,     // Alpha test (discard below threshold)
            Blend       // Alpha blending (fade/transparent)
        }

        // Translucent material classification for OpenRemix path tracing
        public enum TranslucentKind
        {
            None,       // Opaque, Cutout, or standard Blend
            Water,      // Translucent body: thick liquid volume with Snell's window, refraction, TIR, and Beer-Lambert volumetric absorption
            Glass       // Translucent thin wall: specular transmission sheet with Fresnel reflection
        }

        // Material data captured from Unity materials
        public struct MaterialTextureData
        {
            public IntPtr albedoHandle;
            public IntPtr normalHandle;
            public ulong albedoTextureHash;
            public ulong normalTextureHash;
            public Color albedoColor;
            public string materialName;
            public IntPtr remixMaterialHandle;
            public AlphaMode alphaMode;
            public float alphaCutoff; // 0-1, used for Cutout mode
            public byte wrapModeU;    // MDL WrapMode: 0=Clamp, 1=Repeat, 2=MirroredRepeat, 3=Clip
            public byte wrapModeV;
            public byte filterMode;   // MDL Filter: 0=Nearest, 1=Linear
            public Vector4 mainTexST; // _MainTex_ST: (scaleU, scaleV, offsetU, offsetV)
            public IntPtr emissiveHandle;
            public ulong emissiveTextureHash;
            public Color emissiveColor;    // HDR emission color (can exceed 1.0)
            public float emissiveIntensity;
            public bool useEmissiveBlend;  // Use kAlphaEmissive blend instead of kAlpha
            public bool suppressLightGeneration; // OpenRemix: visible emission without analytic triangle light generation in LightBvh
            public float metallic;         // PBR metallic (0.0 to 1.0)
            public float roughness;        // PBR roughness (0.04 to 1.0)
            public TranslucentKind translucentKind;
            public float refractiveIndex;
            public float transmittanceDistance;
            public Color transmittanceColor;

            // Auto PBR & Normal Map state for live updates
            public float baseMetallic;
            public float baseRoughness;
            public float inferredMetallic;
            public float inferredRoughness;
            public bool hasInferredPbr;
            public IntPtr originalNormalHandle;
            public ulong originalNormalTextureHash;
            public Texture2D albedoTex;
        }
        private System.Collections.Concurrent.ConcurrentDictionary<int, MaterialTextureData> materialTextureData = new System.Collections.Concurrent.ConcurrentDictionary<int, MaterialTextureData>();
        private RemixWaterManager waterManager;
        public void SetWaterManager(RemixWaterManager manager) => this.waterManager = manager;
        private HashSet<string> loggedShaderProperties = new HashSet<string>();

        // Auto PBR & Normal Map Generation (Experimental)
        public BepInEx.Configuration.ConfigEntry<bool> ConfigAutoPBR { get; private set; }
        public BepInEx.Configuration.ConfigEntry<bool> ConfigAutoNormalMaps { get; private set; }
        public BepInEx.Configuration.ConfigEntry<float> ConfigAutoNormalStrength { get; private set; }
        private int autoPbrMaterialCount = 0;
        private int autoNormalMapCount = 0;
        public int AutoPbrMaterialCount => autoPbrMaterialCount;
        public int AutoNormalMapCount => autoNormalMapCount;
        private readonly Dictionary<long, (IntPtr handle, ulong hash)> autoNormalCache = new Dictionary<long, (IntPtr, ulong)>();
        
        // Track all texture hashes confirmed to be uploaded into OpenRemix native runtime
        private readonly HashSet<ulong> uploadedTextureHashes = new HashSet<ulong>();
        private IntPtr fallbackMaterialHandle = IntPtr.Zero;
        
        // Queue for async material creation
        private Queue<int> pendingMaterialCreation = new Queue<int>();
        private HashSet<int> pendingMaterialSet = new HashSet<int>(); // Track what's pending
        private bool materialCreationThreadRunning = false;
        private Thread materialCreationThread = null;
        
        public RemixMaterialManager(
            ManualLogSource logger,
            TextureCategoryManager categoryManager,
            BepInEx.Configuration.ConfigEntry<bool> captureTextures,
            BepInEx.Configuration.ConfigEntry<bool> captureMaterials,
            BepInEx.Configuration.ConfigEntry<bool> verboseTextureLogging,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock,
            BepInEx.Configuration.ConfigFile config = null)
        {
            this.logger = logger;
            this.textureCategoryManager = categoryManager;
            this.captureTextures = captureTextures;
            this.captureMaterials = captureMaterials;
            this.verboseTextureLogging = verboseTextureLogging;
            this.apiLock = apiLock;

            if (config != null)
            {
                ConfigAutoPBR = config.Bind("Materials", "AutoPBR", false,
                    "Infer PBR metallic and roughness from material/texture names for non-PBR materials (Experimental).");
                ConfigAutoNormalMaps = config.Bind("Materials", "AutoNormalMaps", false,
                    "Generate tangent-space normal maps from albedo textures via Sobel filter when no normal map exists (Experimental).");
                ConfigAutoNormalStrength = config.Bind("Materials", "AutoNormalStrength", 1.0f,
                    "Strength/intensity of automatically generated normal maps (0.1 to 3.0) (Experimental).");
            }
            
            // Cache delegates
            if (remixInterface.CreateTexture != IntPtr.Zero)
                createTextureFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateTexture>(remixInterface.CreateTexture);
            
            if (remixInterface.DestroyTexture != IntPtr.Zero)
                destroyTextureFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyTexture>(remixInterface.DestroyTexture);
            
            if (remixInterface.CreateMaterial != IntPtr.Zero)
                createMaterialFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateMaterial>(remixInterface.CreateMaterial);
            
            if (remixInterface.DestroyMaterial != IntPtr.Zero)
                destroyMaterialFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyMaterial>(remixInterface.DestroyMaterial);
            
            if (remixInterface.AddTextureHash != IntPtr.Zero)
                addTextureHashFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_AddTextureHash>(remixInterface.AddTextureHash);
            
            if (remixInterface.RemoveTextureHash != IntPtr.Zero)
                removeTextureHashFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_RemoveTextureHash>(remixInterface.RemoveTextureHash);
        }
        
        /// <summary>
        /// Get material texture data for a material ID
        /// </summary>
        public bool TryGetMaterialData(int materialId, out MaterialTextureData data)
        {
            return materialTextureData.TryGetValue(materialId, out data);
        }
        
        /// <summary>
        /// Get _MainTex_ST (tiling/offset) for a material. Returns (1,1,0,0) if unknown.
        /// </summary>
        /// <summary>
        /// How a captured material will reach Remix, for diagnostics; false while it is not captured yet.
        /// </summary>
        public bool TryDescribeMaterial(int materialId, out string description)
        {
            if (!materialTextureData.TryGetValue(materialId, out var d))
            {
                description = null;
                return false;
            }
            description = $"alpha={d.alphaMode} (cutoff {d.alphaCutoff:F2}), albedoTex={(d.albedoHandle != IntPtr.Zero || d.albedoTextureHash != 0)}, " +
                          $"color=({d.albedoColor.r:F2},{d.albedoColor.g:F2},{d.albedoColor.b:F2},{d.albedoColor.a:F2}), " +
                          $"emissive={d.emissiveIntensity:F2} ({d.emissiveColor.r:F2},{d.emissiveColor.g:F2},{d.emissiveColor.b:F2}) emissiveTex={d.emissiveHandle != IntPtr.Zero} " +
                          $"emissiveBlend={d.useEmissiveBlend}, translucent={d.translucentKind}";
            return true;
        }

        public Vector4 GetMainTexST(int materialId)
        {
            if (materialTextureData.TryGetValue(materialId, out var data))
                return data.mainTexST;
            return new Vector4(1, 1, 0, 0);
        }
        
        public IntPtr GetOrCreateFallbackMaterial()
        {
            if (fallbackMaterialHandle != IntPtr.Zero)
                return fallbackMaterialHandle;

            if (createMaterialFunc == null)
                return IntPtr.Zero;

            try
            {
                EnsureDebugTexture();
                string albedoPath = debugTextureHandle != IntPtr.Zero ? DebugTextureHashPath : null;

                var opaqueExt = new RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw
                {
                    sType = (int)RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_EXT,
                    pNext = IntPtr.Zero,
                    roughnessTexture = IntPtr.Zero,
                    metallicTexture = IntPtr.Zero,
                    anisotropy = 0.0f,
                    albedoConstant_x = 0.8f,
                    albedoConstant_y = 0.8f,
                    albedoConstant_z = 0.8f,
                    opacityConstant = 1.0f,
                    roughnessConstant = 0.7f,
                    metallicConstant = 0.0f,
                    thinFilmThickness_hasvalue = 0,
                    thinFilmThickness_value = 0.0f,
                    alphaIsThinFilmThickness = 0,
                    heightTexture = IntPtr.Zero,
                    displaceIn = 0.0f,
                    useDrawCallAlphaState = 0,
                    blendType_hasvalue = 0,
                    blendType_value = 0,
                    invertedBlend = 0,
                    alphaTestType = 7,
                    alphaReferenceValue = 0,
                    displaceOut = 0.0f
                };

                GCHandle extHandle = GCHandle.Alloc(opaqueExt, GCHandleType.Pinned);
                try
                {
                    var materialInfo = new RemixAPI.remixapi_MaterialInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO,
                        pNext = extHandle.AddrOfPinnedObject(),
                        hash = 0xDEADBEEFCAFE0001UL,
                        albedoTexture = albedoPath,
                        normalTexture = null,
                        tangentTexture = null,
                        emissiveTexture = null,
                        emissiveIntensity = 0.0f,
                        emissiveColorConstant = new RemixAPI.remixapi_Float3D { x = 0f, y = 0f, z = 0f },
                        spriteSheetRow = 1,
                        spriteSheetCol = 1,
                        spriteSheetFps = 0,
                        filterMode = 1,
                        wrapModeU = 1,
                        wrapModeV = 1
                    };

                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createMaterialFunc(ref materialInfo, out fallbackMaterialHandle);
                    }
                    if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogInfo($"Created fallback Remix material: handle 0x{fallbackMaterialHandle.ToInt64():X}");
                    }
                    else
                    {
                        logger.LogError($"Failed to create fallback Remix material: {result}");
                    }
                }
                finally
                {
                    extHandle.Free();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception creating fallback material: {ex.Message}");
            }
            return fallbackMaterialHandle;
        }

        /// <summary>
        /// Get or create Remix material handle for a material ID (on-demand creation)
        /// </summary>
        public IntPtr GetOrCreateMaterial(int materialId)
        {
            if (!materialTextureData.TryGetValue(materialId, out var matData))
                return IntPtr.Zero;
            
            // If material already created, return it
            if (matData.remixMaterialHandle != IntPtr.Zero)
                return matData.remixMaterialHandle;
            
            // Process any pending texture uploads first so textures are guaranteed to exist in OpenRemix runtime
            ProcessPendingTextureUploads();

            // Create material on-demand (called from render thread)
            IntPtr handle = CreateRemixMaterialSimple(ref matData, materialId);
            
            if (handle != IntPtr.Zero)
            {
                // Store the handle back
                matData.remixMaterialHandle = handle;
                materialTextureData[materialId] = matData;
            }
            else
            {
                // Assign neutral fallback material so we do not retry every frame and cause API general failures
                IntPtr fallback = GetOrCreateFallbackMaterial();
                if (fallback != IntPtr.Zero)
                {
                    matData.remixMaterialHandle = fallback;
                    materialTextureData[materialId] = matData;
                    logger.LogWarning($"[MaterialFallback] Material '{matData.materialName}' (ID {materialId}) assigned fallback material.");
                }
            }
            
            return matData.remixMaterialHandle;
        }

        /// <summary>
        /// Register a custom/baked material directly (e.g. for baked terrain).
        /// Returns the materialId.
        /// </summary>
        public int RegisterCustomMaterial(string name, Texture2D albedo, Texture2D normal = null, float roughness = 0.85f, float metallic = 0.0f)
        {
            int matId = HashUtils.HashStringFNV(name).GetHashCode();
            if (matId == 0) matId = 1;

            IntPtr albedoHandle = albedo != null ? UploadUnityTexture(albedo) : IntPtr.Zero;
            ulong albedoHash = 0;
            if (albedo != null)
            {
                lock (pendingTextureLock)
                {
                    textureHashCache.TryGetValue(albedo.GetInstanceID(), out albedoHash);
                }
            }

            IntPtr normalHandle = normal != null ? UploadUnityTexture(normal, true) : IntPtr.Zero;
            ulong normalHash = 0;
            if (normal != null)
            {
                lock (pendingTextureLock)
                {
                    textureHashCache.TryGetValue(normal.GetInstanceID(), out normalHash);
                }
            }

            var matData = new MaterialTextureData
            {
                materialName = name,
                albedoHandle = albedoHandle,
                albedoTextureHash = albedoHash,
                normalHandle = normalHandle,
                normalTextureHash = normalHash,
                albedoColor = Color.white,
                alphaMode = AlphaMode.Opaque,
                wrapModeU = 1, // MDL Repeat
                wrapModeV = 1,
                filterMode = 1,
                mainTexST = new Vector4(1, 1, 0, 0),
                roughness = roughness,
                metallic = metallic,
                emissiveColor = Color.black,
                emissiveIntensity = 0f,
                baseMetallic = metallic,
                baseRoughness = roughness,
                hasInferredPbr = false,
                originalNormalHandle = normalHandle,
                originalNormalTextureHash = normalHash,
                albedoTex = albedo
            };

            materialTextureData[matId] = matData;
            return matId;
        }
        
        /// <summary>
        /// Register a texture to a Remix category
        /// </summary>
        public void RegisterTextureCategory(ulong textureHash, string categoryName)
        {
            if (addTextureHashFunc == null) return;
            
            string hashString = textureHash.ToString("X");
            RemixAPI.remixapi_ErrorCode result;
            lock (apiLock)
            {
                result = addTextureHashFunc(categoryName, hashString);
            }
            
            if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                textureCategoryManager.SetTextureCategory(textureHash, categoryName);
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Registered texture 0x{textureHash:X16} to category '{categoryName}'");
            }
            else
            {
                logger.LogError($"Failed to register texture to category: {result}");
            }
        }

        /// <summary>
        /// Check if a color has perceptible chromatic saturation (distinguishing tinted colors from neutral white/grey/black).
        /// </summary>
        private static bool IsSaturatedColor(Color c)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max < 0.01f) return false;
            float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return ((max - min) / max) > 0.08f;
        }

        /// <summary>
        /// Intelligently resolve the intended albedo/tint color of a material.
        /// Prioritizes chromatic (saturated) colors over default neutral white/grey fallbacks,
        /// and aligns candidate property order with the matched texture channel (_Color1 for _Texture1, _Tint for base textures, etc.).
        /// </summary>
        private static Color ResolveMaterialAlbedoColor(Material material, string matchedTexProp)
        {
            string[] candidates;
            if (matchedTexProp == "_Texture1" || matchedTexProp == "_TextureSample1")
                candidates = new string[] { "_Color1", "_BaseColor", "_Tint", "_Color", "_TintColor", "_ColorTint" };
            else if (matchedTexProp == "_Texture2" || matchedTexProp == "_TextureSample2")
                candidates = new string[] { "_Color2", "_Color1", "_BaseColor", "_Tint", "_Color", "_ColorTint" };
            else if (matchedTexProp == "_Texture3" || matchedTexProp == "_TextureSample3")
                candidates = new string[] { "_Color3", "_Color1", "_BaseColor", "_Tint", "_Color", "_ColorTint" };
            else
                candidates = new string[] { "_Tint", "_BaseColor", "_Color", "_Color1", "_TintColor", "_ColorTint", "_ColorTint2", "_ColorTint3" };

            // Pass 1: find first saturated / non-neutral color
            for (int i = 0; i < candidates.Length; i++)
            {
                string p = candidates[i];
                if (material.HasProperty(p))
                {
                    Color c = material.GetColor(p);
                    if (IsSaturatedColor(c))
                        return c;
                }
            }


            // Pass 3: fallback to first non-black candidate property
            for (int i = 0; i < candidates.Length; i++)
            {
                string p = candidates[i];
                if (material.HasProperty(p))
                {
                    Color c = material.GetColor(p);
                    if (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f)
                        return c;
                }
            }

            return Color.white;
        }

        /// <summary>
        /// Apply Hue, Saturation, Brightness, Contrast (HSBC) adjustments to a color.
        /// Standard HSBC color transformation using Rodrigues rotation formula for hue.
        /// </summary>
        private static Color ApplyHsbcToColor(Color col, Vector4 hsbc)
        {
            float r = col.r;
            float g = col.g;
            float b = col.b;

            float h = hsbc.x;
            float br = hsbc.y;
            float c = hsbc.z;
            float s = hsbc.w;

            if (Mathf.Abs(h) > 0.001f)
            {
                float angleDeg = Mathf.Abs(h) <= 1.0f ? h * 360f : h;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                float cosA = Mathf.Cos(angleRad);
                float sinA = Mathf.Sin(angleRad);
                const float k = 0.577350269f;
                float dotKV = (r + g + b) * k;
                float omc = 1.0f - cosA;
                float nr = r * cosA + k * (b - g) * sinA + k * dotKV * omc;
                float ng = g * cosA + k * (r - b) * sinA + k * dotKV * omc;
                float nb = b * cosA + k * (g - r) * sinA + k * dotKV * omc;
                r = nr;
                g = ng;
                b = nb;
            }

            if (Mathf.Abs(c - 1f) > 0.001f)
            {
                r = (r - 0.5f) * c + 0.5f;
                g = (g - 0.5f) * c + 0.5f;
                b = (b - 0.5f) * c + 0.5f;
            }

            if (Mathf.Abs(br) > 0.001f)
            {
                r += br;
                g += br;
                b += br;
            }

            if (Mathf.Abs(s - 1f) > 0.001f)
            {
                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                r = lum + (r - lum) * s;
                g = lum + (g - lum) * s;
                b = lum + (b - lum) * s;
            }

            return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), col.a);
        }

        /// <summary>
        /// Capture textures from a Unity material, with optional per-renderer property overrides
        /// </summary>
        public void CaptureMaterialTextures(Material material, int materialId, Color? mpbEmissiveColor = null, float? mpbEmissiveIntensity = null, Texture2D mpbMainTex = null, Color? mpbColor = null, Renderer renderer = null, bool suppressLightGeneration = false)
        {
            if (material == null)
                return;

            // Check debug toggle
            if (!captureMaterials.Value)
                return;
            
            // Check if material already exists and has been created or is pending
            if (materialTextureData.ContainsKey(materialId))
            {
                // Material data already captured, don't overwrite it
                return;
            }
            
            // Also check if already pending to avoid race conditions
            lock (pendingMaterialCreation)
            {
                if (pendingMaterialSet.Contains(materialId))
                {
                    return;
                }
            }
                
            string shaderName = material.shader != null ? material.shader.name : "null";
            bool isParticleOrTrailRenderer = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
            bool autoSuppressLight = suppressLightGeneration || isParticleOrTrailRenderer ||
                shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("TextMeshPro/Distance Field", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (material.name != null && (material.name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           material.name.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0));

            var matData = new MaterialTextureData
            {
                materialName = material.name,
                albedoColor = mpbColor ?? Color.white,
                albedoHandle = IntPtr.Zero,
                normalHandle = IntPtr.Zero,
                albedoTextureHash = 0,
                normalTextureHash = 0,
                remixMaterialHandle = IntPtr.Zero,
                alphaMode = AlphaMode.Opaque,
                alphaCutoff = 0.5f,
                wrapModeU = 1, // MDL Repeat
                wrapModeV = 1,
                filterMode = 1, // MDL Linear
                mainTexST = new Vector4(1, 1, 0, 0),
                emissiveHandle = IntPtr.Zero,
                emissiveTextureHash = 0,
                emissiveColor = Color.black,
                emissiveIntensity = 0f,
                suppressLightGeneration = autoSuppressLight,
                metallic = 0.0f,
                roughness = 0.7f,
                translucentKind = TranslucentKind.None,
                refractiveIndex = 1.0f,
                transmittanceDistance = 0.0f,
                transmittanceColor = Color.white
            };
            
            // Match albedo texture first so color resolution can be aligned to the matched texture channel
            Texture2D albedoTex = mpbMainTex;
            Texture2D tex = mpbMainTex;
            string matchedTexProp = null;

            if (captureTextures.Value)
            {
                if (tex != null)
                    matchedTexProp = "_MainTex";
                else
                    tex = FindAlbedoTexture(material, out matchedTexProp);
            }

            // Layered stylized materials: their textures are masks; the composited layers are the albedo.
            vertexColorWeights[materialId] = LayeredMaterialCompositor.VertexColorWeight(material);
            bool layeredComposite = false;
            if (captureTextures.Value && mpbMainTex == null && !(renderer is ParticleSystemRenderer) &&
                LayeredMaterialCompositor.IsLayered(material))
            {
                var composite = GetLayeredComposite(material);
                if (composite != null)
                {
                    tex = composite;
                    matchedTexProp = null;
                    layeredComposite = true;
                }
            }

            // Check for HSV / Brightness / Contrast adjustments (e.g. Unlit/ColorAdjust, stylized character customization)
            Vector4? hsbc = null;
            try
            {
                float hue = 0f;
                float brightness = 0f;
                float contrast = 1f;
                float saturation = 1f;
                bool hasHsbcProp = false;

                if (material.HasProperty("_Hue")) { hue = material.GetFloat("_Hue"); hasHsbcProp = true; }
                else if (material.HasProperty("_HueShift")) { hue = material.GetFloat("_HueShift"); hasHsbcProp = true; }
                else if (material.HasProperty("_HueAdjust")) { hue = material.GetFloat("_HueAdjust"); hasHsbcProp = true; }

                if (material.HasProperty("_Brightness")) { brightness = material.GetFloat("_Brightness"); hasHsbcProp = true; }
                if (material.HasProperty("_Contrast")) { contrast = material.GetFloat("_Contrast"); hasHsbcProp = true; }
                if (material.HasProperty("_Saturation")) { saturation = material.GetFloat("_Saturation"); hasHsbcProp = true; }

                if (material.HasProperty("_HSBC"))
                {
                    Vector4 v = material.GetVector("_HSBC");
                    hue = v.x;
                    saturation = v.y;
                    brightness = v.z;
                    contrast = v.w;
                    hasHsbcProp = true;
                }

                if (hasHsbcProp && (Mathf.Abs(hue) > 0.001f || Mathf.Abs(brightness) > 0.001f || Mathf.Abs(contrast - 1f) > 0.001f || Mathf.Abs(saturation - 1f) > 0.001f))
                {
                    hsbc = new Vector4(hue, brightness, contrast, saturation);
                }
            }
            catch { }

            // Get albedo color if not overridden by MPB (smart priority aligned with matched texture)
            if (!mpbColor.HasValue)
            {
                matData.albedoColor = layeredComposite ? Color.white : ResolveMaterialAlbedoColor(material, matchedTexProp);
            }

            // Extract PBR metallic: prioritize _BaseMetallic if > 0 (designer value in stylized shaders like PEAK)
            float metallic = 0.0f;
            bool hasExplicitMetallic = false;
            if (material.HasProperty("_BaseMetallic") && material.GetFloat("_BaseMetallic") > 0f) { metallic = material.GetFloat("_BaseMetallic"); hasExplicitMetallic = true; }
            else if (material.HasProperty("_Metallic")) { metallic = material.GetFloat("_Metallic"); hasExplicitMetallic = true; }
            else if (material.HasProperty("_Metalic")) { metallic = material.GetFloat("_Metalic"); hasExplicitMetallic = true; }

            float smoothness = 0.5f;
            bool hasSmoothness = false;
            if (material.HasProperty("_BaseSmooth")) { smoothness = material.GetFloat("_BaseSmooth"); hasSmoothness = true; }
            else if (material.HasProperty("_Smoothness")) { smoothness = material.GetFloat("_Smoothness"); hasSmoothness = true; }
            else if (material.HasProperty("_Smooth")) { smoothness = material.GetFloat("_Smooth"); hasSmoothness = true; }
            else if (material.HasProperty("_Glossiness")) { smoothness = material.GetFloat("_Glossiness"); hasSmoothness = true; }
            else if (material.HasProperty("_GlossMapScale")) { smoothness = material.GetFloat("_GlossMapScale"); hasSmoothness = true; }

            float roughness;
            bool hasExplicitRoughness = false;
            if (material.HasProperty("_Roughness")) { roughness = material.GetFloat("_Roughness"); hasExplicitRoughness = true; }
            else if (material.HasProperty("_Rough")) { roughness = material.GetFloat("_Rough"); hasExplicitRoughness = true; }
            else if (hasSmoothness) { roughness = 1.0f - Mathf.Clamp01(smoothness); hasExplicitRoughness = true; }
            else roughness = 0.7f;

            matData.baseMetallic = Mathf.Clamp01(metallic);
            matData.baseRoughness = Mathf.Clamp(roughness, 0.04f, 1.0f);
            matData.hasInferredPbr = false;
            matData.inferredMetallic = matData.baseMetallic;
            matData.inferredRoughness = matData.baseRoughness;

            // Auto PBR Heuristics (Experimental): infer physical metallic & roughness for non-PBR surfaces
            if (!hasExplicitMetallic || !hasExplicitRoughness)
            {
                if (InferPbrProperties(material, tex, renderer, shaderName, out float infMetal, out float infRough, out string matchedCat))
                {
                    matData.hasInferredPbr = true;
                    matData.inferredMetallic = Mathf.Clamp01(!hasExplicitMetallic ? infMetal : metallic);
                    matData.inferredRoughness = Mathf.Clamp(!hasExplicitRoughness ? infRough : roughness, 0.04f, 1.0f);
                    if (ConfigAutoPBR != null && ConfigAutoPBR.Value)
                    {
                        if (!hasExplicitMetallic) metallic = infMetal;
                        if (!hasExplicitRoughness) roughness = infRough;
                        autoPbrMaterialCount++;
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"[AutoPBR] Inferred '{material.name}': cat={matchedCat} metal={metallic:F2} rough={roughness:F2}");
                    }
                }
            }

            matData.metallic = Mathf.Clamp01(metallic);
            matData.roughness = Mathf.Clamp(roughness, 0.04f, 1.0f);
            
            // Detect alpha mode from shader keywords, _Mode property, and render queue
            var (detectedMode, detectionReason) = DetectAlphaModeWithReason(material);
            matData.alphaMode = detectedMode;
            if (material.HasProperty("_Cutoff"))
            {
                matData.alphaCutoff = material.GetFloat("_Cutoff");
            }
            if (matData.suppressLightGeneration && !shaderName.Contains("TextMeshPro"))
            {
                matData.alphaMode = AlphaMode.Blend;
            }
            // A layered material's _Shape alpha cuts out its leaves and blades when it alpha tests.
            if (layeredComposite && material.HasProperty("_Shape") && material.GetTexture("_Shape") != null &&
                (material.IsKeywordEnabled("_ALPHATEST_ON") || (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0f)))
            {
                matData.alphaMode = AlphaMode.Cutout;
                matData.alphaCutoff = material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0f ? material.GetFloat("_AlphaClip") : 0.5f;
            }

            // Detect translucent kind (Water vs Glass vs None) for OpenRemix
            var (transKind, ior, dist, transColor, transReason) = DetectTranslucentKind(
                material, material.name, shaderName, renderer, matData.alphaMode, matData.albedoColor, matData.roughness, matData.suppressLightGeneration);
            matData.translucentKind = transKind;
            matData.refractiveIndex = ior;
            matData.transmittanceDistance = dist;
            matData.transmittanceColor = transColor;
            
            // Detailed diagnostic log for every material
            if (verboseTextureLogging.Value)
            {
                string transStr = transKind != TranslucentKind.None ? $" transKind={transKind}({transReason},IOR={ior:F2},dist={dist:F1}m)" : "";
                logger.LogInfo($"[MaterialDiag] '{material.name}' shader='{material.shader?.name}' queue={material.renderQueue} " +
                    $"alphaMode={matData.alphaMode} reason={detectionReason}{transStr} " +
                    $"color=({matData.albedoColor.r:F3},{matData.albedoColor.g:F3},{matData.albedoColor.b:F3},{matData.albedoColor.a:F3}) " +
                    $"metallic={matData.metallic:F2} roughness={matData.roughness:F2} cutoff={matData.alphaCutoff:F3}");
            }

            // Upload albedo texture (with pre-tinting and remapping)
            albedoTex = tex;
            if (captureTextures.Value && tex != null)
            {
                // If a valid diffuse texture exists but captured color is pure black (0,0,0)
                // due to an uninitialized/unused property in ShaderGraph, default to white so the texture isn't crushed
                if (matData.albedoColor.r == 0f && matData.albedoColor.g == 0f && matData.albedoColor.b == 0f)
                {
                    matData.albedoColor = Color.white;
                }

                // Read Unity wrap/filter modes from texture and convert to MDL values
                matData.wrapModeU = UnityWrapToMdl(tex.wrapModeU);
                matData.wrapModeV = UnityWrapToMdl(tex.wrapModeV);
                matData.filterMode = (byte)(tex.filterMode == FilterMode.Point ? 0 : 1);
                
                int texId = tex.GetInstanceID();

                // Check for stylized texture value remapping curves (_Remap1, _Remap2, _Remap3)
                Vector2? remap = null;
                if (matchedTexProp == "_Texture1" && material.HasProperty("_Remap1"))
                {
                    Vector4 r = material.GetVector("_Remap1");
                    if (r.y > r.x && (r.x > 0.01f || r.y < 0.99f)) remap = new Vector2(r.x, r.y);
                }
                else if (matchedTexProp == "_Texture2" && material.HasProperty("_Remap2"))
                {
                    Vector4 r = material.GetVector("_Remap2");
                    if (r.y > r.x && (r.x > 0.01f || r.y < 0.99f)) remap = new Vector2(r.x, r.y);
                }
                else if (matchedTexProp == "_Texture3" && material.HasProperty("_Remap3"))
                {
                    Vector4 r = material.GetVector("_Remap3");
                    if (r.y > r.x && (r.x > 0.01f || r.y < 0.99f)) remap = new Vector2(r.x, r.y);
                }

                Color effectiveTint = mpbColor ?? matData.albedoColor;
                bool isTinted = effectiveTint.r < 0.98f || effectiveTint.g < 0.98f || effectiveTint.b < 0.98f;
                if (matData.suppressLightGeneration && !shaderName.Contains("TextMeshPro"))
                {
                    // Procedural particle shaders: the material drawn by Unity, not its noise texture.
                    var snapshot = renderer is ParticleSystemRenderer ? GetParticleSnapshot(material) : null;
                    if (snapshot != null)
                    {
                        tex = snapshot;
                        albedoTex = snapshot;
                        matData.wrapModeU = matData.wrapModeV = 0; // MDL Clamp
                        matData.mainTexST = new Vector4(1f, 1f, 0f, 0f);
                    }
                    var (partHandle, partHash) = UploadParticleTexture(tex, material, effectiveTint);
                    matData.albedoHandle = partHandle;
                    matData.albedoTextureHash = partHash;
                    if (RemixAPI.IsOpenRemix && isTinted)
                    {
                        Color linTint = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? effectiveTint : effectiveTint.linear;
                        matData.albedoColor = new Color(linTint.r, linTint.g, linTint.b, matData.albedoColor.a);
                    }
                    else
                    {
                        matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                    }
                }
                else if (remap.HasValue || hsbc.HasValue || (!RemixAPI.IsOpenRemix && isTinted))
                {
                    var (tintedHandle, tintedHash) = UploadTintedAlbedoTexture(tex, effectiveTint, remap, hsbc);
                    matData.albedoHandle = tintedHandle;
                    matData.albedoTextureHash = tintedHash;
                    matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                }
                else
                {
                    matData.albedoHandle = UploadUnityTexture(tex);
                    if (matData.albedoHandle != IntPtr.Zero)
                    {
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.albedoTextureHash = hash;
                        }
                    }
                    if (RemixAPI.IsOpenRemix && isTinted)
                    {
                        // OpenRemix natively multiplies s.tint (opaqueExt.albedoConstant) in linear space in scene.glsl
                        Color linTint = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? effectiveTint : effectiveTint.linear;
                        matData.albedoColor = new Color(linTint.r, linTint.g, linTint.b, matData.albedoColor.a);
                    }
                    else
                    {
                        matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                    }
                }
                
                if (matData.albedoHandle != IntPtr.Zero)
                {
                    // Fallback: if shader metadata says Opaque but the texture has genuine cutout
                    // transparency (large near-zero alpha regions), upgrade to Cutout.
                    // Uses texturesWithCutoutAlpha (strict: >=10% pixels at alpha<16) instead of
                    // texturesWithAlpha (loose: any pixel<250) to avoid false positives from
                    // smoothness-as-alpha in Standard shader Opaque mode.
                    if (matData.alphaMode == AlphaMode.Opaque && texturesWithCutoutAlpha.Contains(texId)
                        && (shaderName == null || shaderName.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        matData.alphaMode = AlphaMode.Cutout;
                        if (!material.HasProperty("_Cutoff"))
                            matData.alphaCutoff = 0.5f;
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"[AlphaFallback] '{material.name}': texture has alpha content, upgrading Opaque -> Cutout (cutoff={matData.alphaCutoff:F2})");
                    }
                }
            }
            
            // Capture texture tiling/offset safely without throwing on missing _MainTex
            if (albedoTex != null && particleSnapshots.ContainsValue(albedoTex))
            {
                // a particle snapshot covers the quad exactly
            }
            else if (matchedTexProp != null)
            {
                try
                {
                    Vector2 scale = material.GetTextureScale(matchedTexProp);
                    Vector2 offset = material.GetTextureOffset(matchedTexProp);
                    matData.mainTexST = new Vector4(scale.x, scale.y, offset.x, offset.y);
                }
                catch
                {
                    if (material.HasProperty("_MainTex"))
                    {
                        try
                        {
                            matData.mainTexST = new Vector4(
                                material.mainTextureScale.x,
                                material.mainTextureScale.y,
                                material.mainTextureOffset.x,
                                material.mainTextureOffset.y);
                        }
                        catch { }
                    }
                }
            }
            else if (material.HasProperty("_MainTex"))
            {
                try
                {
                    matData.mainTexST = new Vector4(
                        material.mainTextureScale.x,
                        material.mainTextureScale.y,
                        material.mainTextureOffset.x,
                        material.mainTextureOffset.y);
                }
                catch { }
            }

            // Fallback: no albedo texture but material has a color — create a 1x1 solid-color texture
            // so Remix renders the surface with the correct color instead of the debug checkerboard.
            if (matData.albedoHandle == IntPtr.Zero && (mpbColor.HasValue || material.HasProperty("_Color") || material.HasProperty("_BaseColor") || material.HasProperty("_Tint") || material.HasProperty("_TintColor") || material.HasProperty("_Color1") || hsbc.HasValue))
            {
                if (hsbc.HasValue)
                {
                    matData.albedoColor = ApplyHsbcToColor(matData.albedoColor, hsbc.Value);
                }
                matData.albedoHandle = GetOrCreateSolidColorTexture(matData.albedoColor);
                if (matData.albedoHandle != IntPtr.Zero)
                {
                    ulong h = (ulong)matData.albedoHandle.ToInt64();
                    matData.albedoTextureHash = h;
                }
            }
            
            // Dump shader properties once per shader to discover emission/color property names (via reflection for pre-2019 compatibility)
            if (verboseTextureLogging.Value && material.shader != null && !loggedShaderProperties.Contains(shaderName))
            {
                loggedShaderProperties.Add(shaderName);
                DumpShaderProperties(material.shader, shaderName);
            }
            
            // Upload emission — three shader paths:
            // 1. Standard/URP: _EMISSION keyword gates emission; _EmissionColor + _EmissionMap
            // 2. ULTRAKILL/Master: _EmissiveColor + _EmissiveTex + _EmissiveIntensity + EMISSIVE toggle
            // 3. Generic custom: _EmissionColor + _EmissionMultiplier (e.g. Dark Machine/SHDR_Base)
            bool hasEmission = false;
            if (captureTextures.Value)
            {
                // Standard shader path — require _EMISSION keyword or an actual _EmissionMap texture.
                // HasProperty("_EmissionColor") alone is NOT sufficient: most shaders define it
                // even when emission is disabled, often with a default white value that would
                // cause every surface to glow.
                bool hasActiveEmissionMap = material.HasProperty("_EmissionMap") && material.GetTexture("_EmissionMap") != null;
                if (material.IsKeywordEnabled("_EMISSION") || hasActiveEmissionMap)
                {
                    if (material.HasProperty("_EmissionColor"))
                    {
                        matData.emissiveColor = material.GetColor("_EmissionColor");
                        float maxChannel = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                        // Store 1.0 as intensity — CreateRemixMaterialSimple already folds maxChannel into
                        // emCombinedIntensity (= maxCh * emissiveIntensity), so storing maxChannel here
                        // would double it. The HDR brightness lives in the color, not the intensity.
                        matData.emissiveIntensity = maxChannel > 0f ? 1.0f : 0f;
                    }
                    if (hasActiveEmissionMap)
                    {
                        var emTex = material.GetTexture("_EmissionMap") as Texture2D;
                        if (emTex != null)
                        {
                            var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                            matData.emissiveHandle = emHandle;
                            matData.emissiveTextureHash = emHash;
                        }
                    }
                    hasEmission = matData.emissiveIntensity > 0f || matData.emissiveHandle != IntPtr.Zero;
                }
                
                // Custom shader path: _EmissiveColor, _EmissiveTex, _EmissiveIntensity, or MPB override
                bool hasMpbOverride = mpbEmissiveColor.HasValue || mpbColor.HasValue;
                if (!hasEmission && (material.HasProperty("_EmissiveColor") || hasMpbOverride))
                {
                    bool emissiveToggle = true;
                    if (material.HasProperty("EMISSIVE"))
                        emissiveToggle = material.GetFloat("EMISSIVE") > 0.5f;
                    
                    // A dedicated _EmissiveTex overrides the toggle (artist assigned a glow map)
                    bool hasEmissiveTex = false;
                    if (material.HasProperty("_EmissiveTex"))
                        hasEmissiveTex = material.GetTexture("_EmissiveTex") != null;
                    
                    if (emissiveToggle || hasEmissiveTex || hasMpbOverride)
                    {
                        Color defaultCol = material.HasProperty("_EmissiveColor") ? material.GetColor("_EmissiveColor") : Color.black;
                        matData.emissiveColor = mpbEmissiveColor ?? (mpbColor.HasValue ? mpbColor.Value : defaultCol);
                        
                        if (mpbEmissiveIntensity.HasValue)
                            matData.emissiveIntensity = mpbEmissiveIntensity.Value;
                        else if (material.HasProperty("_EmissiveIntensity"))
                            matData.emissiveIntensity = material.GetFloat("_EmissiveIntensity");
                        else
                        {
                            float maxCh = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                            matData.emissiveIntensity = maxCh > 0f ? maxCh : 1.0f;
                        }
                        
                        // Upload _EmissiveTex if present, pre-tinted by emission color
                        if (hasEmissiveTex)
                        {
                            var emTex = material.GetTexture("_EmissiveTex") as Texture2D;
                            if (emTex != null)
                            {
                                var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                                matData.emissiveHandle = emHandle;
                                matData.emissiveTextureHash = emHash;
                            }
                        }
                        
                        // _UseAlbedoAsEmissive when toggle is on or when MPB display override is present
                        if ((emissiveToggle || hasMpbOverride) && matData.emissiveHandle == IntPtr.Zero
                            && ((material.HasProperty("_UseAlbedoAsEmissive") && material.GetFloat("_UseAlbedoAsEmissive") > 0.5f) || hasMpbOverride)
                            && matData.albedoHandle != IntPtr.Zero)
                        {
                            if (matData.suppressLightGeneration && RemixAPI.IsOpenRemix)
                            {
                                // Reuse the de-fringed particle albedo texture directly; OpenRemix multiplies emissiveColorConstant in the shader
                                matData.emissiveHandle = matData.albedoHandle;
                                matData.emissiveTextureHash = matData.albedoTextureHash;
                            }
                            else if (albedoTex != null)
                            {
                                var (emHandle, emHash) = UploadTintedEmissiveTexture(albedoTex, matData.emissiveColor);
                                matData.emissiveHandle = emHandle;
                                matData.emissiveTextureHash = emHash;
                            }
                            else
                            {
                                matData.emissiveHandle = matData.albedoHandle;
                                matData.emissiveTextureHash = matData.albedoTextureHash;
                            }
                        }
                        
                        hasEmission = matData.emissiveIntensity > 0f && matData.emissiveHandle != IntPtr.Zero;
                    }
                }
                
                // Generic custom shader path: _EmissionMultiplier + _Emission texture (e.g. Dark Machine/SHDR_Base)
                // Requires an actual texture — the multiplier alone with default _EmissionColor is not
                // a reliable gate since many shaders default _EmissionMultiplier to 1.0.
                if (!hasEmission && material.HasProperty("_EmissionMultiplier") && material.HasProperty("_Emission"))
                {
                    var emTex = material.GetTexture("_Emission") as Texture2D;
                    float multiplier = material.GetFloat("_EmissionMultiplier");
                    if (emTex != null && multiplier > 0f)
                    {
                        matData.emissiveColor = material.HasProperty("_EmissionColor")
                            ? material.GetColor("_EmissionColor")
                            : Color.white;
                        matData.emissiveIntensity = multiplier;
                        
                        var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                        matData.emissiveHandle = emHandle;
                        matData.emissiveTextureHash = emHash;
                        
                        hasEmission = true;
                    }
                }
                
                // Stanley Parable custom emissive shaders: emission texture in _TextureSample1 or _TextureSample2,
                // intensity in _Emission (Range), with optional _Useemissionmaponly toggle.
                // These shaders have "Emissive" in the name but use non-standard property names.
                if (!hasEmission && shaderName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Find emission texture: _TextureSample1, _TextureSample2, or fall back to albedo
                    Texture2D stanleyEmTex = null;
                    string emTexProp = null;
                    foreach (var prop in new[] { "_TextureSample1", "_TextureSample2" })
                    {
                        if (material.HasProperty(prop))
                        {
                            stanleyEmTex = material.GetTexture(prop) as Texture2D;
                            if (stanleyEmTex != null) { emTexProp = prop; break; }
                        }
                    }
                    
                    // Read intensity from _Emission (Range) if present, default to 1.0
                    float emIntensity = 1.0f;
                    if (material.HasProperty("_Emission"))
                        emIntensity = material.GetFloat("_Emission");
                    
                    if (emIntensity > 0f)
                    {
                        matData.emissiveColor = Color.white;
                        matData.emissiveIntensity = emIntensity;
                        
                        if (stanleyEmTex != null)
                        {
                            matData.emissiveHandle = UploadUnityTexture(stanleyEmTex);
                            if (matData.emissiveHandle != IntPtr.Zero)
                            {
                                int emTexId = stanleyEmTex.GetInstanceID();
                                if (textureHashCache.TryGetValue(emTexId, out ulong emHash))
                                    matData.emissiveTextureHash = emHash;
                            }
                        }
                        else if (albedoTex != null)
                        {
                            // No dedicated emission texture — use albedo as emission source
                            matData.emissiveHandle = matData.albedoHandle;
                            matData.emissiveTextureHash = matData.albedoTextureHash;
                        }
                        
                        hasEmission = matData.emissiveHandle != IntPtr.Zero;
                        if (hasEmission && verboseTextureLogging.Value)
                            logger.LogInfo($"[StanleyEmission] '{material.name}': tex={emTexProp ?? "albedo"} intensity={emIntensity:F3}");
                    }
                }
                
                // TextMeshPro Distance Field: self-lit UI text — use _FaceColor as emissive tint.
                // SDF atlases are typically Alpha8 format: after GPU readback the glyph shapes
                // live in the alpha channel while RGB is black. We must convert alpha → RGB
                // so Remix sees bright glyph pixels in the emissive texture.
                // The albedo from Alpha8 readback is (0,0,0,A), so reflected light contributes
                // nothing — emission is the only light source for these glyphs. Use a high
                // intensity so text appears clearly self-lit in the path-traced scene.
                if (!hasEmission && shaderName.Contains("TextMeshPro/Distance Field"))
                {
                    Color faceColor = material.HasProperty("_FaceColor")
                        ? material.GetColor("_FaceColor")
                        : Color.white;
                    float maxCh = Mathf.Max(faceColor.r, Mathf.Max(faceColor.g, faceColor.b));
                    if (maxCh > 0f && albedoTex != null)
                    {
                        var (emHandle, emHash) = UploadSDFEmissiveTexture(albedoTex, faceColor);
                        if (emHandle != IntPtr.Zero)
                        {
                            matData.emissiveColor = faceColor;
                            matData.emissiveIntensity = 20.0f;
                            matData.emissiveHandle = emHandle;
                            matData.emissiveTextureHash = emHash;
                            hasEmission = true;
                            
                            // Also replace albedo with the alpha→RGB texture so glyphs
                            // are directly visible, not just in reflections. The original
                            // Alpha8 readback produces (0,0,0,A) which renders as
                            // transparent black in direct view.
                            matData.albedoHandle = emHandle;
                            matData.albedoTextureHash = emHash;
                            matData.useEmissiveBlend = true;
                        }
                    }
                }
                
                // Safety net: if no path confirmed real emission, ensure emissive state is clean.
                // Prevents stale values from a path that read properties but decided not to emit.
                if (!hasEmission)
                {
                    matData.emissiveColor = Color.black;
                    matData.emissiveIntensity = 0f;
                    matData.emissiveHandle = IntPtr.Zero;
                    matData.emissiveTextureHash = 0;
                }
                else if (verboseTextureLogging.Value)
                {
                    logger.LogInfo($"[Emission] '{material.name}': color=({matData.emissiveColor.r:F3},{matData.emissiveColor.g:F3},{matData.emissiveColor.b:F3}) " +
                        $"intensity={matData.emissiveIntensity:F3} texHash=0x{matData.emissiveTextureHash:X16}");
                }
            }
            
            // Upload normal map
            if (captureTextures.Value)
            {
                for (int p = 0; p < NormalTextureProps.Length; p++)
                {
                    string prop = NormalTextureProps[p];
                    if (material.HasProperty(prop))
                    {
                        var normTex = material.GetTexture(prop) as Texture2D;
                        if (normTex != null)
                        {
                            matData.normalHandle = UploadUnityTexture(normTex, isNormalMap: true);
                            if (matData.normalHandle != IntPtr.Zero)
                            {
                                int texId = normTex.GetInstanceID();
                                if (textureHashCache.TryGetValue(texId, out ulong hash))
                                {
                                    matData.normalTextureHash = hash;
                                }
                                if (verboseTextureLogging.Value)
                                    logger.LogInfo($"Captured normal texture for material '{material.name}' using '{prop}' (hash: 0x{matData.normalTextureHash:X16})");
                            }
                            break;
                        }
                    }
                }

                matData.originalNormalHandle = matData.normalHandle;
                matData.originalNormalTextureHash = matData.normalTextureHash;
                matData.albedoTex = albedoTex;

                // Auto Normal Map Generation (Experimental): Sobel height filter on albedo texture
                if (matData.normalHandle == IntPtr.Zero && albedoTex != null
                    && ConfigAutoNormalMaps != null && ConfigAutoNormalMaps.Value)
                {
                    float strength = ConfigAutoNormalStrength != null ? ConfigAutoNormalStrength.Value : 1.0f;
                    var (genHandle, genHash) = GetOrCreateAutoNormalMap(albedoTex, strength);
                    if (genHandle != IntPtr.Zero)
                    {
                        matData.normalHandle = genHandle;
                        matData.normalTextureHash = genHash;
                        autoNormalMapCount++;
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"[AutoNormal] Generated normal map for '{material.name}' from '{albedoTex.name}' (hash: 0x{genHash:X16})");
                    }
                }
            }
            
            // Store for later use
            materialTextureData[materialId] = matData;

            // The first materials of each shader, described once: what the shader offers and what Remix got.
            if (material.shader != null)
            {
                describedShaderMaterials.TryGetValue(shaderName, out int described);
                if (described < 2)
                {
                    describedShaderMaterials[shaderName] = described + 1;
                    try
                    {
                        TryDescribeMaterial(materialId, out string result);
                        logger.LogInfo($"[MaterialInfo] '{material.name}' shader '{shaderName}' queue {material.renderQueue}, albedo from {matchedTexProp ?? "none"} | " +
                                       $"{DescribeMaterialProperties(material)} | Remix {result}");
                    }
                    catch (Exception) { } // Unity without Shader.GetPropertyCount (before 2019.3)
                }
            }

            // DISABLED: Async material creation causes deadlocks with Remix API
            // Materials are now created on-demand on the render thread during mesh creation
            // This prevents deadlocks between material thread and render thread competing for Remix device lock
            
            if (verboseTextureLogging != null && verboseTextureLogging.Value)
            {
                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                logger.LogInfo($"[MatCapture] '{material.name}' shader='{material.shader?.name}' albedo={albedoPath ?? "NONE"} normal={normalPath ?? "none"}");
            }
        }
        
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
        public IntPtr UploadUnityTexture(Texture2D unityTexture, bool isNormalMap = false)
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
                uint actualMipLevels = (uint)unityTexture.mipmapCount;
                
                // DXT5nm normal maps pack X in alpha and Y in green. Raw DXT5 upload
                // would pass the packed channels to Remix unchanged, so we must decompress
                // and unswizzle. Readable textures use GetPixels32 (CPU); non-readable use GPU readback.
                bool isDXT5nm = isNormalMap &&
                    (unityTexture.format == TextureFormat.DXT5 || unityTexture.format == TextureFormat.DXT5Crunched);
                
                // Readable DXT5nm: decompress on CPU to avoid GPU stall
                if (isDXT5nm && unityTexture.isReadable)
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
                else if (!unityTexture.isReadable)
                {
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"Texture '{unityTexture.name}' is not readable - forcing GPU readback");
                    
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        unityTexture.width, unityTexture.height, 0,
                        RenderTextureFormat.ARGB32, isNormalMap ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                    
                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(unityTexture, tmp);
                    RenderTexture.active = tmp;
                    
                    Texture2D readableTexture = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, isNormalMap);
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
                                RenderTextureFormat.ARGB32, isNormalMap ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                            RenderTexture prevActive = RenderTexture.active;
                            Graphics.Blit(unityTexture, tmpGpu);
                            RenderTexture.active = tmpGpu;
                            Texture2D convTex = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, isNormalMap);
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
        /// Upload an emissive texture pre-tinted by the emission color.
        /// Remix's shader ignores emissiveColorConstant when a texture is present,
        /// so we bake the color multiplication into the texture pixels.
        /// Returns (handle, hash) for the uploaded texture.
        /// </summary>
        private (IntPtr handle, ulong hash) UploadTintedEmissiveTexture(Texture2D tex, Color emissiveColor)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);
            
            // OpenRemix natively multiplies emissiveTexture by emissiveColorConstant * emissiveIntensity on the GPU (scene.glsl:235).
            // Pre-tinting on the CPU causes a synchronous GPU readback stall, duplicates textures per tint, and double-tints the emission color.
            if (RemixAPI.IsOpenRemix)
            {
                var handle = UploadUnityTexture(tex);
                int texId = tex.GetInstanceID();
                textureHashCache.TryGetValue(texId, out ulong h);
                return (handle, h);
            }

            // Compute normalized tint direction (all components <= 1.0)
            float maxCh = Mathf.Max(emissiveColor.r, Mathf.Max(emissiveColor.g, emissiveColor.b));
            float tintR = maxCh > 0f ? emissiveColor.r / maxCh : 1f;
            float tintG = maxCh > 0f ? emissiveColor.g / maxCh : 1f;
            float tintB = maxCh > 0f ? emissiveColor.b / maxCh : 1f;
            
            // If tint is white, use normal upload path (benefits from per-texId caching)
            if (tintR > 0.99f && tintG > 0.99f && tintB > 0.99f)
            {
                var handle = UploadUnityTexture(tex);
                int texId = tex.GetInstanceID();
                textureHashCache.TryGetValue(texId, out ulong h);
                return (handle, h);
            }
            
            // Compound cache key: texture ID + quantized tint color
            int tintKey = ((int)(tintR * 255) << 16) | ((int)(tintG * 255) << 8) | (int)(tintB * 255);
            long cacheKey = ((long)tex.GetInstanceID() << 24) ^ (uint)tintKey;
            
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
                if (tex.isReadable)
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
                
                // Tint each pixel by the normalized emission color direction
                byte tR = (byte)(tintR * 255f);
                byte tG = (byte)(tintG * 255f);
                byte tB = (byte)(tintB * 255f);
                
                byte[] pixelData = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixelData[i * 4 + 0] = (byte)((pixels[i].r * tR) / 255);
                    pixelData[i * 4 + 1] = (byte)((pixels[i].g * tG) / 255);
                    pixelData[i * 4 + 2] = (byte)((pixels[i].b * tB) / 255);
                    pixelData[i * 4 + 3] = pixels[i].a;
                }
                
                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;
                
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed tinted emissive hash for '{tex.name}' tint=({tintR:F2},{tintG:F2},{tintB:F2}): 0x{hash:X16}");
                
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
                logger.LogError($"Exception uploading tinted emissive '{tex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }

        /// <summary>
        /// Upload a particle/trail sprite texture with automatic black-background alpha extraction
        /// and premultiplied/dark-matte edge de-fringing so transparent particles never render with black outlines.
        /// Cached per Texture2D in OpenRemix (where tint is applied in scene.glsl).
        /// </summary>
        private (IntPtr handle, ulong hash) UploadParticleTexture(Texture2D tex, Material mat, Color albedoColor)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);

            bool applyCpuTint = !RemixAPI.IsOpenRemix && (albedoColor.r < 0.98f || albedoColor.g < 0.98f || albedoColor.b < 0.98f);
            Color gammaColor = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? albedoColor.gamma : albedoColor;
            float tintR = applyCpuTint ? Mathf.Clamp01(gammaColor.r) : 1f;
            float tintG = applyCpuTint ? Mathf.Clamp01(gammaColor.g) : 1f;
            float tintB = applyCpuTint ? Mathf.Clamp01(gammaColor.b) : 1f;
            int tintKey = applyCpuTint ? (((int)(tintR * 255f) << 16) | ((int)(tintG * 255f) << 8) | (int)(tintB * 255f)) : 0;

            bool isAdditiveShader = false;
            if (mat != null)
            {
                string sName = mat.shader != null ? mat.shader.name : "";
                string mName = mat.name ?? "";
                if (sName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (mat.HasProperty("_DstBlend") && Mathf.RoundToInt(mat.GetFloat("_DstBlend")) == 1))
                {
                    isAdditiveShader = true;
                }
            }

            long cacheKey = unchecked((long)0x5000000000000000L ^ ((long)tex.GetInstanceID() << 28) ^ ((isAdditiveShader ? 1L : 0L) << 24) ^ (uint)tintKey);

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
                if (tex.isReadable)
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
                bool treatAsBlackBgAdditive = isAdditiveShader || (hasNoAlphaChannel && hasDarkBorder);

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

                    if (treatAsBlackBgAdditive)
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
                        // Standard alpha-channel particle sprite: bleed average sprite RGB into near-zero-alpha
                        // or pitch-black border pixels so bilinear filtering doesn't produce black outlines,
                        // while preserving the exact authored RGB and alpha of subtle smoke/steam/dust textures.
                        if (a <= 4 || (a < 64 && maxCh <= 6))
                        {
                            r = avgR;
                            g = avgG;
                            b = avgB;
                            a = 0;
                        }
                        else if (maxCh <= 6)
                        {
                            r = avgR;
                            g = avgG;
                            b = avgB;
                        }
                    }

                    if (applyCpuTint)
                    {
                        r = (byte)Mathf.Clamp(Mathf.RoundToInt(r * tintR), 0, 255);
                        g = (byte)Mathf.Clamp(Mathf.RoundToInt(g * tintG), 0, 255);
                        b = (byte)Mathf.Clamp(Mathf.RoundToInt(b * tintB), 0, 255);
                    }

                    pixelData[i * 4 + 0] = r;
                    pixelData[i * 4 + 1] = g;
                    pixelData[i * 4 + 2] = b;
                    pixelData[i * 4 + 3] = a;
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
        private (IntPtr handle, ulong hash) UploadTintedAlbedoTexture(Texture2D tex, Color albedoColor, Vector2? remap, Vector4? hsbc = null)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);

            // If color is white, no remap, and no hsbc, fast path to standard upload
            if (albedoColor.r >= 0.98f && albedoColor.g >= 0.98f && albedoColor.b >= 0.98f && !remap.HasValue && !hsbc.HasValue)
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
                if (tex.isReadable)
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
                        b = lum + (g - lum) * hsbcS;
                    }

                    if (hasTint)
                    {
                        r *= tintR;
                        g *= tintG;
                        b *= tintB;
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
                if (tex.isReadable)
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
                    byte lum = (byte)Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, Mathf.Max(pixels[i].b, pixels[i].a)));
                    pixelData[i * 4 + 0] = (byte)((lum * tR) / 255);
                    pixelData[i * 4 + 1] = (byte)((lum * tG) / 255);
                    pixelData[i * 4 + 2] = (byte)((lum * tB) / 255);
                    pixelData[i * 4 + 3] = lum; // alpha = glyph shape for blending
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
        
        /// <summary>
        /// Generate material hash
        /// </summary>
        public ulong GenerateMaterialHash(MaterialTextureData matData)
        {
            ulong hash = 14695981039346656037UL;
            
            string cleanName = matData.materialName;
            if (!string.IsNullOrEmpty(cleanName))
            {
                // Remove dynamic Unity tags
                cleanName = cleanName.Replace(" (Instance)", "").Replace(" Instance", "").Replace("(Clone)", "").Trim();
                
                foreach (char c in cleanName)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }

            // Factor in albedo texture hash so distinct texture assets never collide
            if (matData.albedoHandle != IntPtr.Zero)
            {
                hash ^= (ulong)matData.albedoHandle.ToInt64();
                hash *= 1099511628211UL;
            }

            // Factor in normal texture hash so variants with/without normal maps never collide
            if (matData.normalHandle != IntPtr.Zero)
            {
                hash ^= (ulong)matData.normalHandle.ToInt64();
                hash *= 1099511628211UL;
            }

            if (matData.emissiveHandle != IntPtr.Zero)
            {
                hash ^= (ulong)matData.emissiveHandle.ToInt64();
                hash *= 1099511628211UL;
            }

            // Incorporate albedo color / tint (used by OpenRemix GPU s.tint multiplication)
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.albedoColor.r);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.albedoColor.g);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.albedoColor.b);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.albedoColor.a);
            hash *= 1099511628211UL;
            
            // Incorporate emissive color and intensity directly since they're used for per-instance overrides
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.emissiveColor.r);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.emissiveColor.g);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.emissiveColor.b);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.emissiveIntensity);
            hash *= 1099511628211UL;
            if (matData.suppressLightGeneration)
            {
                hash ^= 0x55AA55AAUL;
                hash *= 1099511628211UL;
            }
            // Incorporate alpha mode so cutout materials never collide with opaque materials
            hash ^= (ulong)matData.alphaMode;
            hash *= 1099511628211UL;
            
            // Factor in metallic and roughness so distinct PBR variants have distinct hashes
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.metallic);
            hash *= 1099511628211UL;
            hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.roughness);
            hash *= 1099511628211UL;

            // Incorporate translucent kind and refractive index so translucent materials never collide with opaque materials
            if (matData.translucentKind != TranslucentKind.None)
            {
                hash ^= (ulong)matData.translucentKind * 7919UL;
                hash *= 1099511628211UL;
                hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.refractiveIndex);
                hash *= 1099511628211UL;
                hash ^= (ulong)BitConverter.DoubleToInt64Bits(matData.transmittanceDistance);
                hash *= 1099511628211UL;
            }
            
            if (hash == 0) hash = 1;
            return hash;
        }
        
        /// <summary>
        /// Start async material creation thread
        /// </summary>
        private void StartMaterialCreationThread()
        {
            if (materialCreationThreadRunning)
                return;
                
            materialCreationThreadRunning = true;
            materialCreationThread = new Thread(MaterialCreationThreadFunc);
            materialCreationThread.IsBackground = true;
            materialCreationThread.Start();
            logger.LogInfo("Started async material creation thread");
        }
        
        private void MaterialCreationThreadFunc()
        {
            logger.LogInfo("Material creation thread running");
            
            while (materialCreationThreadRunning)
            {
                int materialId = -1;
                
                lock (pendingMaterialCreation)
                {
                    if (pendingMaterialCreation.Count > 0)
                    {
                        materialId = pendingMaterialCreation.Dequeue();
                        pendingMaterialSet.Remove(materialId);
                    }
                }
                
                if (materialId != -1 && materialTextureData.ContainsKey(materialId))
                {
                    Thread.Sleep(200); // Wait for texture upload to complete
                    
                    var matData = materialTextureData[materialId];
                    IntPtr handle = CreateRemixMaterialSimple(ref matData, materialId);
                    
                    if (handle != IntPtr.Zero)
                    {
                        matData.remixMaterialHandle = handle;
                        materialTextureData[materialId] = matData;
                    }
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
            
            logger.LogInfo("Material creation thread stopped");
        }
        
        /// <summary>
        /// Convert Unity TextureWrapMode to MDL WrapMode (0=Clamp, 1=Repeat, 2=MirroredRepeat, 3=Clip).
        /// </summary>
        private static byte UnityWrapToMdl(TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat: return 1;
                case TextureWrapMode.Clamp:  return 0;
                case TextureWrapMode.Mirror: return 2;
                default:                     return 1; // MirrorOnce and others → Repeat
            }
        }
        
        /// <summary>
        /// Sample an uncompressed RGBA pixel buffer to check if the alpha channel has non-trivial content.
        /// Checks a sparse grid of pixels for performance (avoids scanning entire textures).
        /// </summary>
        private static bool SampleAlphaRGBA(byte[] pixelData, int alphaOffset, int stride)
        {
            int pixelCount = pixelData.Length / stride;
            if (pixelCount == 0) return false;
            
            // Sample up to 256 evenly-spaced pixels
            int step = Math.Max(1, pixelCount / 256);
            for (int i = 0; i < pixelCount; i += step)
            {
                int idx = i * stride + alphaOffset;
                if (idx < pixelData.Length && pixelData[idx] < 250)
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if uncompressed RGBA pixels have genuine cutout transparency: a significant
        /// fraction of pixels with near-zero alpha (< 16). This distinguishes real transparency
        /// (decal backgrounds, cutout shapes) from smoothness-as-alpha packing where values
        /// are typically 50-230.
        /// </summary>
        private static bool SampleCutoutAlphaRGBA(byte[] pixelData, int alphaOffset, int stride)
        {
            int pixelCount = pixelData.Length / stride;
            if (pixelCount == 0) return false;
            
            int sampleCount = Math.Min(pixelCount, 1024);
            int step = Math.Max(1, pixelCount / sampleCount);
            int nearZeroCount = 0;
            int totalSampled = 0;
            
            for (int i = 0; i < pixelCount; i += step)
            {
                int idx = i * stride + alphaOffset;
                if (idx >= pixelData.Length) break;
                totalSampled++;
                if (pixelData[idx] < 16)
                    nearZeroCount++;
            }
            
            // >= 10% of sampled pixels are near-transparent → genuine cutout
            return totalSampled > 0 && nearZeroCount * 10 >= totalSampled;
        }
        
        /// <summary>
        /// Check if a BC3/DXT5 texture has any non-trivial alpha by scanning alpha endpoint
        /// bytes in each 16-byte block. Returns true if any block has an endpoint below 250.
        /// </summary>
        private static bool SampleAlphaBC3(byte[] rawData)
        {
            int blockCount = rawData.Length / 16;
            int step = Math.Max(1, blockCount / 256);
            for (int b = 0; b < blockCount; b += step)
            {
                int offset = b * 16;
                byte a0 = rawData[offset];
                byte a1 = rawData[offset + 1];
                if (a0 < 250 || a1 < 250)
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if a BC3/DXT5 texture has genuine cutout transparency by scanning alpha
        /// endpoints. Returns true if a significant fraction of blocks have near-zero alpha
        /// endpoints (< 16), indicating real transparency rather than smoothness packing.
        /// </summary>
        private static bool SampleCutoutAlphaBC3(byte[] rawData)
        {
            int blockCount = rawData.Length / 16;
            if (blockCount == 0) return false;
            
            int sampleCount = Math.Min(blockCount, 1024);
            int step = Math.Max(1, blockCount / sampleCount);
            int nearZeroCount = 0;
            int totalSampled = 0;
            
            for (int b = 0; b < blockCount; b += step)
            {
                int offset = b * 16;
                byte a0 = rawData[offset];
                byte a1 = rawData[offset + 1];
                totalSampled++;
                if (a0 < 16 || a1 < 16)
                    nearZeroCount++;
            }
            
            // >= 10% of sampled blocks have near-zero alpha → genuine cutout
            return totalSampled > 0 && nearZeroCount * 10 >= totalSampled;
        }
        
        /// <summary>
        /// Detect alpha mode from Unity material properties, shader keywords, and render queue.
        /// Covers Standard shader (_Mode), URP/HDRP (_Surface), and keyword-based detection.
        /// Returns (mode, reason) for diagnostic logging.
        /// </summary>
        private static (AlphaMode mode, string reason) DetectAlphaModeWithReason(Material material)
        {
            // 1. Shader keywords (most reliable, works across Standard/URP/HDRP/custom)
            if (material.IsKeywordEnabled("_ALPHATEST_ON"))
                return (AlphaMode.Cutout, "keyword:_ALPHATEST_ON");
            if (material.IsKeywordEnabled("_ALPHABLEND_ON"))
                return (AlphaMode.Blend, "keyword:_ALPHABLEND_ON");
            if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return (AlphaMode.Blend, "keyword:_ALPHAPREMULTIPLY_ON");
            
            // 2. Standard shader _Mode property (0=Opaque, 1=Cutout, 2=Fade, 3=Transparent)
            if (material.HasProperty("_Mode"))
            {
                int mode = (int)material.GetFloat("_Mode");
                if (mode == 1) return (AlphaMode.Cutout, $"_Mode={mode}");
                if (mode >= 2) return (AlphaMode.Blend, $"_Mode={mode}");
            }
            
            // 3. URP/HDRP _Surface property (0=Opaque, 1=Transparent)
            if (material.HasProperty("_Surface"))
            {
                int surface = (int)material.GetFloat("_Surface");
                if (surface == 1)
                {
                    if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
                        return (AlphaMode.Cutout, $"_Surface=1,_AlphaClip=1");
                    return (AlphaMode.Blend, $"_Surface=1");
                }
            }
            
            // 4. Shader name heuristics (particles, transparents, additive, cutout)
            if (material.shader != null)
            {
                string sName = material.shader.name;
                if (!string.IsNullOrEmpty(sName))
                {
                    if (sName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("AlphaTest", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (AlphaMode.Cutout, $"shader:{sName}");

                    if (sName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (AlphaMode.Blend, $"shader:{sName}");
                }
            }

            // 5. Material name heuristics
            string mName = material.name;
            if (!string.IsNullOrEmpty(mName))
            {
                if (mName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("AlphaTest", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (AlphaMode.Cutout, $"matName:{mName}");

                if (mName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (AlphaMode.Blend, $"matName:{mName}");
            }

            // 6. Tint color alpha heuristics
            if (material.HasProperty("_TintColor"))
            {
                Color tc = material.GetColor("_TintColor");
                if (tc.a < 0.99f) return (AlphaMode.Blend, $"_TintColor.a={tc.a:F2}");
            }
            if (material.HasProperty("_Color"))
            {
                Color c = material.GetColor("_Color");
                if (c.a < 0.99f) return (AlphaMode.Blend, $"_Color.a={c.a:F2}");
            }
            
            // 7. Render queue heuristic (check both material and shader queue)
            int queue = material.renderQueue;
            if (queue < 0 && material.shader != null)
                queue = material.shader.renderQueue;

            if (queue >= 2450 && queue < 3000) return (AlphaMode.Cutout, $"renderQueue={queue}");
            if (queue >= 3000) return (AlphaMode.Blend, $"renderQueue={queue}");
            
            return (AlphaMode.Opaque, "default");
        }

        /// <summary>
        /// Detect if a material represents a physical translucent material (Water body or Glass thin wall) for OpenRemix.
        /// </summary>
        private (TranslucentKind kind, float ior, float distance, Color color, string reason) DetectTranslucentKind(
            Material material, string matName, string shaderName, Renderer renderer, AlphaMode alphaMode, Color albedoColor, float roughness, bool isParticleOrTrail = false)
        {
            if (waterManager != null && !waterManager.ConfigEnableTranslucents.Value)
                return (TranslucentKind.None, 1.0f, 0f, Color.white, "disabled");

            if (isParticleOrTrail || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                return (TranslucentKind.None, 1.0f, 0f, Color.white, "particle");

            int layer = renderer != null ? renderer.gameObject.layer : -1;
            string layerName = layer >= 0 ? LayerMask.LayerToName(layer) : null;
            string goName = renderer != null ? renderer.gameObject.name : null;
            string mName = matName ?? "";
            string sName = shaderName ?? "";

            // Non-rendered or invisible materials (e.g. InvisibleFace triggers, blockers) and particle/decal/splash effects must never be converted to translucent water or glass
            if (SceneMeshScanner.IsMaterialOrShaderInvisible(material) ||
                MatchesAnyKeyword(mName, "invisible", "nodraw", "trigger", "collisiononly", "zerostencilbuffer", "portalocclusion",
                    "particle", "trail", "drop", "drip", "splash", "spurt", "stain", "splat", "spark", "mist", "bubble", "pool", "fallback", "giblet", "chunk", "warning", "shockwave", "flash", "beam") ||
                MatchesAnyKeyword(sName, "invisible", "nodraw", "clear", "particle") ||
                (goName != null && MatchesAnyKeyword(goName, "particle", "trail", "drop", "drip", "splash", "spurt", "stain", "splat", "spark", "giblet", "chunk", "warning")))
            {
                return (TranslucentKind.None, 1.0f, 0f, Color.white, "excluded");
            }

            bool enableWater = waterManager == null || waterManager.ConfigEnableWater.Value;
            bool enableGlass = waterManager == null || waterManager.ConfigEnableGlass.Value;

            // 1. Water & Fluid Body Detection (Translucent Body with refraction, TIR, Snell's window, and volumetric depth fog)
            if (enableWater)
            {
                bool isLiquid = false;
                string reason = null;

                // Built-in Unity Water layer (Layer 4)
                if (layer == 4 || (layerName != null && layerName.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    isLiquid = true;
                    reason = $"layer:{layerName ?? layer.ToString()}";
                }
                // Shader name heuristics
                else if (MatchesAnyKeyword(sName, "water", "ocean", "river", "lake", "pond", "waterfall", "fluid", "aquatic", "puddle", "stream",
                         "blood", "mulch", "gore", "slime", "toxic", "acid", "sewer", "poison"))
                {
                    isLiquid = true;
                    reason = $"shader:{sName}";
                }
                // Material name heuristics
                else if (MatchesAnyKeyword(mName, "water", "ocean", "river", "lake", "pond", "waterfall", "liquid", "aquatic", "puddle", "stream",
                         "blood", "mulch", "gore", "flesh", "slime", "toxic", "acid", "sewer", "poison") &&
                         !MatchesAnyKeyword(mName, "watermark", "resistwater", "resistWater", "waterproof", "decal", "splatter", "spray", "particle", "drop", "splash", "stain", "pool", "fallback", "trail"))
                {
                    isLiquid = true;
                    reason = $"matName:{mName}";
                }
                // GameObject name heuristics
                else if (goName != null && MatchesAnyKeyword(goName, "waterfall", "water_plane", "waterplane", "ocean", "river", "pond", "lake",
                         "blood", "mulch", "gore", "slime", "toxic", "acid") &&
                         !MatchesAnyKeyword(goName, "watermark", "resistwater", "resistWater", "underwateroverlay", "decal", "splatter", "spray", "particle", "drop", "splash", "stain", "pool", "fallback", "trail"))
                {
                    isLiquid = true;
                    reason = $"goName:{goName}";
                }
                // Shader properties
                else if (material != null && (material.HasProperty("_WaterColor") || material.HasProperty("_DepthColor") || material.HasProperty("_WaveSpeed") || material.HasProperty("_WaveScale") || material.HasProperty("_BloodColor")))
                {
                    isLiquid = true;
                    reason = "liquidProperties";
                }

                if (isLiquid)
                {
                    // Classify specific liquid subtype: Blood/Mulch, Acid/Slime, or Clean Water
                    bool isBlood = MatchesAnyKeyword(mName, "blood", "mulch", "gore", "flesh", "bleed") ||
                                   MatchesAnyKeyword(sName, "blood", "mulch", "gore", "flesh") ||
                                   (goName != null && MatchesAnyKeyword(goName, "blood", "mulch", "gore", "flesh"));

                    bool isAcid = !isBlood && (MatchesAnyKeyword(mName, "acid", "slime", "toxic", "poison", "ooze") ||
                                   MatchesAnyKeyword(sName, "acid", "slime", "toxic", "poison", "ooze") ||
                                   (goName != null && MatchesAnyKeyword(goName, "acid", "slime", "toxic", "poison", "ooze")));

                    float ior;
                    float dist;
                    Color liquidCol;

                    if (isBlood)
                    {
                        ior = waterManager != null ? waterManager.ConfigBloodIOR.Value : 1.34f;
                        dist = waterManager != null ? waterManager.ConfigBloodClarity.Value : 2.0f;
                        liquidCol = ExtractBloodColor(material, albedoColor);
                        reason += ":blood";
                    }
                    else if (isAcid)
                    {
                        ior = 1.35f;
                        dist = 2.5f;
                        liquidCol = ExtractToxicColor(material, albedoColor);
                        reason += ":acid";
                    }
                    else
                    {
                        ior = waterManager != null ? waterManager.ConfigWaterIOR.Value : 1.333f;
                        dist = waterManager != null ? waterManager.ConfigWaterClarity.Value : 15.0f;
                        liquidCol = ExtractWaterColor(material, albedoColor);
                    }

                    if (renderer != null && waterManager != null)
                    {
                        waterManager.RegisterWaterSurface(renderer, liquidCol, dist);
                    }
                    if (waterManager != null)
                    {
                        waterManager.WaterMaterialCount++;
                    }

                    return (TranslucentKind.Water, ior, dist, liquidCol, reason);
                }
            }

            // 2. Glass Detection (Thin Wall specular transmission sheet with Fresnel reflection)
            if (enableGlass)
            {
                bool isGlass = false;
                string reason = null;

                // Layer heuristics (e.g. ULTRAKILL Layer 17 'BrokenGlass')
                if (layerName != null && layerName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isGlass = true;
                    reason = $"layer:{layerName}";
                }
                // Shader name heuristics
                else if (MatchesAnyKeyword(sName, "glass", "window", "windshield", "visor", "lens", "refractive", "stainedglass", "crystal", "ice"))
                {
                    isGlass = true;
                    reason = $"shader:{sName}";
                }
                // Material name heuristics
                else if (MatchesAnyKeyword(mName, "glass", "window", "windshield", "visor", "lens", "bottle", "jar", "stainedglass", "crystal", "ice") &&
                         !MatchesAnyKeyword(mName, "hourglass", "sunglasses"))
                {
                    isGlass = true;
                    reason = $"matName:{mName}";
                }
                // GameObject name heuristics (e.g. ULTRAKILL 'SandCan_Glass_Low')
                else if (goName != null && MatchesAnyKeyword(goName, "glass", "window", "windshield", "visor", "lens", "bottle", "jar") &&
                         !MatchesAnyKeyword(goName, "hourglass"))
                {
                    isGlass = true;
                    reason = $"goName:{goName}";
                }
                // PBR Glass heuristic: transparent queue + low alpha + smooth (not particle/smoke/blood/fire)
                else if (material != null)
                {
                    int queue = material.renderQueue;
                    if (queue >= 2900 && alphaMode == AlphaMode.Blend)
                    {
                        if (roughness <= 0.25f && albedoColor.a <= 0.75f)
                        {
                            if (!MatchesAnyKeyword(mName, "particle", "smoke", "fire", "blood", "mulch", "gore", "flesh", "decal", "glow", "halo", "trail", "spark", "explosion") &&
                                !MatchesAnyKeyword(sName, "particle", "smoke", "fire", "blood", "mulch", "gore", "flesh", "decal", "glow", "halo", "trail", "spark", "explosion"))
                            {
                                isGlass = true;
                                reason = $"pbrGlass(queue={queue},rough={roughness:F2},alpha={albedoColor.a:F2})";
                            }
                        }
                    }
                }

                if (isGlass)
                {
                    float ior = waterManager != null ? waterManager.ConfigGlassIOR.Value : 1.52f;
                    Color glassCol = ExtractGlassColor(material, albedoColor);

                    if (waterManager != null)
                    {
                        waterManager.GlassMaterialCount++;
                    }

                    return (TranslucentKind.Glass, ior, 0.0f, glassCol, reason);
                }
            }

            return (TranslucentKind.None, 1.0f, 0f, Color.white, "none");
        }

        private static bool MatchesAnyKeyword(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (text.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static Color ExtractWaterColor(Material mat, Color fallbackColor)
        {
            if (mat != null)
            {
                string[] waterColorProps = { "_WaterColor", "_DepthColor", "_ShallowColor", "_UnderwaterColor", "_Color", "_BaseColor" };
                for (int i = 0; i < waterColorProps.Length; i++)
                {
                    if (mat.HasProperty(waterColorProps[i]))
                    {
                        Color c = mat.GetColor(waterColorProps[i]);
                        if (c.maxColorComponent > 0.05f && (c.r < 0.95f || c.g < 0.95f || c.b < 0.95f))
                        {
                            return new Color(c.r, c.g, c.b, 1.0f);
                        }
                    }
                }

                if (mat.HasProperty("_MainTex") && mat.mainTexture is Texture2D tex && tex.isReadable)
                {
                    try
                    {
                        Color px = tex.GetPixel(tex.width / 2, tex.height / 2);
                        if (px.maxColorComponent > 0.05f && (px.r < 0.95f || px.g < 0.95f || px.b < 0.95f))
                        {
                            return new Color(px.r, px.g, px.b, 1.0f);
                        }
                    }
                    catch { }
                }
            }
            if (fallbackColor.maxColorComponent > 0.05f && (fallbackColor.r < 0.95f || fallbackColor.g < 0.95f || fallbackColor.b < 0.95f))
            {
                return new Color(fallbackColor.r, fallbackColor.g, fallbackColor.b, 1.0f);
            }
            // Natural clean water cyan/blue
            return new Color(0.20f, 0.70f, 0.90f, 1.0f);
        }

        private static Color ExtractBloodColor(Material mat, Color fallbackColor)
        {
            Color candidate = Color.clear;
            if (mat != null)
            {
                string[] bloodProps = { "_BloodColor", "_Color", "_BaseColor", "_TintColor", "_WaterColor", "_DepthColor" };
                for (int i = 0; i < bloodProps.Length; i++)
                {
                    if (mat.HasProperty(bloodProps[i]))
                    {
                        Color c = mat.GetColor(bloodProps[i]);
                        if (c.r > 0.30f && c.r > (c.g + c.b) * 0.65f)
                        {
                            candidate = c;
                            break;
                        }
                    }
                }

                if (candidate == Color.clear && mat.HasProperty("_MainTex") && mat.mainTexture is Texture2D tex && tex.isReadable)
                {
                    try
                    {
                        Color px = tex.GetPixel(tex.width / 2, tex.height / 2);
                        if (px.r > 0.25f && px.r > (px.g + px.b) * 0.6f)
                        {
                            candidate = px;
                        }
                    }
                    catch { }
                }
            }

            if (candidate == Color.clear && fallbackColor.r > 0.30f && fallbackColor.r > (fallbackColor.g + fallbackColor.b) * 0.65f)
            {
                candidate = fallbackColor;
            }

            if (candidate != Color.clear)
            {
                // Enforce physical blood absorption: red transmits, green and blue are heavily absorbed
                float r = Mathf.Clamp(candidate.r, 0.65f, 0.95f);
                float g = Mathf.Clamp(candidate.g * 0.1f, 0.01f, 0.05f);
                float b = Mathf.Clamp(candidate.b * 0.1f, 0.01f, 0.05f);
                return new Color(r, g, b, 1.0f);
            }

            // Default deep visceral blood red transmittance (Beer-Lambert: green and blue absorbed within ~2m)
            return new Color(0.85f, 0.02f, 0.02f, 1.0f);
        }

        private static Color ExtractToxicColor(Material mat, Color fallbackColor)
        {
            if (mat != null)
            {
                string[] toxicProps = { "_AcidColor", "_SlimeColor", "_ToxicColor", "_Color", "_BaseColor" };
                for (int i = 0; i < toxicProps.Length; i++)
                {
                    if (mat.HasProperty(toxicProps[i]))
                    {
                        Color c = mat.GetColor(toxicProps[i]);
                        if (c.g > 0.30f && c.g > (c.r + c.b) * 0.65f)
                        {
                            return new Color(c.r * 0.15f, Mathf.Clamp(c.g, 0.7f, 0.95f), c.b * 0.15f, 1.0f);
                        }
                    }
                }
            }
            return new Color(0.15f, 0.85f, 0.15f, 1.0f);
        }

        private static Color ExtractGlassColor(Material mat, Color fallbackColor)
        {
            if (fallbackColor.a < 0.95f && (fallbackColor.r < 0.95f || fallbackColor.g < 0.95f || fallbackColor.b < 0.95f))
            {
                return new Color(fallbackColor.r, fallbackColor.g, fallbackColor.b, 1.0f);
            }
            if (mat != null)
            {
                if (mat.HasProperty("_Color"))
                {
                    Color c = mat.GetColor("_Color");
                    if (c.r < 0.98f || c.g < 0.98f || c.b < 0.98f)
                        return new Color(c.r, c.g, c.b, 1.0f);
                }
                if (mat.HasProperty("_TintColor"))
                {
                    Color c = mat.GetColor("_TintColor");
                    if (c.r < 0.98f || c.g < 0.98f || c.b < 0.98f)
                        return new Color(c.r, c.g, c.b, 1.0f);
                }
            }
            return Color.white;
        }

        #region Auto PBR & Normal Map Generation (Experimental)

        private struct PbrTaxonomyEntry
        {
            public string categoryName;
            public float metallic;
            public float roughness;
            public string[] keywords;

            public PbrTaxonomyEntry(string categoryName, float metallic, float roughness, params string[] keywords)
            {
                this.categoryName = categoryName;
                this.metallic = metallic;
                this.roughness = roughness;
                this.keywords = keywords;
            }
        }

        private static readonly PbrTaxonomyEntry[] PbrTaxonomy = new PbrTaxonomyEntry[]
        {
            // 1. Precious & Polished Metals
            new PbrTaxonomyEntry("Chrome/Mirror", 1.0f, 0.05f,
                "chrome", "mirror", "silver", "polished_metal", "specular_metal"),
            new PbrTaxonomyEntry("Gold", 1.0f, 0.15f,
                "gold", "golden", "gilded", "brass", "bronze", "copper"),

            // 2. Structural & Industrial Metals
            new PbrTaxonomyEntry("Smooth Metal/Steel", 1.0f, 0.25f,
                "steel", "iron", "aluminum", "metal_smooth", "metallic", "metal_trim", "gunmetal"),
            new PbrTaxonomyEntry("Standard Metal/Machinery", 1.0f, 0.38f,
                "metal", "machinery", "machine", "armor", "armour", "chassis", "engine", "hull",
                "pipe", "pipeline", "barrel", "rebar", "grate", "grating", "girder", "beam",
                "scaffold", "vent", "panel_metal", "sheet_metal", "weapon", "sword", "blade", "bullet"),
            new PbrTaxonomyEntry("Rusted/Oxidized Metal", 0.35f, 0.75f,
                "rust", "rusted", "rusty", "corrosion", "corroded", "oxidized"),

            // 3. High-Gloss Dielectrics
            new PbrTaxonomyEntry("Marble/Polished Stone", 0.0f, 0.12f,
                "marble", "porcelain", "polished_stone", "polished_rock", "granite_polished"),
            new PbrTaxonomyEntry("Ceramic/Tile", 0.0f, 0.18f,
                "ceramic", "tile", "tiles", "tiling", "mosaic", "glazed"),
            new PbrTaxonomyEntry("Glossy Plastic/Car Paint/Glassy", 0.0f, 0.22f,
                "plastic", "shiny", "glossy", "varnish", "acrylic", "lacquer", "enamel", "polymer"),
            new PbrTaxonomyEntry("Crystal/Gemstone", 0.0f, 0.10f,
                "crystal", "gem", "gemstone", "diamond", "ruby", "emerald", "sapphire", "amethyst", "obsidian"),

            // 4. Semi-Gloss & Organic Surfaces
            new PbrTaxonomyEntry("Polished Wood", 0.0f, 0.28f,
                "wood_polished", "parquet", "varnished_wood", "lacquered_wood", "wood_shiny"),
            new PbrTaxonomyEntry("Leather/Rubber", 0.0f, 0.55f,
                "leather", "rubber", "tire", "vinyl", "tarp", "latex"),
            new PbrTaxonomyEntry("Flesh/Skin", 0.0f, 0.42f,
                "skin", "flesh", "meat", "organ", "muscle", "guts", "viscera", "tongue"),

            // 5. Standard Matte & Rough Surfaces
            new PbrTaxonomyEntry("Rough Wood/Bark", 0.0f, 0.65f,
                "wood", "plank", "planks", "timber", "bark", "log", "branch", "lumber", "fence", "crate", "barrel_wood"),
            new PbrTaxonomyEntry("Cardboard/Paper", 0.0f, 0.82f,
                "paper", "cardboard", "carton", "book", "scroll", "poster", "document"),
            new PbrTaxonomyEntry("Foliage/Vegetation", 0.0f, 0.60f,
                "grass", "leaf", "leaves", "foliage", "moss", "plant", "bush", "vine", "tree", "flora"),
            new PbrTaxonomyEntry("Stone/Rock/Masonry", 0.0f, 0.85f,
                "stone", "rock", "boulder", "cliff", "cobble", "cobblestone", "masonry", "slate", "granite", "monument"),
            new PbrTaxonomyEntry("Concrete/Brick/Asphalt", 0.0f, 0.88f,
                "concrete", "brick", "bricks", "asphalt", "cement", "pavement", "sidewalk", "plaster", "stucco"),
            new PbrTaxonomyEntry("Fabric/Cloth/Carpet", 0.0f, 0.92f,
                "fabric", "cloth", "carpet", "rug", "canvas", "curtain", "flag", "banner", "linen", "wool",
                "cotton", "towel", "blanket", "cushion", "upholstery", "velvet", "silk"),
            new PbrTaxonomyEntry("Dirt/Mud/Sand/Ground", 0.0f, 0.95f,
                "dirt", "soil", "mud", "sand", "ground", "earth", "gravel", "dust", "terrain", "clay")
        };

        private bool InferPbrProperties(Material material, Texture2D albedoTex, Renderer renderer, string shaderName, out float inferredMetallic, out float inferredRoughness, out string matchedCategory)
        {
            inferredMetallic = 0.0f;
            inferredRoughness = 0.7f;
            matchedCategory = null;

            string matName = material != null ? material.name : null;
            string texName = albedoTex != null ? albedoTex.name : null;
            string goName = renderer != null ? renderer.gameObject.name : null;

            for (int i = 0; i < PbrTaxonomy.Length; i++)
            {
                var entry = PbrTaxonomy[i];
                if (MatchesAnyKeyword(matName, entry.keywords) ||
                    MatchesAnyKeyword(texName, entry.keywords) ||
                    MatchesAnyKeyword(goName, entry.keywords) ||
                    MatchesAnyKeyword(shaderName, entry.keywords))
                {
                    inferredMetallic = entry.metallic;
                    inferredRoughness = entry.roughness;
                    matchedCategory = entry.categoryName;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Generates a tangent-space normal map from an albedo texture via a Sobel height filter on luminance.
        /// Caches generated normal maps by texture instance ID to prevent redundant GPU/CPU work.
        /// </summary>
        private (IntPtr handle, ulong hash) GetOrCreateAutoNormalMap(Texture2D albedoTex, float bumpStrength)
        {
            if (albedoTex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);

            int texId = albedoTex.GetInstanceID();
            long cacheKey = ((long)texId << 32) | (uint)(Mathf.RoundToInt(bumpStrength * 100f) & 0xFFFFFFFF);
            lock (autoNormalCache)
            {
                if (autoNormalCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            try
            {
                Color32[] pixels;
                int w = albedoTex.width;
                int h = albedoTex.height;

                // Clamp processing resolution to at most 1024x1024 for instant processing (<1ms)
                int maxDim = 1024;
                if (w > maxDim || h > maxDim || !albedoTex.isReadable)
                {
                    int targetW = Mathf.Min(w, maxDim);
                    int targetH = Mathf.Min(h, maxDim);
                    RenderTexture tmp = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(albedoTex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false, false);
                    readable.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    w = readable.width;
                    h = readable.height;
                    UnityEngine.Object.Destroy(readable);
                }
                else
                {
                    pixels = albedoTex.GetPixels32();
                }

                int numPixels = w * h;
                if (pixels == null || pixels.Length < numPixels)
                    return (IntPtr.Zero, 0);

                // 1. Precalculate luminance for Sobel convolution
                byte[] lum = new byte[numPixels];
                for (int i = 0; i < numPixels; i++)
                {
                    lum[i] = (byte)((pixels[i].r * 77 + pixels[i].g * 150 + pixels[i].b * 29) >> 8);
                }

                // 2. Sobel 3x3 filter to calculate tangent-space normal vectors (X, Y, Z)
                byte[] normalPixelData = new byte[numPixels * 4];
                float scale = (Mathf.Clamp(bumpStrength, 0.1f, 5.0f) * 2.5f) / 1020f;

                for (int y = 0; y < h; y++)
                {
                    int yPrev = (y > 0 ? y - 1 : h - 1) * w;
                    int yCurr = y * w;
                    int yNext = (y < h - 1 ? y + 1 : 0) * w;

                    for (int x = 0; x < w; x++)
                    {
                        int xPrev = (x > 0 ? x - 1 : w - 1);
                        int xNext = (x < w - 1 ? x + 1 : 0);

                        int tl = lum[yPrev + xPrev];
                        int tc = lum[yPrev + x];
                        int tr = lum[yPrev + xNext];

                        int ml = lum[yCurr + xPrev];
                        int mr = lum[yCurr + xNext];

                        int bl = lum[yNext + xPrev];
                        int bc = lum[yNext + x];
                        int br = lum[yNext + xNext];

                        // Sobel gradients
                        int dX = (tr + (mr << 1) + br) - (tl + (ml << 1) + bl);
                        int dY = (bl + (bc << 1) + br) - (tl + (tc << 1) + tr);

                        // Tangent-space normal vector
                        float nx = -dX * scale;
                        float ny = -dY * scale;
                        float nz = 1.0f;

                        float invLen = 1.0f / Mathf.Sqrt(nx * nx + ny * ny + 1.0f);
                        nx *= invLen;
                        ny *= invLen;
                        nz *= invLen;

                        int idx = (yCurr + x) * 4;
                        normalPixelData[idx + 0] = (byte)((nx * 0.5f + 0.5f) * 255f); // R = Tangent X
                        normalPixelData[idx + 1] = (byte)((ny * 0.5f + 0.5f) * 255f); // G = Tangent Y
                        normalPixelData[idx + 2] = (byte)((nz * 0.5f + 0.5f) * 255f); // B = Tangent Z
                        normalPixelData[idx + 3] = 255;                                // A
                    }
                }

                // 3. Compute hash and queue for deferred upload on render thread
                ulong hash = XXHash64.ComputeHash(normalPixelData, 0, normalPixelData.Length);
                if (hash == 0) hash = 1;

                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = 0,
                        pixelData = normalPixelData,
                        hash = hash,
                        width = (uint)w,
                        height = (uint)h,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                }

                var result = (new IntPtr((long)hash), hash);
                lock (autoNormalCache)
                {
                    autoNormalCache[cacheKey] = result;
                }
                return result;
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to generate auto normal map for '{albedoTex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }

        /// <summary>
        /// Updates all captured materials to apply or remove auto PBR heuristics and auto normal maps live.
        /// Resets remixMaterialHandle for altered materials so Remix creates new material instances.
        /// </summary>
        public void UpdateAllAutoPbrMaterials()
        {
            bool pbrEnabled = ConfigAutoPBR != null && ConfigAutoPBR.Value;
            bool normalEnabled = ConfigAutoNormalMaps != null && ConfigAutoNormalMaps.Value;
            float normalStrength = ConfigAutoNormalStrength != null ? ConfigAutoNormalStrength.Value : 1.0f;

            int newPbrCount = 0;
            int newNormalCount = 0;

            foreach (var kvp in materialTextureData.ToArray())
            {
                var matData = kvp.Value;
                bool changed = false;

                // 1. Update Metallic & Roughness
                float targetMetallic = matData.baseMetallic;
                float targetRoughness = matData.baseRoughness;

                if (pbrEnabled && matData.hasInferredPbr)
                {
                    targetMetallic = matData.inferredMetallic;
                    targetRoughness = matData.inferredRoughness;
                    newPbrCount++;
                }

                if (Mathf.Abs(matData.metallic - targetMetallic) > 0.001f ||
                    Mathf.Abs(matData.roughness - targetRoughness) > 0.001f)
                {
                    matData.metallic = targetMetallic;
                    matData.roughness = targetRoughness;
                    changed = true;
                }

                // 2. Update Normal Map
                IntPtr targetNormalHandle = matData.originalNormalHandle;
                ulong targetNormalHash = matData.originalNormalTextureHash;

                if (normalEnabled && matData.originalNormalHandle == IntPtr.Zero && matData.albedoTex != null)
                {
                    var (genHandle, genHash) = GetOrCreateAutoNormalMap(matData.albedoTex, normalStrength);
                    if (genHandle != IntPtr.Zero)
                    {
                        targetNormalHandle = genHandle;
                        targetNormalHash = genHash;
                        newNormalCount++;
                    }
                }

                if (matData.normalHandle != targetNormalHandle || matData.normalTextureHash != targetNormalHash)
                {
                    matData.normalHandle = targetNormalHandle;
                    matData.normalTextureHash = targetNormalHash;
                    changed = true;
                }

                if (changed)
                {
                    matData.remixMaterialHandle = IntPtr.Zero;
                    materialTextureData[kvp.Key] = matData;
                }
            }

            autoPbrMaterialCount = newPbrCount;
            autoNormalMapCount = newNormalCount;

            logger?.LogInfo($"[AutoPBR] Live update completed: PBR={pbrEnabled} ({autoPbrMaterialCount} active), AutoNormal={normalEnabled} ({autoNormalMapCount} active, strength={normalStrength:F2})");
        }

        #endregion

        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        
        private IntPtr CreateRemixMaterialSimple(ref MaterialTextureData matData, int materialId)
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
                    if (materialHandleByHash.TryGetValue(matHash, out IntPtr existingHandle) && existingHandle != IntPtr.Zero)
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
                IntPtr extPtr;
                IntPtr emissionExtPtr = IntPtr.Zero;
                RemixAPI.remixapi_MaterialInfoTranslucentEXT_Raw transExt = default;
                RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw opaqueExt = default;

                if (RemixAPI.IsOpenRemix && matData.suppressLightGeneration)
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
                            // DXVK-Remix compatibility
                            opaqueExt.blendType_hasvalue = 1; // remixapi_Bool.True
                            opaqueExt.blendType_value = 1;
                            // OpenRemix: enable alpha testing (GREATER_OR_EQUAL) so near-zero alpha texels are discarded in acceptCandidate
                            opaqueExt.alphaTestType = 6;
                            if (matData.suppressLightGeneration && RemixAPI.IsOpenRemix)
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
                    
                    extHandle = GCHandle.Alloc(opaqueExt, GCHandleType.Pinned);
                    extPtr = extHandle.AddrOfPinnedObject();
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
                    extHandle.Free();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception creating material: {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Process queued texture uploads on the render thread.
        /// Must be called before ProcessMeshCreationBatch so textures are available when materials are created.
        /// </summary>
        public void ProcessPendingTextureUploads()
        {
            PendingTextureUpload[] batch;
            lock (pendingTextureLock)
            {
                if (pendingTextureUploads.Count == 0)
                    return;
                batch = pendingTextureUploads.ToArray();
                pendingTextureUploads.Clear();
            }
            
            foreach (var upload in batch)
            {
                GCHandle pinned = GCHandle.Alloc(upload.pixelData, GCHandleType.Pinned);
                try
                {
                    var info = new RemixAPI.remixapi_TextureInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_TEXTURE_INFO,
                        pNext = IntPtr.Zero,
                        hash = upload.hash,
                        width = upload.width,
                        height = upload.height,
                        depth = 1,
                        mipLevels = upload.mipLevels,
                        format = upload.format,
                        data = pinned.AddrOfPinnedObject(),
                        dataSize = (ulong)upload.pixelData.Length
                    };
                    
                    IntPtr handle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock) { result = createTextureFunc(ref info, out handle); }
                    
                    if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        lock (uploadedTextureHashes)
                        {
                            uploadedTextureHashes.Add(upload.hash);
                        }
                        lock (pendingTextureLock)
                        {
                            if (upload.texId >= 0)
                            {
                                textureCache[upload.texId] = handle;
                                pendingTextureIds.Remove(upload.texId);
                            }
                            if (upload.tintedCacheKey != 0)
                            {
                                tintedTextureCache[upload.tintedCacheKey] = (handle, upload.hash);
                                pendingTintedKeys.Remove(upload.tintedCacheKey);
                                pendingTintedHashes.Remove(upload.tintedCacheKey);
                            }
                        }
                    }
                    else
                    {
                        logger.LogError($"Failed to upload deferred texture (hash 0x{upload.hash:X16}): {result}");
                        lock (pendingTextureLock)
                        {
                            if (upload.texId >= 0) pendingTextureIds.Remove(upload.texId);
                            if (upload.tintedCacheKey != 0)
                            {
                                pendingTintedKeys.Remove(upload.tintedCacheKey);
                                pendingTintedHashes.Remove(upload.tintedCacheKey);
                            }
                        }
                    }
                }
                finally
                {
                    pinned.Free();
                }
            }
        }
        
        /// <summary>
        /// Lazily creates the debug texture on first use (must be called from render thread
        /// after the Remix device is registered).
        /// </summary>
        private void EnsureDebugTexture()
        {
            if (debugTextureHandle != IntPtr.Zero)
                return;
            CreateDebugTexture();
        }

        private void CreateDebugTexture()
        {
            if (createTextureFunc == null)
                return;

            const int size = 8;
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    bool checker = ((x + y) & 1) == 0;
                    pixels[i + 0] = checker ? (byte)255 : (byte)0; // R
                    pixels[i + 1] = 0;                             // G
                    pixels[i + 2] = checker ? (byte)255 : (byte)0; // B
                    pixels[i + 3] = 255;                           // A
                }
            }

            debugTextureHash = 0xDEBB0600DEBB0600UL;

            GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var info = new RemixAPI.remixapi_TextureInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_TEXTURE_INFO,
                    pNext = IntPtr.Zero,
                    hash = debugTextureHash,
                    width = size,
                    height = size,
                    depth = 1,
                    mipLevels = 1,
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM,
                    data = pinned.AddrOfPinnedObject(),
                    dataSize = (ulong)pixels.Length
                };

                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createTextureFunc(ref info, out debugTextureHandle);
                }

                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    lock (uploadedTextureHashes)
                    {
                        uploadedTextureHashes.Add(debugTextureHash);
                    }
                    logger.LogInfo($"Created debug placeholder texture (hash: 0x{debugTextureHash:X16})");
                }
                else
                {
                    logger.LogWarning($"Failed to create debug texture: {result}");
                }
            }
            finally
            {
                pinned.Free();
            }
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Cleanup()
        {
            materialCreationThreadRunning = false;
            if (materialCreationThread != null && materialCreationThread.IsAlive)
            {
                materialCreationThread.Join(1000);
            }
            
            // Discard pending uploads
            lock (pendingTextureLock)
            {
                pendingTextureUploads.Clear();
                pendingTextureIds.Clear();
                pendingTintedKeys.Clear();
                pendingTintedHashes.Clear();
            }
            
            if (destroyTextureFunc != null)
            {
                foreach (var handle in textureCache.Values)
                {
                    if (handle != IntPtr.Zero)
                    {
                        destroyTextureFunc(handle);
                    }
                }
            }
            textureCache.Clear();
            lock (autoNormalCache)
            {
                autoNormalCache.Clear();
            }
            lock (uploadedTextureHashes)
            {
                uploadedTextureHashes.Clear();
            }
        }

        // --- Diagnostic getters for debug HUD ---
        public int TextureCacheCount => textureCache.Count;
        public int MaterialDataCount => materialTextureData.Count;

        /// <summary>
        /// Log a summary of captured material texture stats.
        /// </summary>
        public void LogMaterialStats()
        {
            int total = materialTextureData.Count;
            int withAlbedo = 0, noAlbedo = 0;
            foreach (var kvp in materialTextureData)
            {
                if (kvp.Value.albedoHandle != IntPtr.Zero)
                    withAlbedo++;
                else
                    noAlbedo++;
            }
            int pendingTex;
            lock (pendingTextureLock) { pendingTex = pendingTextureUploads.Count; }
            logger.LogInfo($"[MaterialStats] {total} materials captured: {withAlbedo} with albedo, {noAlbedo} without | texCache={textureCache.Count} pendingTex={pendingTex}");
        }

        /// <summary>
        /// Returns a snapshot of material names that fell back to the debug placeholder texture.
        /// </summary>
        public string[] GetPlaceholderMaterialNames()
        {
            lock (placeholderMaterialNames)
                return placeholderMaterialNames.Count > 0
                    ? new List<string>(placeholderMaterialNames).ToArray()
                    : Array.Empty<string>();
        }

        private static MethodInfo _getPropertyCountMethod;
        private static MethodInfo _getPropertyNameMethod;
        private static MethodInfo _getPropertyTypeMethod;
        private static bool _shaderPropsReflected;

        private readonly Dictionary<string, int> describedShaderMaterials = new Dictionary<string, int>();

        // Per material id: how much of a mesh's vertex colors is color (LayeredMaterialCompositor.VertexColorWeight).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, float> vertexColorWeights = new System.Collections.Concurrent.ConcurrentDictionary<int, float>();
        private readonly Dictionary<int, Texture2D> layeredComposites = new Dictionary<int, Texture2D>();

        public float GetVertexColorWeight(int materialId)
        {
            return vertexColorWeights.TryGetValue(materialId, out float weight) ? weight : 1f;
        }

        private Texture2D GetLayeredComposite(Material material)
        {
            int id = material.GetInstanceID();
            if (layeredComposites.TryGetValue(id, out var cached))
                return cached;
            Texture2D composite = null;
            try
            {
                composite = LayeredMaterialCompositor.Compose(material, 256, out string description);
                logger.LogInfo($"[LayeredMaterial] '{material.name}' shader '{(material.shader != null ? material.shader.name : "-")}': {description}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[LayeredMaterial] '{material.name}': {ex.Message}");
            }
            layeredComposites[id] = composite;
            return composite;
        }

        // Particle materials whose look is computed by the shader rather than read from a texture,
        // drawn once by Unity (ParticleMaterialSnapshot). Null: the material's own texture serves.
        private readonly Dictionary<int, Texture2D> particleSnapshots = new Dictionary<int, Texture2D>();

        /// <summary>
        /// True when the particle material is replaced by its snapshot, whose colors already
        /// include the material's tint (main thread).
        /// </summary>
        public bool UsesParticleSnapshot(Material material)
        {
            return material != null && GetParticleSnapshot(material) != null;
        }

        private Texture2D GetParticleSnapshot(Material material)
        {
            int id = material.GetInstanceID();
            if (particleSnapshots.TryGetValue(id, out var cached))
                return cached;
            Texture2D snapshot = null;
            try
            {
                var source = FindAlbedoTexture(material, out string sourceProp);
                if (ParticleMaterialSnapshot.ShouldSnapshot(source, out string why))
                {
                    snapshot = ParticleMaterialSnapshot.Render(material, 256, out string result);
                    logger.LogInfo($"[ParticleSnapshot] '{material.name}' shader '{(material.shader != null ? material.shader.name : "-")}': {why}; " +
                                   (snapshot != null ? $"drawn by Unity ({result})" : $"kept its texture ({result})"));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[ParticleSnapshot] '{material.name}': {ex.Message}");
                snapshot = null;
            }
            particleSnapshots[id] = snapshot;
            return snapshot;
        }

        /// <summary>
        /// The texture a material's albedo comes from: the known albedo property names first, then
        /// _MainTex, then any texture property that is not a normal/mask/noise/emission map.
        /// </summary>
        private Texture2D FindAlbedoTexture(Material material, out string matchedTexProp)
        {
            Texture2D tex = null;
            matchedTexProp = null;
            for (int p = 0; p < AlbedoTextureProps.Length; p++)
            {
                string pName = AlbedoTextureProps[p];
                if (material.HasProperty(pName))
                {
                    var t = material.GetTexture(pName) as Texture2D;
                    if (t != null)
                    {
                        tex = t;
                        matchedTexProp = pName;
                        break;
                    }
                }
            }

            // Only query material.mainTexture if _MainTex exists (prevents Unity engine warning spam)
            if (tex == null && material.HasProperty("_MainTex") && material.mainTexture is Texture2D mainT)
            {
                tex = mainT;
                matchedTexProp = "_MainTex";
            }

            // Dynamic fallback: inspect all texture property names on the material
            if (tex == null)
            {
                try
                {
                    string[] propNames = material.GetTexturePropertyNames();
                    if (propNames != null)
                    {
                        for (int i = 0; i < propNames.Length; i++)
                        {
                            string p = propNames[i];
                            if (string.IsNullOrEmpty(p)) continue;
                            string lower = p.ToLowerInvariant();
                            if (lower.Contains("bump") || lower.Contains("normal") ||
                                lower.Contains("lightmap") || lower.Contains("shadow") ||
                                lower.Contains("mask") || lower.Contains("noise") ||
                                lower.Contains("emission") || lower.Contains("emissive") ||
                                lower.Contains("sparkle") || lower.Contains("wind") ||
                                lower.Contains("distortion") || lower.Contains("dissolve"))
                            {
                                continue;
                            }
                            var t = material.GetTexture(p) as Texture2D;
                            if (t != null)
                            {
                                tex = t;
                                matchedTexProp = p;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }
            return tex;
        }


        // Every property of the material's shader with its value on this material, and its keywords.
        private string DescribeMaterialProperties(Material material)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var shader = material.shader;
                int count = shader.GetPropertyCount();
                for (int p = 0; p < count; p++)
                {
                    string name = shader.GetPropertyName(p);
                    switch (shader.GetPropertyType(p))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var t = material.GetTexture(name);
                            sb.Append(t != null ? $"{name}='{t.name}' {t.width}x{t.height}; " : $"{name}=none; ");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            var c = material.GetColor(name);
                            sb.Append($"{name}=({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2}); ");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            var v = material.GetVector(name);
                            sb.Append($"{name}=({v.x:G3},{v.y:G3},{v.z:G3},{v.w:G3}); ");
                            break;
                        default:
                            sb.Append($"{name}={material.GetFloat(name):G3}; ");
                            break;
                    }
                }
                var keywords = material.shaderKeywords;
                if (keywords != null && keywords.Length > 0)
                    sb.Append($"keywords [{string.Join(" ", keywords)}] ");
                sb.Append("passes [");
                for (int p = 0; p < material.passCount; p++)
                    sb.Append(p == 0 ? "" : ", ").Append(material.GetPassName(p));
                sb.Append("]");
            }
            catch (Exception ex)
            {
                sb.Append($"(properties unavailable: {ex.Message})");
            }
            return sb.ToString();
        }

        private void DumpShaderProperties(Shader shader, string shaderName)
        {
            try
            {
                if (!_shaderPropsReflected)
                {
                    _shaderPropsReflected = true;
                    _getPropertyCountMethod = typeof(Shader).GetMethod("GetPropertyCount", BindingFlags.Public | BindingFlags.Instance);
                    _getPropertyNameMethod = typeof(Shader).GetMethod("GetPropertyName", new[] { typeof(int) });
                    _getPropertyTypeMethod = typeof(Shader).GetMethod("GetPropertyType", new[] { typeof(int) });
                }

                if (_getPropertyCountMethod != null && _getPropertyNameMethod != null)
                {
                    int propCount = (int)_getPropertyCountMethod.Invoke(shader, null);
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[ShaderProps] '{shaderName}': ");
                    for (int p = 0; p < propCount; p++)
                    {
                        string pName = (string)_getPropertyNameMethod.Invoke(shader, new object[] { p });
                        string pType = _getPropertyTypeMethod != null ? _getPropertyTypeMethod.Invoke(shader, new object[] { p })?.ToString() : "?";
                        sb.Append($"{pName}({pType}) ");
                    }
                    logger.LogInfo(sb.ToString());
                }
            }
            catch { }
        }
    }
}
