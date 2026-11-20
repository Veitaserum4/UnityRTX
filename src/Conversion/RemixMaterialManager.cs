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
    public partial class RemixMaterialManager
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
        
        // A recolour mask (a game's player-chosen colours; GameProfile.GetColorMask): where the
        // mask's red, green, blue channel is set, the albedo moves toward itself times color1,
        // color2, color3 in turn, by weight x the channel. The base is the mask's own alpha
        // (baseInMaskAlpha: a greyscale copy of the surface) or the material's albedo texture.
        // metalFromColorAlpha: each colour's alpha is that region's metal amount, combined as
        // 1 - (1-m1)(1-m2)(1-m3); metal texels are given metalRoughness. Values are in the
        // shader's working space (linear in a linear-colour-space project).
        public struct ColorMask
        {
            public Texture2D mask;
            public Color color1, color2, color3;
            public float weight;
            public bool baseInMaskAlpha;
            public bool metalFromColorAlpha;
            public float metalRoughness;

            public int Hash()
            {
                unchecked
                {
                    int h = mask != null ? mask.GetInstanceID() : 0;
                    foreach (var c in new[] { color1, color2, color3 })
                    {
                        Color32 q = c;
                        h = h * 397 ^ ((q.r << 24) | (q.g << 16) | (q.b << 8) | q.a);
                    }
                    h = h * 397 ^ Mathf.RoundToInt(weight * 1000f);
                    h = h * 397 ^ (baseInMaskAlpha ? 1 : 0) ^ (metalFromColorAlpha ? 2 : 0);
                    return h * 397 ^ Mathf.RoundToInt(metalRoughness * 1000f);
                }
            }
        }

        // A thin film over a material (a game's coating, such as oil; GameProfile.GetIridescence):
        // OpenRemix's remixapi_MaterialIridescenceEXT, its interference colouring the reflection.
        // The coated surface's albedo is scaled by albedoScale and its roughness is roughness.
        public struct Iridescence
        {
            public float strength, filmIndex, thickness;
            public float flowAmplitude, flowScale, flowSpeed;
            public float structural;
            public float albedoScale, roughness;
            // Partial coverage: the film coats only the triangles whose vertices' UV set
            // coverageChannel, component coverageAxis, average below coverageBelow (each
            // vertex moved by up to +-coverageNoise, fixed per vertex, for a ragged edge); the
            // rest keep the bare material. Applied where the capture can split a mesh (GPU-
            // skinned meshes); elsewhere the film coats the whole surface.
            public bool partial;
            public int coverageChannel, coverageAxis;
            public float coverageBelow, coverageNoise;

            public int Hash()
            {
                unchecked
                {
                    int h = 0x1f11;
                    h = h * 397 ^ Mathf.RoundToInt(strength * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(filmIndex * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(thickness * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(flowAmplitude * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(flowScale * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(flowSpeed * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(structural * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(albedoScale * 1000f);
                    h = h * 397 ^ Mathf.RoundToInt(roughness * 1000f);
                    if (partial)
                    {
                        h = h * 397 ^ (coverageChannel * 8 + coverageAxis + 1);
                        h = h * 397 ^ Mathf.RoundToInt(coverageBelow * 1000f);
                        h = h * 397 ^ Mathf.RoundToInt(coverageNoise * 1000f);
                    }
                    return h;
                }
            }
        }

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
            public bool emissionAsShown;  // a backdrop (panoramic sky): its emission is what Unity shows, not scaled to light units
            public bool suppressLightGeneration; // OpenRemix: visible emission without analytic triangle light generation in LightBvh
            public int rayPortalIndex;  // remixapi_MaterialInfoPortalEXT index its surfaces open, -1 none
            public Color glowColor;     // an additive glow on a mesh: its emission (what Unity adds per white texel, over glowCoverage)
            public float glowCoverage;  // its texels' coverage scale, the glow's brightness up to 1 (0: not an additive glow)
            public float glowStrength;  // the captured glow's brightest channel (GlowFade's reference)
            public bool blendedInstance; // drawn with remixapi_InstanceInfoBlendEXT (a particle-like effect on a mesh)
            public bool additive;        // Unity adds it (additive particles, glows): its coverage times its colour
            public bool lightsScene; // its emission lights the scene, glow or not (GameProfile.SceneLightScale)
            public float viewChromaticAberration; // remixapi_MaterialInfoViewEffectsEXT (a view portal's screen)
            public bool paintFinish; // remixapi_MaterialInfoPaintFinishEXT: multiplicative paint with its own roughness
            public bool hasFilm;           // remixapi_MaterialIridescenceEXT (film)
            public Iridescence film;
            public bool rayPortalStandalone; // a portal alone (no opaque look): registered by SetRayPortalIndex
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
            // AutoPBR: which values it may set (the material has no explicit one), the keyword
            // table's values, and whether the albedo's statistics refined them (RefineInferredPbr),
            // at which AutoPbrTuning.
            public bool inferMetallic, inferRoughness;
            public float categoryMetallic, categoryRoughness;
            public bool pbrRefined;
            public Vector3 pbrTuning;
            // AutoPBR roughness detail: the albedo doubles as the roughness map, its inverted green
            // channel remapped to bias + scale * x (remixapi_MaterialPbrRemapEXT).
            public bool hasRoughnessDetail;
            public float roughnessDetailBias, roughnessDetailScale;
            // Unity's own PBR maps (DetectUnityPbrMaps): a texture whose red channel is metallic
            // and/or alpha smoothness, or smoothness in the albedo's alpha. Values are
            // bias + scale * x, with x the texel (metallic) or 1 - texel (roughness).
            public IntPtr pbrMapHandle;
            public bool pbrMapMetallic, pbrMapSmoothness, smoothnessInAlbedoAlpha;
            public float pbrMetalBias, pbrMetalScale, pbrRoughBias, pbrRoughScale;
            // Generated normal maps: strength factor of the material's AutoPBR category (1 when
            // unmatched), 0 for surfaces that get none (particles, translucent, blended).
            public float autoNormalScale;
}
        private System.Collections.Concurrent.ConcurrentDictionary<int, MaterialTextureData> materialTextureData = new System.Collections.Concurrent.ConcurrentDictionary<int, MaterialTextureData>();
        private RemixWaterManager waterManager;
        public void SetWaterManager(RemixWaterManager manager) => this.waterManager = manager;
        private HashSet<string> loggedShaderProperties = new HashSet<string>();

        // Auto PBR & Normal Map Generation (Experimental)
        public BepInEx.Configuration.ConfigEntry<bool> ConfigAutoPBR { get; private set; }
        public BepInEx.Configuration.ConfigEntry<bool> ConfigAutoNormalMaps { get; private set; }
        // Lighting/IntensityMultiplier (RemixLightConverter): Unity lights reach Remix scaled by it.
        public static BepInEx.Configuration.ConfigEntry<float> LightIntensityMultiplier;
        public static BepInEx.Configuration.ConfigEntry<bool> EmissionInLightUnits;

        // The Remix radiance of what Unity shows unlit at 1: a white diffuse surface lit head-on
        // by a Unity light of intensity 1 shows 1 in Unity, and in Remix (the light's radiance its
        // irradiance, times albedo / pi) the multiplier over pi.
        public static float UnlitRadianceScale => (LightIntensityMultiplier != null ? LightIntensityMultiplier.Value : 1f) / Mathf.PI;
        public BepInEx.Configuration.ConfigEntry<float> ConfigAutoNormalStrength { get; private set; }
        public BepInEx.Configuration.ConfigEntry<float> ConfigAutoPbrVariation { get; private set; }
        public BepInEx.Configuration.ConfigEntry<float> ConfigAutoPbrRoughnessBias { get; private set; }
        public BepInEx.Configuration.ConfigEntry<float> ConfigAutoPbrMetallicBias { get; private set; }
        // x: the variation, y: the roughness bias, z: the metallic bias.
        private Vector3 AutoPbrTuning => new Vector3(
            ConfigAutoPbrVariation != null ? Mathf.Clamp(ConfigAutoPbrVariation.Value, 0f, 2f) : 1f,
            ConfigAutoPbrRoughnessBias != null ? Mathf.Clamp(ConfigAutoPbrRoughnessBias.Value, -1f, 1f) : 0f,
            ConfigAutoPbrMetallicBias != null ? Mathf.Clamp(ConfigAutoPbrMetallicBias.Value, -1f, 1f) : 0f);
        private int autoPbrMaterialCount = 0;
        private int autoNormalMapCount = 0;
        public int AutoPbrMaterialCount => autoPbrMaterialCount;
        public int AutoNormalMapCount => autoNormalMapCount;
        private readonly Dictionary<long, (IntPtr handle, ulong hash)> autoNormalCache = new Dictionary<long, (IntPtr, ulong)>();
        private readonly Dictionary<int, AlbedoStats> albedoStatsCache = new Dictionary<int, AlbedoStats>();
        
        // Track all texture hashes confirmed to be uploaded into OpenRemix native runtime
        private readonly HashSet<ulong> uploadedTextureHashes = new HashSet<ulong>();
        private IntPtr fallbackMaterialHandle = IntPtr.Zero;
        
        // Queue for async material creation
        
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
                EmissionInLightUnits = config.Bind("Materials", "EmissionInLightUnits", true,
                    "Scale emission into the units Unity lights reach Remix in (Lighting/IntensityMultiplier over pi): " +
                    "an emissive surface then stands against lit ones as it does in Unity. Off: Unity's emission values as Remix radiance (about 3x brighter).");
                LightShafts.Bind(config, logger);
                ConfigAutoPBR = config.Bind("Materials", "AutoPBR", false,
                    "Infer PBR metallic and roughness from material/texture names for non-PBR materials (Experimental).");
                ConfigAutoNormalMaps = config.Bind("Materials", "AutoNormalMaps", false,
                    "Generate tangent-space normal maps from albedo textures via Sobel filter when no normal map exists (Experimental).");
                ConfigAutoNormalStrength = config.Bind("Materials", "AutoNormalStrength", 1.0f,
                    "Strength/intensity of automatically generated normal maps (0.1 to 3.0) (Experimental).");
                ConfigAutoPbrVariation = config.Bind("Materials", "AutoPbrVariation", 1.0f,
                    "How much AutoPBR varies materials of one kind, each texture by its own stable amount (0 to 2): " +
                    "roughness up to +/-0.18 and metals' metalness down to 0.15 less at 1.");
                ConfigAutoPbrRoughnessBias = config.Bind("Materials", "AutoPbrRoughnessBias", 0f,
                    "Added to every roughness AutoPBR infers (-1 to 1): above 0 rougher, below 0 glossier.");
                ConfigAutoPbrMetallicBias = config.Bind("Materials", "AutoPbrMetallicBias", 0f,
                    "Added to every metalness AutoPBR infers (-1 to 1): above 0 more metallic, below 0 less.");
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
        /// <summary>
        /// Whether a Forward+ bake can hold a surface with this material: not blended and
        /// not water/glass transmission (unsupported bake coverage).
        /// </summary>
        // Skybox/Panoramic in latitude-longitude layout on a mesh: Unity samples it by the
        // direction of each object-space vertex position, never the mesh's UVs.
        public static bool IsPanoramicSky(Material material)
        {
            if (material == null || material.shader == null || material.shader.name != "Skybox/Panoramic")
                return false;
            try { return !material.HasProperty("_Mapping") || Mathf.RoundToInt(material.GetFloat("_Mapping")) == 1; }
            catch { return false; }
        }

        public bool IsBakeableMaterial(int materialId)
        {
            return materialId == 0 || !materialTextureData.TryGetValue(materialId, out var data) ||
                   (data.alphaMode != AlphaMode.Blend && data.translucentKind == TranslucentKind.None);
        }

        // A particle-like material on a mesh renderer (an additive glow, a particle shader): its
        // instances blend (remixapi_InstanceInfoBlendEXT) as particles' do, rather than standing
        // as an opaque surface or a tinted sheet.
        // An additive glow's strength now over when captured (its _Color faded): 1 for others.
        public float GlowFade(int materialId, Material material)
        {
            if (material == null || !materialTextureData.TryGetValue(materialId, out var data) || data.glowStrength <= 0f)
                return 1f;
            // (Never 0: SkinnedMeshData.glowFade 0 means unset.)
            if (!TryGetAdditiveGlow(material, out Color glow))
                return 1e-4f;
            return Mathf.Clamp(Mathf.Max(glow.r, Mathf.Max(glow.g, glow.b)) / data.glowStrength, 1e-4f, 1f);
        }

        public bool IsAdditive(int materialId)
        {
            return materialId != 0 && materialTextureData.TryGetValue(materialId, out var data) && data.additive;
        }

        public bool NeedsBlendedInstance(int materialId)
        {
            return materialId != 0 && materialTextureData.TryGetValue(materialId, out var data) && data.blendedInstance;
        }

        // True for any additive material, its glow black when it adds nothing (a light shaft's
        // "off" twin): drawn as a glow of no coverage rather than as a solid surface.
        // Additive blending (SrcAlpha or One, One): ULTRAKILL/Additive and the legacy additive
        // particle shaders (fixed blend; 2 x _TintColor), and shaders reading _SrcBlend/_DstBlend
        // (ULTRAKILL/Master, Standard, Particles/Standard) set to it. glow: what Unity adds for a
        // white, opaque texel. (The psx transparent shaders keep stale _DstBlend values: they
        // alpha blend.)
        internal static bool TryGetAdditiveGlow(Material material, out Color glow)
        {
            glow = Color.black;
            if (material == null || material.shader == null)
                return false;
            string s = material.shader.name;
            bool fixedAdditive = s.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!fixedAdditive)
            {
                bool blendProperties = s == "Standard" ||
                                       s.StartsWith("Particles/Standard", StringComparison.Ordinal) ||
                                       Games.GameProfiles.ReadsBlendProperties(s);
                if (!blendProperties || !material.HasProperty("_SrcBlend") || !material.HasProperty("_DstBlend"))
                    return false;
                int src = Mathf.RoundToInt(material.GetFloat("_SrcBlend")), dst = Mathf.RoundToInt(material.GetFloat("_DstBlend"));
                if (dst != (int)UnityEngine.Rendering.BlendMode.One ||
                    (src != (int)UnityEngine.Rendering.BlendMode.One && src != (int)UnityEngine.Rendering.BlendMode.SrcAlpha))
                    return false;
            }
            if (material.HasProperty("_TintColor"))
            {
                Color t = material.GetColor("_TintColor");
                glow = new Color(2f * t.r * t.a, 2f * t.g * t.a, 2f * t.b * t.a, 1f);
            }
            else
            {
                Color c = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                float opacity = material.HasProperty("_Opacity") ? material.GetFloat("_Opacity") : 1f;
                glow = new Color(c.r * c.a * opacity, c.g * c.a * opacity, c.b * c.a * opacity, 1f);
                if (material.IsKeywordEnabled("_EMISSION") && material.HasProperty("_EmissionColor"))
                {
                    Color e = material.GetColor("_EmissionColor");
                    glow = new Color(glow.r + e.r, glow.g + e.g, glow.b + e.b, 1f);
                }
            }
            return true;
        }

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
        public int RegisterCustomMaterial(string name, Texture2D albedo, Texture2D normal = null, float roughness = 0.85f, float metallic = 0.0f,
            float alphaCutoff = 0f, bool paintFinish = false, Iridescence? film = null)
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
                alphaMode = alphaCutoff > 0f ? AlphaMode.Cutout : AlphaMode.Opaque,
                alphaCutoff = alphaCutoff,
                paintFinish = paintFinish,
                rayPortalIndex = -1, // 0 would open portal 0 (e.g. Limbo's TV view)
                wrapModeU = 1, // MDL Repeat
                wrapModeV = 1,
                filterMode = (byte)(albedo != null && albedo.filterMode == FilterMode.Point ? 0 : 1),
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
            if (film.HasValue)
            {
                matData.hasFilm = true;
                matData.film = film.Value;
            }

            materialTextureData[matId] = matData;
            return matId;
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

            // Factor in the material's own normal map so variants with/without one never collide.
            // A generated normal map (AutoNormalMaps) is left out: toggling it replaces the
            // material under the same hash, which its meshes then pick up.
            if (matData.originalNormalHandle != IntPtr.Zero)
            {
                hash ^= (ulong)matData.originalNormalHandle.ToInt64();
                hash *= 1099511628211UL;
            }

            if (matData.emissiveHandle != IntPtr.Zero)
            {
                hash ^= (ulong)matData.emissiveHandle.ToInt64();
                hash *= 1099511628211UL;
            }

            if (matData.hasFilm)
            {
                hash ^= (ulong)(uint)matData.film.Hash();
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
            if (matData.lightsScene)
            {
                hash ^= 0x4C474C53UL;
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
            if (VerboseLog.On) logger.LogInfo($"[MaterialStats] {total} materials captured: {withAlbedo} with albedo, {noAlbedo} without | texCache={textureCache.Count} pendingTex={pendingTex}");
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

        // Material id -> its top-color variant (registered as its own material) and the up threshold.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int id, float threshold)> topVariants =
            new System.Collections.Concurrent.ConcurrentDictionary<int, (int id, float threshold)>();

        /// <summary>
        /// For a layered material that colors upward-facing surfaces differently: the variant
        /// material id for faces whose up component exceeds the threshold.
        /// </summary>
        public bool TryGetTopVariant(int materialId, out int topMaterialId, out float threshold)
        {
            if (topVariants.TryGetValue(materialId, out var variant))
            {
                topMaterialId = variant.id;
                threshold = variant.threshold;
                return true;
            }
            topMaterialId = 0;
            threshold = 0f;
            return false;
        }

        private void RegisterTopVariant(Material material, int materialId, MaterialTextureData baseData, float threshold)
        {
            try
            {
                var top = LayeredMaterialCompositor.Compose(material, 256, true, out string description);
                if (top == null)
                    return;
                var data = baseData;
                data.materialName = baseData.materialName + " (top)";
                data.remixMaterialHandle = IntPtr.Zero;
                data.albedoHandle = UploadUnityTexture(top);
                data.albedoTextureHash = textureHashCache.TryGetValue(top.GetInstanceID(), out ulong hash) ? hash : 0;
                data.albedoTex = top;
                int topId = unchecked(materialId * 486187739 + 0x70C010);
                materialTextureData[topId] = data;
                vertexColorWeights[topId] = vertexColorWeights.TryGetValue(materialId, out float w) ? w : 1f;
                if (projectionScales.TryGetValue(materialId, out float projection))
                    projectionScales[topId] = projection;
                topVariants[materialId] = (topId, threshold);
                logger.LogInfo($"[LayeredMaterial] '{material.name}' top surfaces (up > {threshold:F2}): {description}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[LayeredMaterial] '{material.name}' top: {ex.Message}");
            }
        }

        // Material id -> repeats per object-space unit of the box-projected UVs it needs.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, float> projectionScales =
            new System.Collections.Concurrent.ConcurrentDictionary<int, float>();

        /// <summary>
        /// For a layered material drawn by world-projected layers: the scale of the box-projected
        /// UVs (RemixMeshConverter.ProjectUV) its surfaces need instead of the mesh's own.
        /// </summary>
        public bool TryGetProjection(int materialId, out float scale)
        {
            return projectionScales.TryGetValue(materialId, out scale);
        }

        // How a mesh's vertex colours apply to the material: 1 as colour, 0 not at all (masks of
        // a layered material), between partly; GlowVertexColors for an additive glow, whose
        // vertex brightness is how much it adds (a light shaft fading to black along its length):
        // moved into alpha, its coverage (RemixMeshConverter.WeightVertexColor).
        public const float GlowVertexColors = -1f;

        public float GetVertexColorWeight(int materialId)
        {
            if (materialTextureData.TryGetValue(materialId, out var data) && data.glowCoverage > 0f)
                return GlowVertexColors;
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
                composite = LayeredMaterialCompositor.Compose(material, 256, false, out string description);
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
        internal string DescribeMaterialProperties(Material material)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                if (UnityCompat.HasShaderPropertyType)
                    AppendShaderProperties(material, sb);
                else
                    sb.Append("(property list needs Unity 2019.3) ");
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

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AppendShaderProperties(Material material, System.Text.StringBuilder sb)
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
