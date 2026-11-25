using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityRemix.Games.Repo
{
    /// <summary>
    /// R.E.P.O.'s camcorder look over the path-traced frame. The game draws its world into a
    /// small render texture (RenderTextureMain) shown on the overlay camera, whose Post
    /// Processing Stack v2 layer adds grain, vignette, lens distortion, chromatic aberration and
    /// colour grading in one "uber" shader. That shader is passed through (ShaderPassThrough) as
    /// a screen effect: its material and per-frame values are read from the stack's own uber
    /// sheet after it renders, and the composed frame stands in for its source, at the game's
    /// render resolution.
    /// </summary>
    internal sealed class RepoProfile : GameProfile
    {
        public override string Name => "R.E.P.O.";
        public override string[] ExecutableNames => new[] { "REPO" };
        public override string DefaultsFolder => "Repo";

        public static BepInEx.Configuration.ConfigEntry<bool> GamePostProcess;

        public override bool UsesShaderPassThrough => true;

        private const string UberShader = "Hidden/PostProcessing/Uber";

        // Bloom reads a texture the stack allocates for the frame only (OpenRemix blooms the
        // frame itself); dithering (FINALPASS) cycles a texture a frame; stereo is never on.
        private static readonly HashSet<string> droppedKeywords = new HashSet<string>
        {
            "BLOOM", "BLOOM_LOW", "FINALPASS", "STEREO_INSTANCING_ENABLED", "STEREO_DOUBLEWIDE_TARGET", "UNITY_SINGLE_PASS_STEREO",
        };
        private static readonly string[] vectors =
        {
            "_Distortion_CenterScale", "_Distortion_Amount", "_Grain_Params1", "_Grain_Params2",
            "_Lut2D_Params", "_Lut3D_Params", "_Vignette_Color", "_Vignette_Center", "_Vignette_Settings",
        };
        private static readonly string[] floats =
        {
            "_ChromaticAberration_Amount", "_PostExposure", "_Vignette_Mode", "_Vignette_Opacity", "_LumaInAlpha",
        };
        private static readonly string[] textures = { "_ChromaticAberration_SpectralLut", "_Vignette_Mask" };

        private Type layerType;
        private FieldInfo sheetFactoryField, sheetsField;
        private PropertyInfo sheetMaterialProperty, sheetPropertiesProperty;
        private bool lookedUp, lookupFailed;
        private Behaviour layer;
        private int layerSearchFrame = -1000;
        private Material uber;
        private string loggedKeywords;

        // The stack's grain and LUT render textures, which it redraws every frame: copied into
        // textures of our own (the grain once: the shader offsets it a frame; the LUT when the
        // grading changes), sent to the replay only then.
        private Texture grainSource, lutSource;
        private Texture grainCopy, lutCopy;
        private int lutHash, lutCopiesLeft;

        // REPO's PostProcessing.colorGrading, whose settings the LUT is baked from.
        private Type postProcessingType;
        private FieldInfo postProcessingInstance, colorGradingField;
        private readonly List<FieldInfo> gradingParameters = new List<FieldInfo>();

        private bool LookUp()
        {
            if (lookedUp)
                return !lookupFailed;
            lookedUp = true;
            layerType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
            var factoryType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PropertySheetFactory");
            var sheetType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PropertySheet");
            sheetFactoryField = layerType != null ? AccessTools.Field(layerType, "m_PropertySheetFactory") : null;
            sheetsField = factoryType != null ? AccessTools.Field(factoryType, "m_Sheets") : null;
            sheetMaterialProperty = sheetType != null ? AccessTools.Property(sheetType, "material") : null;
            sheetPropertiesProperty = sheetType != null ? AccessTools.Property(sheetType, "properties") : null;
            lookupFailed = sheetFactoryField == null || sheetsField == null || sheetMaterialProperty == null || sheetPropertiesProperty == null;
            if (lookupFailed)
                Logger.LogInfo("[REPO] Post Processing Stack v2's uber sheet not found: the game's camcorder look is not drawn");

            postProcessingType = AccessTools.TypeByName("PostProcessing");
            postProcessingInstance = postProcessingType != null ? AccessTools.Field(postProcessingType, "Instance") : null;
            colorGradingField = postProcessingType != null ? AccessTools.Field(postProcessingType, "colorGrading") : null;
            var gradingType = colorGradingField?.FieldType;
            var parameterType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.ParameterOverride");
            if (gradingType != null && parameterType != null)
                foreach (var field in gradingType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                    if (parameterType.IsAssignableFrom(field.FieldType))
                        gradingParameters.Add(field);
            return !lookupFailed;
        }

        // The enabled post-processing layer (the overlay camera's), re-found while there is none.
        private Behaviour Layer()
        {
            if (layer != null && layer.isActiveAndEnabled)
                return layer;
            layer = null;
            if (Time.frameCount - layerSearchFrame < 120)
                return null;
            layerSearchFrame = Time.frameCount;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType(layerType))
                if (candidate is Behaviour b && b.isActiveAndEnabled)
                {
                    layer = b;
                    Logger.LogInfo($"[REPO] post-processing layer on '{b.name}': its uber shader passes through over the frame");
                    break;
                }
            return layer;
        }

        // The stack's uber sheet: (its material, the frame's values).
        private bool UberSheet(out Material material, out MaterialPropertyBlock properties)
        {
            material = null;
            properties = null;
            var factory = sheetFactoryField.GetValue(layer);
            if (factory == null || !(sheetsField.GetValue(factory) is IDictionary sheets))
                return false;
            foreach (DictionaryEntry entry in sheets)
                if (entry.Key is Shader shader && shader != null && shader.name == UberShader)
                {
                    material = sheetMaterialProperty.GetValue(entry.Value) as Material;
                    properties = sheetPropertiesProperty.GetValue(entry.Value) as MaterialPropertyBlock;
                    return material != null && properties != null;
                }
            return false;
        }

        public override Material PassThroughScreenEffect()
        {
            if (GamePostProcess == null || !GamePostProcess.Value || !ShaderPassThrough.Installed || !LookUp() || Layer() == null)
                return null;
            if (!UberSheet(out var source, out var properties))
                return null;

            if (uber == null || uber.shader != source.shader)
                uber = new Material(source) { name = "REPO uber (UnityRTX)", hideFlags = HideFlags.HideAndDontSave };
            var keywords = new List<string>();
            foreach (var keyword in source.shaderKeywords)
                if (!droppedKeywords.Contains(keyword))
                    keywords.Add(keyword);
            if (keywords.Count == 0)
                return null;
            uber.shaderKeywords = keywords.ToArray();
            string joined = string.Join(" ", keywords);
            if (joined != loggedKeywords)
            {
                loggedKeywords = joined;
                Logger.LogInfo($"[REPO] uber pass-through keywords: {joined}");
            }

            foreach (var name in vectors)
                uber.SetVector(name, properties.GetVector(name));
            foreach (var name in floats)
                uber.SetFloat(name, properties.GetFloat(name));
            foreach (var name in textures)
            {
                var t = properties.GetTexture(name);
                uber.SetTexture(name, t != null ? t : Texture2D.whiteTexture);
            }
            // Drawn into a render texture as the stack's passes before its last are.
            uber.SetVector("_UVTransform", SystemInfo.graphicsUVStartsAtTop ? new Vector4(1f, -1f, 0f, 1f) : new Vector4(1f, 1f, 0f, 0f));
            uber.SetTexture("_AutoExposureTex", Texture2D.whiteTexture);
            uber.SetFloat("_LumaInAlpha", 0f);

            if (uber.IsKeywordEnabled("GRAIN"))
                uber.SetTexture("_GrainTex", Copied(properties.GetTexture("_GrainTex"), ref grainSource, ref grainCopy, false));
            bool lut3D = uber.IsKeywordEnabled("COLOR_GRADING_HDR_3D");
            if (lut3D || uber.IsKeywordEnabled("COLOR_GRADING_HDR_2D") || uber.IsKeywordEnabled("COLOR_GRADING_LDR_2D"))
            {
                string name = lut3D ? "_Lut3D" : "_Lut2D";
                int hash = GradingHash();
                // The stack bakes the LUT when it renders, after this: copied over the next frames.
                if (hash != lutHash)
                {
                    lutHash = hash;
                    lutCopiesLeft = 2;
                }
                bool recopy = lutCopiesLeft > 0;
                if (recopy)
                    --lutCopiesLeft;
                uber.SetTexture(name, Copied(properties.GetTexture(name), ref lutSource, ref lutCopy, recopy));
            }
            return uber;
        }

        // A copy of the stack's render texture `rt` (made again when it changes, or `recopy`).
        private Texture Copied(Texture rt, ref Texture source, ref Texture copy, bool recopy)
        {
            if (rt == null)
                return Texture2D.blackTexture;
            int depth = rt is RenderTexture r && r.dimension == UnityEngine.Rendering.TextureDimension.Tex3D ? r.volumeDepth : 1;
            bool fresh = copy == null || rt != source || copy.width != rt.width || copy.height != rt.height ||
                         copy.graphicsFormat != rt.graphicsFormat;
            if (fresh)
            {
                if (copy != null)
                    UnityEngine.Object.Destroy(copy);
                copy = depth > 1
                    ? new Texture3D(rt.width, rt.height, depth, rt.graphicsFormat, TextureCreationFlags.None)
                    : (Texture)new Texture2D(rt.width, rt.height, rt.graphicsFormat, TextureCreationFlags.None);
                copy.name = rt.name + " (UnityRTX)";
                copy.wrapMode = rt.wrapMode;
                copy.filterMode = rt.filterMode;
                copy.hideFlags = HideFlags.HideAndDontSave;
                source = rt;
                Logger.LogInfo($"[REPO] copying the stack's '{rt.name}' {rt.width}x{rt.height}{(depth > 1 ? "x" + depth : "")} {rt.graphicsFormat}");
            }
            if (fresh || recopy)
            {
                Graphics.CopyTexture(rt, copy);
                ShaderPassThrough.TextureChanged(copy);
            }
            return copy;
        }

        // The colour grading's settings (the game's gamma setting, its saturation and contrast
        // fades, the level's temperature and filter): a change rebakes the LUT.
        private int GradingHash()
        {
            if (colorGradingField == null || postProcessingInstance == null)
                return 0;
            var instance = postProcessingInstance.GetValue(null);
            var grading = instance != null ? colorGradingField.GetValue(instance) : null;
            if (grading == null)
                return 0;
            int hash = 17;
            foreach (var field in gradingParameters)
            {
                var parameter = field.GetValue(grading);
                if (parameter == null)
                    continue;
                var valueField = parameter.GetType().GetField("value");
                var value = valueField?.GetValue(parameter);
                hash = hash * 31 + (value != null ? value.GetHashCode() : 0);
            }
            return hash;
        }

        // The game's world view: its RenderTextureMain's size (the graphics menu's render size).
        public override Vector2Int PassThroughScreenResolution(Material material)
        {
            var rt = WorldTexture();
            return material == uber && rt != null ? new Vector2Int(rt.width, rt.height) : Vector2Int.zero;
        }

        public override FilterMode PassThroughScreenFilter(Material material)
        {
            var rt = WorldTexture();
            return material == uber && rt != null ? rt.filterMode : FilterMode.Bilinear;
        }

        public override bool PassThroughScreenClipSpace(Material material) => material != null && material == uber;

        private Type renderTextureMainType;
        private FieldInfo renderTextureMainInstance, renderTextureField;

        private RenderTexture WorldTexture()
        {
            if (renderTextureMainType == null)
            {
                renderTextureMainType = AccessTools.TypeByName("RenderTextureMain");
                if (renderTextureMainType == null)
                    return null;
                renderTextureMainInstance = AccessTools.Field(renderTextureMainType, "instance");
                renderTextureField = AccessTools.Field(renderTextureMainType, "renderTexture");
            }
            var instance = renderTextureMainInstance?.GetValue(null);
            return instance != null ? renderTextureField?.GetValue(instance) as RenderTexture : null;
        }

        public override bool DrawSettings()
        {
            RemixImGui.TextColored(0.4f, 0.8f, 1.0f, 1.0f, "Screen");
            RemixImGui.Separator();
            if (GamePostProcess != null)
            {
                bool post = GamePostProcess.Value;
                if (RemixImGui.Checkbox("Camcorder look", ref post))
                    GamePostProcess.Value = post;
                if (RemixImGui.IsItemHovered())
                    RemixImGui.SetTooltip("R.E.P.O.'s own screen effects over the path-traced image, at its render size: " +
                                          "grain, vignette, lens distortion, chromatic aberration and colour grading.");
            }
            return true;
        }
    }
}
