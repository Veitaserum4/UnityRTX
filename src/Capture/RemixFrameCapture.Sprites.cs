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
                    if (sprite != null && trackedSpriteIds.Add(sprite.GetInstanceID()))
                        trackedSprites.Add(sprite);
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

                    // Unity binds the sprite's texture as _MainTex (a property block); the material
                    // gives the rest. One material per texture: an animation's frames may be one
                    // atlas or a texture each.
                    int spriteMatId = unchecked((mat.GetInstanceID() * 397 ^ texture.GetInstanceID()) * 31 ^ 0x53505254);
                    if (_capturedParticleMatIds.Add(spriteMatId))
                        materialManager.CaptureMaterialTextures(mat, spriteMatId, mpbMainTex: texture, renderer: sr);

                    int id = sr.GetInstanceID();
                    Color32 color = sr.color;
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
