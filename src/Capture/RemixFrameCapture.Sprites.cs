using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    // Sprite renderers: flat sprites in the world (fires, glows, signs), often animated by an
    // Animator swapping the sprite each frame and turned to the camera by a script. Drawn as
    // Unity draws them: the current sprite's own outline mesh, textured from its atlas.
    public partial class RemixFrameCapture
    {
        private readonly List<SpriteRenderer> trackedSprites = new List<SpriteRenderer>();
        private readonly HashSet<int> trackedSpriteIds = new HashSet<int>();

        // A sprite's mesh in Remix's terms (shared by every renderer showing it; read-only once made).
        private sealed class SpriteGeometry
        {
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector2[] uvs;
            public int[] triangles;
        }
        private readonly Dictionary<int, SpriteGeometry> spriteGeometry = new Dictionary<int, SpriteGeometry>();

        // Each renderer's vertex colours, its colour: a new array when the colour changes, as the
        // render thread may still be reading the last.
        private readonly Dictionary<int, Color32[]> spriteColors = new Dictionary<int, Color32[]>();

        // An additive sprite's coverage scale (its emission scaled up to match).
        private const float AdditiveSpriteCoverage = 0.5f;

        public void CaptureSpriteRenderers(FrameState state, int frameCount)
        {
            Camera mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
            if (mainCam == null || !mainCam.gameObject.activeInHierarchy || !mainCam.enabled || Time.frameCount < 10)
                return;

            // New sprites (a lit fire taken from a pool, a level's torches) found every few frames;
            // destroyed ones pruned.
            if (frameCount % 4 == 0)
            {
                foreach (var sprite in UnityCompat.FindActiveSceneComponents<SpriteRenderer>())
                {
                    if (sprite == null || !trackedSpriteIds.Add(sprite.GetInstanceID()))
                        continue;
                    trackedSprites.Add(sprite);
                    // Single Window draws nothing with the game's cameras: an animator culled
                    // with its renderer would hold a flipbook (a fire just lit) on one frame until
                    // the next scene-wide sweep (RefreshRendererCache) let it run.
                    var animator = sprite.GetComponent<Animator>();
                    if (animator != null && animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
            if (frameCount % 180 == 0)
            {
                for (int i = trackedSprites.Count - 1; i >= 0; i--)
                    if (trackedSprites[i] == null)
                        trackedSprites.RemoveAt(i);
                trackedSpriteIds.Clear();
                foreach (var sprite in trackedSprites)
                    trackedSpriteIds.Add(sprite.GetInstanceID());
                spriteColors.Clear();
                if (spriteGeometry.Count > 1024)
                    spriteGeometry.Clear();
            }
            if (trackedSprites.Count == 0)
                return;

            Vector3 camPos = mainCam.transform.position;
            bool cull = configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value;
            float maxDist = configParticleMaxDistance != null && configParticleMaxDistance.Value > 0f ? configParticleMaxDistance.Value : 60f;

            foreach (var sr in trackedSprites)
            {
                if (sr == null || !sr.enabled || !sr.gameObject.activeInHierarchy)
                    continue;
                Sprite sprite = sr.sprite;
                Texture2D texture = sprite != null ? sprite.texture : null;
                if (texture == null || sr.color.a <= 0f)
                    continue;
                if (IsLayerDisabled(sr.gameObject.layer) || IsRendererDisabled(sr.GetInstanceID()))
                    continue;
                if (cull && (sr.bounds.ClosestPoint(camPos) - camPos).sqrMagnitude > maxDist * maxDist)
                    continue;
                Material mat = sr.sharedMaterial;
                if (mat == null || SceneMeshScanner.IsMaterialOrShaderInvisible(mat))
                    continue;

                try
                {
                    var geometry = GetSpriteGeometry(sprite);
                    if (geometry == null)
                        continue;

                    // As a particle's: the material's blend and tint, the renderer's colour its
                    // vertex colours. Sprite shaders are unlit (bar a lit one's, which takes the
                    // scene's light): they emit their colour, quantized as a particle's is.
                    ParticleLook look = GetParticleLook(mat, false);
                    bool emissive = !look.IsMultiply && !IsLitSpriteShader(mat);
                    int colorKey = 0;
                    Color emission = Color.black;
                    if (emissive)
                    {
                        emission = look.EmissionColor(sr.color);
                        int r4 = Mathf.Clamp((int)(emission.r * 15f + 0.5f), 0, 15);
                        int g4 = Mathf.Clamp((int)(emission.g * 15f + 0.5f), 0, 15);
                        int b4 = Mathf.Clamp((int)(emission.b * 15f + 0.5f), 0, 15);
                        colorKey = (1 << 12) | (r4 << 8) | (g4 << 4) | b4;
                        emission = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);
                    }

                    // Unity binds the sprite's texture as _MainTex (a property block). One material
                    // per texture: an animation's frames may be one atlas or a texture each.
                    int spriteMatId = unchecked(((mat.GetInstanceID() * 397 ^ texture.GetInstanceID()) * 31 ^ colorKey) * 31 ^ 0x53505254);
                    // An additive sprite adds its light over what is behind it; OpenRemix blends it
                    // over, its brightness its coverage, so a bright one (a fire's white-hot
                    // flipbook) would stand solid. Half the coverage at twice the emission adds
                    // the same light and leaves the rest showing through.
                    float coverage = look.IsAdditive ? AdditiveSpriteCoverage : 1f;
                    if (_capturedParticleMatIds.Add(spriteMatId))
                    {
                        materialManager.CaptureMaterialTextures(mat, spriteMatId,
                            mpbEmissiveColor: emissive ? emission : (Color?)null,
                            mpbEmissiveIntensity: emissive ? 0.8f / coverage : (float?)null,
                            mpbMainTex: texture,
                            mpbColor: look.MaterialColor,
                            suppressLightGeneration: true,
                            renderer: sr);
                    }

                    int id = sr.GetInstanceID();
                    Color32 color = look.Vertex(sr.color);
                    color.a = (byte)Mathf.RoundToInt(color.a * coverage);
                    if (!spriteColors.TryGetValue(id, out var colors) || colors.Length != geometry.vertices.Length ||
                        !colors[0].Equals(color))
                    {
                        colors = new Color32[geometry.vertices.Length];
                        for (int i = 0; i < colors.Length; i++)
                            colors[i] = color;
                        spriteColors[id] = colors;
                    }

                    // flipX / flipY mirror the sprite about its pivot.
                    Matrix4x4 localToWorld = sr.transform.localToWorldMatrix;
                    if (sr.flipX || sr.flipY)
                        localToWorld *= Matrix4x4.Scale(new Vector3(sr.flipX ? -1f : 1f, sr.flipY ? -1f : 1f, 1f));

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.Sprite,
                        meshId = id,
                        remixMeshHash = (ulong)(uint)id | 0x5350520000000000UL,
                        materialId = spriteMatId,
                        vertices = geometry.vertices,
                        normals = geometry.normals,
                        uvs = geometry.uvs,
                        colors = colors,
                        triangles = geometry.triangles,
                        localToWorld = localToWorld,
                        boneTransforms = null,
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = frameCount
                    });
                }
                catch (Exception ex)
                {
                    if (VerboseLog.On)
                        logger.LogWarning($"CaptureSpriteRenderers failed on '{sr.name}': {ex.Message}");
                }
            }
        }

        // Sprite shaders that take the scene's light (Sprites/Diffuse, URP's Sprite-Lit-Default).
        private static bool IsLitSpriteShader(Material mat)
        {
            string s = mat.shader != null ? mat.shader.name : "";
            return s.IndexOf("Diffuse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("Sprite-Lit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private SpriteGeometry GetSpriteGeometry(Sprite sprite)
        {
            int key = sprite.GetInstanceID();
            if (spriteGeometry.TryGetValue(key, out var geometry))
                return geometry;

            Vector2[] verts2 = sprite.vertices;
            ushort[] tris16 = sprite.triangles;
            Vector2[] uvs = sprite.uv;
            if (verts2 == null || verts2.Length < 3 || tris16 == null || tris16.Length < 3 || uvs == null || uvs.Length != verts2.Length)
            {
                spriteGeometry[key] = null;
                return null;
            }
            geometry = new SpriteGeometry
            {
                vertices = new Vector3[verts2.Length],
                normals = new Vector3[verts2.Length],
                uvs = uvs,
                triangles = new int[tris16.Length]
            };
            // A sprite faces -Z (towards a camera looking down +Z at it).
            for (int i = 0; i < verts2.Length; i++)
            {
                geometry.vertices[i] = verts2[i];
                geometry.normals[i] = Vector3.back;
            }
            for (int i = 0; i < tris16.Length; i++)
                geometry.triangles[i] = tris16[i];
            spriteGeometry[key] = geometry;
            return geometry;
        }
    }
}
