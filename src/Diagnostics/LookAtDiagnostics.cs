using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Logs what the camera's center is looking at (object, mesh, materials, shaders, vertex
    /// colors) whenever it changes, checked every 120 frames: ties a screenshot to the materials
    /// on screen. Two answers: the solid collider the view ray hits (UnityEngine.Physics through
    /// reflection, no PhysicsModule reference; trigger volumes ignored), and the nearest renderer
    /// UnityRTX draws along the ray (RemixFrameCapture.PickDrawnRenderer: objects without
    /// colliders too). And every surface the ray crosses within half a metre of the first (two
    /// at one distance fight in the path tracer). Main thread only.
    /// </summary>
    internal sealed class LookAtDiagnostics
    {
        private readonly ManualLogSource logger;
        private MethodInfo raycast;
        private object ignoreTriggers;
        private PropertyInfo hitCollider, hitPoint;
        private bool unavailable;
        private int lastTarget, lastDrawn;
        private SceneMeshScanner scanner;
        private RemixMaterialManager materials;

        public LookAtDiagnostics(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void Update(Camera camera, RemixFrameCapture capture, SceneMeshScanner scanner, RemixMaterialManager materials, int frameCount)
        {
            this.scanner = scanner;
            this.materials = materials;
            if (!VerboseLog.On || camera == null || frameCount % 120 != 0)
                return;
            var ray = new Ray(camera.transform.position, camera.transform.forward);

            if (capture != null)
            {
                var drawn = capture.PickDrawnRenderer(ray, camera.transform, out float distance);
                int id = drawn != null ? drawn.GetInstanceID() : 0;
                if (drawn != null && id != lastDrawn)
                    logger.LogInfo($"[LookAt] drawn '{drawn.name}' at {distance:F1} m (bounds): {Describe(drawn)}");
                lastDrawn = id;
            }

            try { LogSurfacesAlongRay(ray); }
            catch (Exception ex) { logger.LogInfo($"[LookAt] surfaces failed: {ex.Message}"); }

            if (unavailable)
                return;
            try
            {
                if (raycast == null && !Resolve())
                    return;
                var args = new object[] { ray, null, 1000f, ~0, ignoreTriggers };
                if (!(bool)raycast.Invoke(null, args))
                    return;
                var collider = hitCollider.GetValue(args[1]) as Component;
                if (collider == null)
                    return;
                var renderer = collider.GetComponent<Renderer>() ?? collider.GetComponentInParent<Renderer>() ?? collider.GetComponentInChildren<Renderer>();
                int target = renderer != null ? renderer.GetInstanceID() : collider.GetInstanceID();
                if (target == lastTarget)
                    return;
                lastTarget = target;
                var point = (Vector3)hitPoint.GetValue(args[1]);
                logger.LogInfo($"[LookAt] '{collider.name}' at {Vector3.Distance(camera.transform.position, point):F1} m: {Describe(renderer)}");
            }
            catch (Exception ex)
            {
                unavailable = true;
                logger.LogInfo($"[LookAt] unavailable: {ex.Message}");
            }
        }

        private bool Resolve()
        {
            var physics = Type.GetType("UnityEngine.Physics, UnityEngine.PhysicsModule") ?? Type.GetType("UnityEngine.Physics, UnityEngine");
            var hitType = Type.GetType("UnityEngine.RaycastHit, UnityEngine.PhysicsModule") ?? Type.GetType("UnityEngine.RaycastHit, UnityEngine");
            if (physics == null || hitType == null)
            {
                unavailable = true;
                logger.LogInfo("[LookAt] unavailable: no UnityEngine.Physics");
                return false;
            }
            // Raycast(Ray, out RaycastHit, float maxDistance, int layerMask, QueryTriggerInteraction)
            foreach (var m in physics.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var p = m.GetParameters();
                if (m.Name == "Raycast" && p.Length == 5 && p[0].ParameterType == typeof(Ray) &&
                    p[1].ParameterType == hitType.MakeByRefType() && p[2].ParameterType == typeof(float) &&
                    p[3].ParameterType == typeof(int) && p[4].ParameterType.IsEnum)
                {
                    raycast = m;
                    ignoreTriggers = Enum.ToObject(p[4].ParameterType, 1); // QueryTriggerInteraction.Ignore
                    break;
                }
            }
            hitCollider = hitType.GetProperty("collider");
            hitPoint = hitType.GetProperty("point");
            if (raycast == null || hitCollider == null || hitPoint == null)
            {
                unavailable = true;
                logger.LogInfo("[LookAt] unavailable: Physics.Raycast(Ray, out RaycastHit, float, int, QueryTriggerInteraction) not found");
                return false;
            }
            return true;
        }

        // The renderer as Unity has it now, then as the scene scan captured it (a static batch
        // can replace its mesh and materials after the scan).
        private string Describe(Renderer renderer)
        {
            string text = DescribeLive(renderer);
            string scanned = renderer != null ? scanner?.DescribeScanned(renderer) : null;
            return scanned != null ? $"{text} ||| scanned as {scanned}" : text;
        }

        private string DescribeLive(Renderer renderer)
        {
            if (renderer == null)
                return "no renderer";
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
            else { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) mesh = filter.sharedMesh; }
            var go = renderer.gameObject;
            var text = $"renderer '{renderer.name}' ({renderer.GetType().Name}) path '{Path(renderer.transform)}' layer {LayerMask.LayerToName(go.layer)}({go.layer}), shadows {renderer.shadowCastingMode}, mesh '{(mesh != null ? mesh.name : "-")}'";
            if (mesh != null)
            {
                text += $" {mesh.vertexCount} verts, readable {mesh.isReadable}, colors {UnityCompat.MeshHasAttribute(mesh, UnityCompat.AttributeColor)}";
                var t = renderer.transform;
                text += $", rotation {t.rotation.eulerAngles}, scale {t.lossyScale}";
            }
            var mats = renderer.sharedMaterials;
            for (int i = 0; mats != null && i < mats.Length; i++)
            {
                var m = mats[i];
                text += m != null ? $" | material {i} '{m.name}' shader '{(m.shader != null ? m.shader.name : "-")}' id {StaticGeometryDedupe.GetCanonicalMaterialId(m)} queue {m.renderQueue}{Properties(m)}" : $" | material {i} none";
                try { text += PropertyBlock(renderer, i, m); } catch { } // (MaterialPropertyBlock.Has* needs Unity 2021)
            }
            return text;
        }

        // A renderer's property block (what Animators and scripts set per renderer, the shared
        // material unchanged): the colours and floats it overrides.
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        // The block readers (HasColor, ShaderPropertyType) are newer than Unity 2018: the work
        // sits in its own method, which fails to compile there and is caught here.
        private static string PropertyBlock(Renderer renderer, int index, Material m)
        {
            try
            {
                return UnityCompat.HasShaderPropertyType ? ReadPropertyBlock(renderer, index, m) : "";
            }
            catch (Exception ex)
            {
                return $" block (unreadable: {ex.Message})";
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string ReadPropertyBlock(Renderer renderer, int index, Material m)
        {
            {
                if (!renderer.HasPropertyBlock())
                    return "";
                renderer.GetPropertyBlock(block, index);
                if (block.isEmpty)
                    renderer.GetPropertyBlock(block);
                if (block.isEmpty || m == null || m.shader == null)
                    return " block (empty)";
                var parts = new System.Collections.Generic.List<string>();
                int count = m.shader.GetPropertyCount();
                for (int p = 0; p < count; p++)
                {
                    string name = m.shader.GetPropertyName(p);
                    int id = Shader.PropertyToID(name);
                    var type = m.shader.GetPropertyType(p);
                    if (type == UnityEngine.Rendering.ShaderPropertyType.Color && block.HasColor(id))
                        parts.Add($"{name}={block.GetColor(id)}");
                    else if ((type == UnityEngine.Rendering.ShaderPropertyType.Float || type == UnityEngine.Rendering.ShaderPropertyType.Range) && block.HasFloat(id))
                        parts.Add($"{name}={block.GetFloat(id):G4}");
                    else if (type == UnityEngine.Rendering.ShaderPropertyType.Vector && block.HasVector(id))
                        parts.Add($"{name}={block.GetVector(id)}");
                }
                return $" block {{{string.Join("; ", parts)}}}";
            }
        }

        // Keywords and properties of a live material (when the material manager is known).
        private string Properties(Material m)
        {
            if (materials == null) return "";
            try { return $" keywords [{string.Join(" ", m.shaderKeywords)}] {{{materials.DescribeMaterialProperties(m)}}}{DumpMainTexture(m)}"; }
            catch { return ""; }
        }

        // The material's main texture, once per texture: format, mips and sampling logged, and
        // its top level written as raw RGBA8 (bottom row first) to BepInEx/LookAtTextures.
        private readonly System.Collections.Generic.HashSet<int> dumpedTextures = new System.Collections.Generic.HashSet<int>();

        private string DumpMainTexture(Material m)
        {
            if (!(m.mainTexture is Texture2D tex))
                return "";
            string info = $" mainTex '{tex.name}' {tex.width}x{tex.height} {tex.format} mips {UnityCompat.MipmapCount(tex)} filter {tex.filterMode} wrap {tex.wrapMode} readable {UnityCompat.IsReadable(tex)}";
            if (!dumpedTextures.Add(tex.GetInstanceID()))
                return info;
            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var previous = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LookAtTextures");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"{string.Join("_", tex.name.Split(System.IO.Path.GetInvalidFileNameChars()))}_{tex.width}x{tex.height}.rgba");
                System.IO.File.WriteAllBytes(file, copy.GetRawTextureData());
                UnityEngine.Object.Destroy(copy);
                return info + $" dumped '{file}'";
            }
            catch (Exception ex)
            {
                return info + $" dump failed: {ex.Message}";
            }
        }

        // Every mesh surface the view ray crosses within SurfaceWindow of the first: two
        // surfaces at one distance fight in the path tracer (a level's coplanar brushes, a
        // panel the game draws over a wall by queue or depth offset), where Unity's draw order
        // picks one. Static batches by their slice of the combined mesh (world space). Meshes
        // are read once (GPU readback where not readable); the renderer list per scene.
        private const float SurfaceWindow = 0.5f;
        private MeshRenderer[] sceneRenderers;
        private string sceneRenderersFor;
        private readonly Dictionary<int, KeyValuePair<Vector3[], int[][]>> meshGeometry =
            new Dictionary<int, KeyValuePair<Vector3[], int[][]>>();
        private readonly System.Collections.Generic.HashSet<int> describedSurfaces = new System.Collections.Generic.HashSet<int>();
        private string lastSurfaces;

        private void LogSurfacesAlongRay(Ray ray)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sceneRenderers == null || sceneRenderersFor != scene)
            {
                sceneRenderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
                sceneRenderersFor = scene;
                meshGeometry.Clear();
                describedSurfaces.Clear();
            }
            var hits = new System.Collections.Generic.List<KeyValuePair<float, string>>();
            var renderers = new System.Collections.Generic.List<KeyValuePair<float, MeshRenderer>>();
            float nearest = float.MaxValue;
            foreach (var r in sceneRenderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !r.bounds.IntersectRay(ray, out float boundsDistance) ||
                    boundsDistance > Math.Min(nearest + SurfaceWindow, 1000f))
                    continue;
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !Geometry(mesh, out var vertices, out var submeshes))
                    continue;
                bool batched = r.isPartOfStaticBatch;
                var materials = r.sharedMaterials;
                int start = batched ? r.subMeshStartIndex : 0;
                int end = batched ? Math.Min(start + materials.Length, submeshes.Length) : submeshes.Length;
                Matrix4x4 toWorld = batched ? Matrix4x4.identity : r.localToWorldMatrix;
                Matrix4x4 toLocal = toWorld.inverse;
                Vector3 o = toLocal.MultiplyPoint3x4(ray.origin), d = toLocal.MultiplyVector(ray.direction);
                float best = float.MaxValue;
                for (int s = start; s < end; s++)
                {
                    var tris = submeshes[s];
                    if (tris == null) continue;
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        if (!Intersect(o, d, vertices[tris[i]], vertices[tris[i + 1]], vertices[tris[i + 2]], out float t))
                            continue;
                        float distance = Vector3.Distance(ray.origin, toWorld.MultiplyPoint3x4(o + d * t));
                        if (distance > nearest + SurfaceWindow) continue;
                        nearest = Math.Min(nearest, distance);
                        best = Math.Min(best, distance);
                        var m = s - start < materials.Length ? materials[s - start] : null;
                        hits.Add(new KeyValuePair<float, string>(distance,
                            $"{distance:F3} m '{Path(r.transform)}' submesh {s - start} material '{(m != null ? m.name : "-")}' shader '{(m != null && m.shader != null ? m.shader.name : "-")}' queue {(m != null ? m.renderQueue : 0)}"));
                    }
                }
                if (best < float.MaxValue)
                    renderers.Add(new KeyValuePair<float, MeshRenderer>(best, r));
            }
            hits.RemoveAll(h => h.Key > nearest + SurfaceWindow);
            if (hits.Count < 2)
                return;
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));
            var text = string.Join(" | ", hits.ConvertAll(h => h.Value));
            if (text == lastSurfaces)
                return;
            lastSurfaces = text;
            logger.LogInfo($"[LookAt] surfaces within {SurfaceWindow} m: {text}");
            foreach (var kv in renderers)
                if (kv.Key <= nearest + SurfaceWindow && describedSurfaces.Add(kv.Value.GetInstanceID()))
                    logger.LogInfo($"[LookAt] surface at {kv.Key:F3} m: {Describe(kv.Value)}");
        }

        private bool Geometry(Mesh mesh, out Vector3[] vertices, out int[][] submeshes)
        {
            int id = mesh.GetInstanceID();
            if (meshGeometry.TryGetValue(id, out var cached))
            {
                vertices = cached.Key;
                submeshes = cached.Value;
                return vertices != null;
            }
            vertices = null;
            submeshes = null;
            try
            {
                if (mesh.isReadable)
                {
                    vertices = mesh.vertices;
                    submeshes = new int[mesh.subMeshCount][];
                    for (int s = 0; s < submeshes.Length; s++)
                        submeshes[s] = mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : null;
                }
                else if (!NativeMeshReader.ReadMeshFromGPU(mesh, out vertices, out _, out _, out submeshes))
                    vertices = null;
            }
            catch { vertices = null; }
            meshGeometry[id] = new KeyValuePair<Vector3[], int[][]>(vertices, submeshes);
            return vertices != null;
        }

        // Möller–Trumbore, both faces.
        private static bool Intersect(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 0;
        }

        private static string Path(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }
    }
}
