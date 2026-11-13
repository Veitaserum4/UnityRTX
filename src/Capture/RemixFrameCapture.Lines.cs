using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    // Line renderers: ropes, chains, electricity lines and beams, baked each frame as Unity draws
    // them (camera-facing where they face the view).
    public partial class RemixFrameCapture
    {
        private readonly List<LineRenderer> trackedLines = new List<LineRenderer>();
        private readonly HashSet<int> trackedLineIds = new HashSet<int>();
        private Mesh lineBakeMesh;

        public void CaptureLineRenderers(FrameState state, int frameCount)
        {
            Camera mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
            if (mainCam == null || !mainCam.gameObject.activeInHierarchy || !mainCam.enabled || Time.frameCount < 10)
                return;

            // New lines (spawned beams, a level's ropes) found every few frames; destroyed ones pruned.
            if (frameCount % 4 == 0)
            {
                foreach (var line in UnityCompat.FindActiveSceneComponents<LineRenderer>())
                    if (line != null && trackedLineIds.Add(line.GetInstanceID()))
                        trackedLines.Add(line);
            }
            if (frameCount % 180 == 0)
            {
                for (int i = trackedLines.Count - 1; i >= 0; i--)
                    if (trackedLines[i] == null)
                        trackedLines.RemoveAt(i);
                trackedLineIds.Clear();
                foreach (var line in trackedLines)
                    trackedLineIds.Add(line.GetInstanceID());
            }
            if (trackedLines.Count == 0)
                return;

            if (lineBakeMesh == null)
                lineBakeMesh = new Mesh { name = "LineRendererBakeMesh" };

            Vector3 camPos = mainCam.transform.position;
            bool cull = configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value;
            float maxDist = configParticleMaxDistance != null && configParticleMaxDistance.Value > 0f ? configParticleMaxDistance.Value : 60f;

            foreach (var line in trackedLines)
            {
                if (line == null || !line.enabled || !line.gameObject.activeInHierarchy || line.positionCount < 2)
                    continue;
                if (IsLayerDisabled(line.gameObject.layer) || IsRendererDisabled(line.GetInstanceID()))
                    continue;
                if (cull && (line.bounds.ClosestPoint(camPos) - camPos).sqrMagnitude > maxDist * maxDist)
                    continue;
                Material mat = line.sharedMaterial;
                if (mat == null)
                    continue;

                try
                {
                    lineBakeMesh.Clear();
                    line.BakeMesh(lineBakeMesh, mainCam, true);
                    var verts = lineBakeMesh.vertices;
                    var tris = lineBakeMesh.triangles;
                    if (verts == null || verts.Length < 3 || tris == null || tris.Length < 3)
                        continue;
                    // BakeMesh leaves out the transform's position (rotation and scale only): placed
                    // where the renderer's world bounds say it is.
                    lineBakeMesh.RecalculateBounds();
                    Vector3 offset = line.bounds.center - lineBakeMesh.bounds.center;

                    // The line's colour (its vertices carry the gradient), one material per colour.
                    Color lineColor = (line.startColor + line.endColor) * 0.5f;
                    int r4 = Mathf.Clamp((int)(lineColor.r * 15f + 0.5f), 0, 15);
                    int g4 = Mathf.Clamp((int)(lineColor.g * 15f + 0.5f), 0, 15);
                    int b4 = Mathf.Clamp((int)(lineColor.b * 15f + 0.5f), 0, 15);
                    int colorKey = (r4 << 8) | (g4 << 4) | b4;
                    int lineMatId = unchecked(mat.GetInstanceID() * 397 ^ colorKey ^ 0x4C494E45);
                    if (_capturedParticleMatIds.Add(lineMatId))
                    {
                        // Additive lines (electricity, beams) glow; others (ropes, chains) are lit surfaces.
                        bool additive = IsAdditiveLineMaterial(mat);
                        Color quantized = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);
                        materialManager.CaptureMaterialTextures(mat, lineMatId,
                            mpbEmissiveColor: additive ? quantized : (Color?)null,
                            mpbEmissiveIntensity: additive ? 0.8f : (float?)null,
                            mpbColor: quantized,
                            suppressLightGeneration: true,
                            renderer: line);
                    }

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.Trail,
                        meshId = line.GetInstanceID(),
                        remixMeshHash = (ulong)(uint)line.GetInstanceID() | 0x4C494E0000000000UL,
                        materialId = lineMatId,
                        vertices = verts,
                        normals = lineBakeMesh.normals,
                        uvs = lineBakeMesh.uv,
                        colors = lineBakeMesh.colors32,
                        triangles = tris,
                        localToWorld = Matrix4x4.Translate(offset),
                        boneTransforms = null,
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = frameCount
                    });
                }
                catch (Exception ex)
                {
                    if (VerboseLog.On)
                        logger.LogWarning($"CaptureLineRenderers failed on '{line.name}': {ex.Message}");
                }
            }
        }

        private static bool IsAdditiveLineMaterial(Material mat)
        {
            string shader = mat.shader != null ? mat.shader.name : "";
            if (shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return mat.HasProperty("_DstBlend") && Mathf.RoundToInt(mat.GetFloat("_DstBlend")) == (int)UnityEngine.Rendering.BlendMode.One;
        }
    }
}
