using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Detects and extracts scrolling / animated UV parameters from Unity renderers,
    /// materials, and MaterialPropertyBlocks. Ensures animated water, conveyor belts,
    /// lava, and scrolling textures are excluded from static scene bakes and rendered
    /// with live, dynamic UV coordinates every frame.
    /// </summary>
    public static class RemixScrollingTextureDetector
    {
        private static readonly int PropScrollOffset = Shader.PropertyToID("_ScrollOffset");
        private static readonly int PropMainTexST = Shader.PropertyToID("_MainTex_ST");
        private static readonly int PropBaseMapST = Shader.PropertyToID("_BaseMap_ST");
        private static readonly int PropTextureOffset = Shader.PropertyToID("_TextureOffset");
        private static readonly int PropOffset = Shader.PropertyToID("_Offset");

        // Cache of renderer instance ID -> is scrolling to avoid repeated reflection / component queries
        private static readonly Dictionary<int, bool> _rendererScrollCache = new Dictionary<int, bool>();
        private static int _lastClearFrame = -1;

        // Cached reflection for ScrollingTexture component
        private static FieldInfo _scrollerOffsetField;
        private static FieldInfo _scrollerSpeedXField;
        private static FieldInfo _scrollerSpeedYField;
        private static bool _reflectionInitialized = false;

        /// <summary>
        /// Clears the per-renderer detection cache (e.g. on scene transitions).
        /// </summary>
        public static void ClearCache()
        {
            _rendererScrollCache.Clear();
        }

        /// <summary>
        /// Checks whether a renderer has scrolling/moving UVs via attached components
        /// or MaterialPropertyBlock properties.
        /// </summary>
        public static bool IsScrollingRenderer(Renderer renderer)
        {
            if (renderer == null) return false;

            int id = renderer.GetInstanceID();
            int currentFrame = Time.frameCount;

            // Periodically clean cache if it grows large
            if (currentFrame != _lastClearFrame && _rendererScrollCache.Count > 4096)
            {
                _rendererScrollCache.Clear();
                _lastClearFrame = currentFrame;
            }

            if (_rendererScrollCache.TryGetValue(id, out bool cached))
                return cached;

            bool isScrolling = DetectScrollingRenderer(renderer);
            _rendererScrollCache[id] = isScrolling;
            return isScrolling;
        }

        private static bool DetectScrollingRenderer(Renderer renderer)
        {
            if (renderer == null) return false;

            try
            {
                // 1. Check for specific, dedicated UV scroller components on GameObject, parent, or children
                if (CheckHierarchyForScroller(renderer.gameObject))
                    return true;

                // 2. Check MaterialPropertyBlock for active _ScrollOffset
                if (renderer.HasPropertyBlock())
                {
                    var mpb = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(mpb);
                    Vector4 sOff = mpb.GetVector(PropScrollOffset);
                    if (sOff.x != 0f || sOff.y != 0f)
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool CheckHierarchyForScroller(GameObject go)
        {
            if (go == null) return false;

            // Check GameObject itself
            var comps = go.GetComponents<MonoBehaviour>();
            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (IsKnownScrollerComponent(comps[i]))
                        return true;
                }
            }

            // Check immediate parent only (1 level) to catch compound visual child meshes,
            // but NEVER walk recursively up to the scene root (which causes whole rooms/levels to match)
            Transform parent = go.transform.parent;
            if (parent != null)
            {
                var parentComps = parent.GetComponents<MonoBehaviour>();
                if (parentComps != null)
                {
                    for (int i = 0; i < parentComps.Length; i++)
                    {
                        if (IsKnownScrollerComponent(parentComps[i]))
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool IsKnownScrollerComponent(MonoBehaviour c)
        {
            if (c == null) return false;
            string typeName = c.GetType().Name;

            // Explicitly exclude known non-scroller management and UI scripts FIRST
            if (typeName.StartsWith("Disable", StringComparison.OrdinalIgnoreCase) ||
                typeName.StartsWith("Menu", StringComparison.OrdinalIgnoreCase) ||
                typeName.StartsWith("UI", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Check") ||
                typeName.Contains("Tracker") ||
                typeName.Contains("Controller") ||
                typeName.Contains("Manager"))
            {
                return false;
            }

            // 1. Direct positive match for known scrollers
            if (typeName.Equals("ScrollingTexture", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("UVScroller", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("TextureScroller", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("ScrollUV", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("AnimateUV", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("ScrollTexture", StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals("MaterialScroller", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Generic ending matches (e.g. ConveyorScroller, WaterUVScroll)
            // Explicitly do NOT use broad substring "Text" which would match "Texture"
            if ((typeName.EndsWith("Scroller", StringComparison.OrdinalIgnoreCase) ||
                 typeName.EndsWith("UVScroll", StringComparison.OrdinalIgnoreCase) ||
                 typeName.EndsWith("TextureScroll", StringComparison.OrdinalIgnoreCase)) &&
                !typeName.StartsWith("Text", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static MonoBehaviour FindScrollerComponent(GameObject go)
        {
            if (go == null) return null;

            var comps = go.GetComponents<MonoBehaviour>();
            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (IsKnownScrollerComponent(comps[i]))
                        return comps[i];
                }
            }

            // Check immediate parent only (1 level)
            Transform parent = go.transform.parent;
            if (parent != null)
            {
                var parentComps = parent.GetComponents<MonoBehaviour>();
                if (parentComps != null)
                {
                    for (int i = 0; i < parentComps.Length; i++)
                    {
                        if (IsKnownScrollerComponent(parentComps[i]))
                            return parentComps[i];
                    }
                }
            }

            return null;
        }

        private static void InitReflection(Type componentType)
        {
            if (_reflectionInitialized) return;
            try
            {
                _scrollerOffsetField = componentType.GetField("offset", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _scrollerSpeedXField = componentType.GetField("scrollSpeedX", BindingFlags.Instance | BindingFlags.Public);
                _scrollerSpeedYField = componentType.GetField("scrollSpeedY", BindingFlags.Instance | BindingFlags.Public);
                _reflectionInitialized = true;
            }
            catch { }
        }

        /// <summary>
        /// Reads the live animated UV transform (scaleX, scaleY, offsetX, offsetY) for the current frame.
        /// Returns true if an active or non-identity transform is present.
        /// </summary>
        public static bool TryGetAnimatedUVTransform(Renderer renderer, MaterialPropertyBlock tempMpb, out Vector4 uvST)
        {
            uvST = new Vector4(1f, 1f, 0f, 0f);
            if (renderer == null) return false;

            bool hasOffset = false;

            try
            {
                // A. Base material tiling & scale
                var mat = renderer.sharedMaterial;
                if (mat != null)
                {
                    try
                    {
                        Vector2 matScale = mat.mainTextureScale;
                        if (matScale.x != 0f && matScale.y != 0f)
                        {
                            uvST.x = matScale.x;
                            uvST.y = matScale.y;
                        }

                        Vector2 matOffset = mat.mainTextureOffset;
                        if (matOffset.x != 0f || matOffset.y != 0f)
                        {
                            uvST.z += matOffset.x;
                            uvST.w += matOffset.y;
                            hasOffset = true;
                        }
                    }
                    catch { }
                }

                // B. Check MaterialPropertyBlock
                if (renderer.HasPropertyBlock())
                {
                    if (tempMpb == null) tempMpb = new MaterialPropertyBlock();
                    else tempMpb.Clear();

                    renderer.GetPropertyBlock(tempMpb);

                    // ULTRAKILL and generic scrollers: _ScrollOffset
                    Vector4 sOff = tempMpb.GetVector(PropScrollOffset);
                    if (sOff.x != 0f || sOff.y != 0f)
                    {
                        uvST.z += sOff.x;
                        uvST.w += sOff.y;
                        hasOffset = true;
                    }

                    // Standard Unity / URP tiling and offset: _MainTex_ST
                    Vector4 mtST = tempMpb.GetVector(PropMainTexST);
                    if (mtST.x != 0f || mtST.y != 0f || mtST.z != 0f || mtST.w != 0f)
                    {
                        if (mtST.x != 0f) uvST.x = mtST.x;
                        if (mtST.y != 0f) uvST.y = mtST.y;
                        if (mtST.z != 0f || mtST.w != 0f)
                        {
                            uvST.z += mtST.z;
                            uvST.w += mtST.w;
                            hasOffset = true;
                        }
                    }

                    Vector4 bmST = tempMpb.GetVector(PropBaseMapST);
                    if (bmST.x != 0f || bmST.y != 0f || bmST.z != 0f || bmST.w != 0f)
                    {
                        if (bmST.x != 0f) uvST.x = bmST.x;
                        if (bmST.y != 0f) uvST.y = bmST.y;
                        if (bmST.z != 0f || bmST.w != 0f)
                        {
                            uvST.z += bmST.z;
                            uvST.w += bmST.w;
                            hasOffset = true;
                        }
                    }

                    Vector4 tOff = tempMpb.GetVector(PropTextureOffset);
                    if (tOff.x != 0f || tOff.y != 0f)
                    {
                        uvST.z += tOff.x;
                        uvST.w += tOff.y;
                        hasOffset = true;
                    }

                    Vector4 off = tempMpb.GetVector(PropOffset);
                    if (off.x != 0f || off.y != 0f)
                    {
                        uvST.z += off.x;
                        uvST.w += off.y;
                        hasOffset = true;
                    }
                }

                // C. Direct fallback to ScrollingTexture component if MPB offset was not yet populated
                if (!hasOffset)
                {
                    var scrollerComp = FindScrollerComponent(renderer.gameObject);
                    if (scrollerComp != null && scrollerComp.GetType().Name.Equals("ScrollingTexture", StringComparison.OrdinalIgnoreCase))
                    {
                        InitReflection(scrollerComp.GetType());
                        if (_scrollerOffsetField != null)
                        {
                            object val = _scrollerOffsetField.GetValue(scrollerComp);
                            if (val is Vector2 v && (v.x != 0f || v.y != 0f))
                            {
                                uvST.z += v.x;
                                uvST.w += v.y;
                                hasOffset = true;
                            }
                        }

                        if (!hasOffset && _scrollerSpeedXField != null && _scrollerSpeedYField != null)
                        {
                            float sx = (float)_scrollerSpeedXField.GetValue(scrollerComp);
                            float sy = (float)_scrollerSpeedYField.GetValue(scrollerComp);
                            if (sx != 0f || sy != 0f)
                            {
                                uvST.z += Time.time * sx;
                                uvST.w += Time.time * sy;
                                hasOffset = true;
                            }
                        }
                    }
                }

                // Keep offsets bounded to prevent float32 precision degradation over long play sessions
                if (Math.Abs(uvST.z) > 1000.0f) uvST.z %= 1000.0f;
                if (Math.Abs(uvST.w) > 1000.0f) uvST.w %= 1000.0f;
            }
            catch { }

            return hasOffset;
        }
    }
}
