using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityRemix
{
    // In-world screens and canvases: weapon screens, world-space UI, live camera images.
    public partial class RemixFrameCapture
    {
        private static void EnsureTmpReflection()
        {
            if (_tmpReflectionChecked) return;
            _tmpReflectionChecked = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("TMPro.TMP_Text");
                    if (t != null)
                    {
                        _tmpTextType = t;
                        _tmpMeshProperty = t.GetProperty("mesh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _tmpForceMeshUpdateMethod = t.GetMethod("ForceMeshUpdate", new Type[] { typeof(bool), typeof(bool) });
                        if (_tmpForceMeshUpdateMethod == null)
                            _tmpForceMeshUpdateMethod = t.GetMethod("ForceMeshUpdate", Type.EmptyTypes);
                        break;
                    }
                }
            }
            catch { }
        }

        private static bool IsExcludedHudElement(Graphic g)
        {
            Transform curr = g.transform;
            while (curr != null)
            {
                string name = curr.name;
                if (name.IndexOf("hud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("crosshair", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                curr = curr.parent;
            }
            return false;
        }

        private static bool IsEquippedWeaponElement(Graphic g)
        {
            // Must be rendered by a non-overlay Canvas that moves with the viewmodel/camera hierarchy
            var canvas = g.canvas;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return false;

            return true;
        }

        // World-space canvas screens, drawn path traced as emissive world UI: the screens on the
        // held weapons (placed with the view models) and screens in the level (shop terminals,
        // signs). Their graphics are culled from Unity's own canvas rendering when the UI
        // overlay's UI cameras would draw them a second time, flat over the image (canvases on
        // the UI layer). The capture generates their meshes itself, so culling does not affect it.
        private readonly HashSet<CanvasRenderer> culledWeaponScreens = new HashSet<CanvasRenderer>();
        // Metres: a screen off its surface, then each graphic above the graphics it overlaps that
        // its canvas paints earlier (Unity paints a canvas's graphics in hierarchy order, all on
        // one plane; path traced, overlapping ones would z-fight).
        private const float WeaponScreenLift = 0.002f, WeaponScreenLayerLift = 0.001f;
        private struct PlacedGraphic { public float xMin, yMin, xMax, yMax; public int layer; }
        private readonly Dictionary<Canvas, List<PlacedGraphic>> placedGraphics = new Dictionary<Canvas, List<PlacedGraphic>>();
        private readonly List<List<PlacedGraphic>> placedGraphicsPool = new List<List<PlacedGraphic>>();
        private readonly Vector3[] graphicCorners = new Vector3[4];
        private readonly HashSet<int> loggedWeaponScreens = new HashSet<int>();
        private int loggedWorldScreenGraphics;

        // Root world-space canvases in the level (not on a camera), refreshed every 30 frames.
        private readonly List<Canvas> worldCanvases = new List<Canvas>();
        private readonly List<Graphic> worldCanvasGraphics = new List<Graphic>();
        private readonly HashSet<int> loggedWorldCanvases = new HashSet<int>();
        private int worldCanvasRefreshFrame = -1000000;
        private const float WorldScreenMaxDistance = 80f;
        // Screens in the level glow less than the small weapon screens: their large bright
        // panels are also lit by the room, and drawn by Unity unlit at their plain colour.
        private const float WorldScreenEmission = 0.5f;

        // Live images: a raw image showing a camera's render texture (ULTRAKILL's shop item
        // previews: SmileRenderer, an orthographic camera on the image that draws models
        // placed just in front of it, at their true size, over a transparent background).
        // The models that camera sees are drawn where they are, path traced; the image quad
        // only when the camera clears to a visible colour. They are left out of the skinned
        // capture (previewRendererIds), which would draw them again.
        private readonly Dictionary<int, Camera> imageCameras = new Dictionary<int, Camera>();
        private readonly HashSet<int> previewRendererIds = new HashSet<int>();
        private readonly List<Renderer> imageSceneRenderers = new List<Renderer>();
        private readonly Dictionary<int, Mesh> previewBakedMeshes = new Dictionary<int, Mesh>();
        private readonly Dictionary<int, PreviewMesh> previewStaticMeshes = new Dictionary<int, PreviewMesh>();
        private readonly HashSet<int> loggedPreviewRenderers = new HashSet<int>();

        private sealed class PreviewMesh
        {
            public Vector3[] vertices, normals;
            public Vector2[] uvs;
            public Color32[] colors;
            public int[][] submeshes;
        }

        private Camera FindImageCamera(RawImage image, RenderTexture texture)
        {
            int id = image.GetInstanceID();
            if (imageCameras.TryGetValue(id, out var cam) && cam != null && cam.targetTexture == texture)
                return cam;
            cam = image.GetComponent<Camera>();
            if (cam == null || cam.targetTexture != texture)
            {
                cam = null;
                foreach (var c in Camera.allCameras)
                    if (c != null && c.targetTexture == texture) { cam = c; break; }
            }
            imageCameras[id] = cam;
            return cam;
        }

        private void CaptureImageCameraScene(FrameState state, Camera cam, int frameCount)
        {
            int mask = GameCameraMasks.Get(cam);
            var camT = cam.transform;
            var root = camT.parent != null ? camT.parent : camT;
            imageSceneRenderers.Clear();
            root.GetComponentsInChildren(false, imageSceneRenderers);
            Vector3 origin = camT.position, right = camT.right, up = camT.up, fwd = camT.forward;
            float halfH = cam.orthographic ? cam.orthographicSize : float.MaxValue;
            float halfW = cam.orthographic ? cam.orthographicSize * cam.aspect : float.MaxValue;
            for (int i = 0; i < imageSceneRenderers.Count; i++)
            {
                var r = imageSceneRenderers[i];
                if (r == null || !r.enabled || ((mask >> r.gameObject.layer) & 1) == 0)
                    continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                    continue;
                // In the camera's view volume.
                var b = r.bounds;
                float ext = b.extents.magnitude;
                Vector3 d = b.center - origin;
                float z = Vector3.Dot(d, fwd);
                if (z < cam.nearClipPlane - ext || z > cam.farClipPlane + ext ||
                    Mathf.Abs(Vector3.Dot(d, right)) > halfW + ext || Mathf.Abs(Vector3.Dot(d, up)) > halfH + ext)
                    continue;
                previewRendererIds.Add(r.GetInstanceID());
                CapturePreviewRenderer(state, r, frameCount);
            }
        }

        private void CapturePreviewRenderer(FrameState state, Renderer r, int frameCount)
        {
            int rid = r.GetInstanceID();
            var t = r.transform;
            PreviewMesh data;
            Matrix4x4 localToWorld;
            int bakeFrame;
            var skinned = r as SkinnedMeshRenderer;
            if (skinned != null)
            {
                if (skinned.sharedMesh == null)
                    return;
                if (!previewBakedMeshes.TryGetValue(rid, out var baked) || baked == null)
                {
                    baked = new Mesh();
                    baked.MarkDynamic();
                    previewBakedMeshes[rid] = baked;
                }
                UnityCompat.BakeMeshScaled(skinned, baked);
                data = new PreviewMesh
                {
                    vertices = baked.vertices,
                    normals = baked.normals,
                    uvs = baked.uv,
                    colors = baked.colors32,
                    submeshes = new int[baked.subMeshCount][]
                };
                for (int sm = 0; sm < data.submeshes.Length; sm++)
                    data.submeshes[sm] = baked.GetTriangles(sm);
                // Whether the bake holds the renderer's scale varies (Unity version, scaled
                // parents): the scale that matches Unity's own world bounds is applied. Wrong,
                // a preview drawn at a canvas's tiny scale shows full size, and swings around
                // its turntable's pivot instead of turning in place.
                Vector3 ls = t.lossyScale;
                float uniform = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
                float bakedSize = baked.bounds.size.magnitude, worldSize = r.bounds.size.magnitude;
                bool applyScale = bakedSize > 1e-6f && worldSize > 1e-6f && uniform > 1e-6f &&
                    Mathf.Abs(Mathf.Log(worldSize / (bakedSize * uniform))) < Mathf.Abs(Mathf.Log(worldSize / bakedSize));
                localToWorld = Matrix4x4.TRS(t.position, t.rotation, applyScale ? ls : Vector3.one);
                if (!loggedPreviewRenderers.Contains(rid))
                    logger.LogInfo($"[WorldScreen] live image model '{r.name}': baked {bakedSize:F3}, world bounds {worldSize:F3}, lossy scale {ls}, scale applied {applyScale}");
                bakeFrame = frameCount;
            }
            else
            {
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    return;
                int meshKey = mesh.GetInstanceID();
                if (!previewStaticMeshes.TryGetValue(meshKey, out data))
                {
                    data = new PreviewMesh();
                    if (mesh.isReadable)
                    {
                        data.vertices = mesh.vertices;
                        data.normals = mesh.normals;
                        data.uvs = mesh.uv;
                        data.colors = mesh.colors32;
                        data.submeshes = new int[mesh.subMeshCount][];
                        for (int sm = 0; sm < data.submeshes.Length; sm++)
                            data.submeshes[sm] = mesh.GetTriangles(sm);
                    }
                    else if (!NativeMeshReader.ReadMeshFromGPU(mesh, out data.vertices, out data.normals, out data.uvs, out data.submeshes))
                        data.vertices = null;
                    previewStaticMeshes[meshKey] = data;
                }
                localToWorld = t.localToWorldMatrix;
                bakeFrame = (meshKey & 0x7FFFFFFF) | 1;
            }
            if (data.vertices == null || data.vertices.Length == 0 || data.submeshes == null)
                return;
            int n = data.vertices.Length;
            Vector3[] normals = data.normals != null && data.normals.Length == n ? data.normals : null;
            Vector2[] uvs = data.uvs != null && data.uvs.Length == n ? data.uvs : null;
            Color32[] colors = data.colors != null && data.colors.Length == n ? data.colors : null;

            var mats = r.sharedMaterials;
            for (int sub = 0; sub < data.submeshes.Length; sub++)
            {
                var tris = data.submeshes[sub];
                if (tris == null || tris.Length == 0 || mats == null || mats.Length == 0)
                    continue;
                var mat = mats[Mathf.Min(sub, mats.Length - 1)];
                if (mat == null)
                    continue;
                int matId;
                if (skinned != null)
                    matId = ResolveMaterial(skinned, mat);
                else
                {
                    matId = StaticGeometryDedupe.GetCanonicalMaterialId(mat);
                    var colorMask = ResolveColorMask(r, mat, ref matId, out var film);
                    materialManager.CaptureMaterialTextures(mat, matId, renderer: r, colorMask: colorMask, film: film, coat: resolvedCoat);
                }
                if (loggedPreviewRenderers.Add(rid))
                    logger.LogInfo($"[WorldScreen] live image model '{r.name}' ({r.GetType().Name}): {n} verts, {data.submeshes.Length} submeshes, material '{mat.name}' shader '{(mat.shader != null ? mat.shader.name : "-")}'");
                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.MeshBaked,
                    meshId = (int)((uint)rid ^ ((uint)sub * 0x9E3779B9u)),
                    remixMeshHash = 0x4000000000000000UL | ((ulong)(uint)rid << 8) | (uint)sub,
                    materialId = matId,
                    vertices = data.vertices,
                    normals = normals,
                    uvs = uvs,
                    colors = colors,
                    triangles = tris,
                    localToWorld = localToWorld,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = 0,
                    bakeFrame = bakeFrame
                });
            }
        }

        private void UncullWeaponScreens()
        {
            foreach (var cr in culledWeaponScreens)
                if (cr != null)
                    cr.cull = false;
            culledWeaponScreens.Clear();
        }

        private void CaptureCanvasScreens(FrameState state, int frameCount)
        {
            bool cullScreens = RemixUIOverlay.CapturesUICameras;
            if (!cullScreens && culledWeaponScreens.Count > 0)
                UncullWeaponScreens();
            else if (culledWeaponScreens.Count > 0)
                culledWeaponScreens.RemoveWhere(cr => cr == null);

            Camera mainCam = cameraHandler?.GetPreferredCamera();
            if (mainCam == null)
                return;

            EnsureTmpReflection();
            foreach (var list in placedGraphics.Values)
            {
                list.Clear();
                placedGraphicsPool.Add(list);
            }
            placedGraphics.Clear();

            // Screens on the held weapons (under the camera).
            var graphics = mainCam.GetComponentsInChildren<Graphic>(false);
            for (int i = 0; graphics != null && i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null)
                    continue;
                // Reject anything attached to the 2D HUD or overlay
                if (IsExcludedHudElement(g))
                    continue;
                if (!IsEquippedWeaponElement(g))
                    continue;
                CaptureCanvasGraphic(state, g, mainCam, frameCount, onViewModel: true, cull: cullScreens);
            }

            CaptureWorldCanvasScreens(state, mainCam, frameCount, cullScreens);
        }

        private void CaptureWorldCanvasScreens(FrameState state, Camera mainCam, int frameCount, bool cullScreens)
        {
            if (frameCount - worldCanvasRefreshFrame >= 30 || frameCount < worldCanvasRefreshFrame)
            {
                worldCanvasRefreshFrame = frameCount;
                worldCanvases.Clear();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.isRootCanvas || c.renderMode != RenderMode.WorldSpace)
                        continue;
                    // On a camera: the weapon screens above, or HUD canvases the UI overlay draws.
                    if (c.GetComponentInParent<Camera>() != null)
                        continue;
                    worldCanvases.Add(c);
                }
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            Vector3 camPos = mainCam.transform.position;
            float maxDistSqr = WorldScreenMaxDistance * WorldScreenMaxDistance;
            for (int c = 0; c < worldCanvases.Count; c++)
            {
                var canvas = worldCanvases[c];
                if (canvas == null || !canvas.isActiveAndEnabled)
                    continue;
                if ((canvas.transform.position - camPos).sqrMagnitude > maxDistSqr)
                    continue;

                worldCanvasGraphics.Clear();
                canvas.GetComponentsInChildren(false, worldCanvasGraphics);
                if (loggedWorldCanvases.Add(canvas.GetInstanceID()))
                    LogWorldCanvas(canvas);

                // Only canvases on the UI layer are also drawn by the overlay's UI cameras.
                bool cull = cullScreens && canvas.gameObject.layer == uiLayer;
                for (int i = 0; i < worldCanvasGraphics.Count; i++)
                    CaptureCanvasGraphic(state, worldCanvasGraphics[i], mainCam, frameCount,
                                         onViewModel: (viewModelLayers & (1 << canvas.gameObject.layer)) != 0, cull: cull);
            }
        }

        // Once per world canvas: its graphics, and the renderers and cameras beside it (a shop's
        // item previews can be models, or a camera rendering into a RawImage's texture).
        private void LogWorldCanvas(Canvas canvas)
        {
            var t = canvas.transform;
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            var rect = canvas.GetComponent<RectTransform>();
            Vector3 size = rect != null ? Vector3.Scale(rect.rect.size, rect.lossyScale) : Vector3.zero;
            logger.LogInfo($"[WorldScreen] canvas '{path}' layer {LayerMask.LayerToName(canvas.gameObject.layer)}, {size.x:F2} x {size.y:F2} m, " +
                           $"event camera '{(canvas.worldCamera != null ? canvas.worldCamera.name : "-")}', {worldCanvasGraphics.Count} active graphics");

            var scope = t.parent != null ? t.parent : t;
            int logged = 0;
            foreach (var cam in scope.GetComponentsInChildren<Camera>(true))
            {
                var target = cam.targetTexture;
                logger.LogInfo($"[WorldScreen]   camera '{cam.name}' active {cam.isActiveAndEnabled}, mask 0x{cam.cullingMask:X}, " +
                               $"target {(target != null ? $"'{target.name}' {target.width}x{target.height}" : "screen")}");
            }
            foreach (var r in scope.GetComponentsInChildren<Renderer>(true))
            {
                if (logged++ >= 80) { logger.LogInfo("[WorldScreen]   ..."); break; }
                Mesh mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                string rpath = r.name;
                for (var p = r.transform.parent; p != null && p != scope; p = p.parent)
                    rpath = p.name + "/" + rpath;
                logger.LogInfo($"[WorldScreen]   renderer '{rpath}' ({r.GetType().Name}) layer {LayerMask.LayerToName(r.gameObject.layer)}({r.gameObject.layer}), " +
                               $"active {r.gameObject.activeInHierarchy}, enabled {r.enabled}, mesh '{(mesh != null ? mesh.name : "-")}', " +
                               $"material '{(r.sharedMaterial != null ? r.sharedMaterial.name : "-")}'");
            }
            foreach (var raw in canvas.GetComponentsInChildren<RawImage>(true))
            {
                var tex = raw.texture;
                logger.LogInfo($"[WorldScreen]   raw image '{raw.name}' active {raw.isActiveAndEnabled}, texture {(tex != null ? $"'{tex.name}' ({tex.GetType().Name})" : "none")}");
            }
        }

        // Places a graphic above the graphics of its canvas that it overlaps and that paint
        // before it (callers go in hierarchy pre-order, the canvas's painting order).
        private int PlaceGraphic(Graphic g)
        {
            var canvas = g.canvas;
            if (!placedGraphics.TryGetValue(canvas, out var placed))
            {
                if (placedGraphicsPool.Count > 0)
                {
                    placed = placedGraphicsPool[placedGraphicsPool.Count - 1];
                    placedGraphicsPool.RemoveAt(placedGraphicsPool.Count - 1);
                }
                else
                    placed = new List<PlacedGraphic>();
                placedGraphics[canvas] = placed;
            }

            g.rectTransform.GetWorldCorners(graphicCorners);
            var canvasTransform = canvas.transform;
            var box = new PlacedGraphic { xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue };
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = canvasTransform.InverseTransformPoint(graphicCorners[i]);
                box.xMin = Mathf.Min(box.xMin, p.x); box.xMax = Mathf.Max(box.xMax, p.x);
                box.yMin = Mathf.Min(box.yMin, p.y); box.yMax = Mathf.Max(box.yMax, p.y);
            }
            int layer = 0;
            for (int i = 0; i < placed.Count; i++)
            {
                var o = placed[i];
                if (o.layer >= layer && box.xMin < o.xMax && o.xMin < box.xMax && box.yMin < o.yMax && o.yMin < box.yMax)
                    layer = o.layer + 1;
            }
            box.layer = layer;
            placed.Add(box);
            return layer;
        }

        private void CaptureCanvasGraphic(FrameState state, Graphic g, Camera mainCam, int frameCount, bool onViewModel, bool cull)
        {
            if (g == null || !g.enabled || !g.gameObject.activeInHierarchy)
                return;

            var canvas = g.canvas;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return;

            var screenRenderer = g.canvasRenderer;
            // Culled by a RectMask2D (scrolled out of view), unless culled here.
            if (screenRenderer != null && screenRenderer.cull && !culledWeaponScreens.Contains(screenRenderer))
                return;

            Color col = g.color;
            // CanvasGroup fades (a shop page switching) multiply in.
            if (screenRenderer != null)
                col.a *= UnityCompat.GetInheritedAlpha(screenRenderer);
            if (col.a <= 0.001f)
                return;

            if (cull && screenRenderer != null && !screenRenderer.cull)
            {
                screenRenderer.cull = true;
                culledWeaponScreens.Add(screenRenderer);
            }

            if (g is RawImage liveImage && liveImage.texture is RenderTexture liveTexture)
            {
                var liveCam = FindImageCamera(liveImage, liveTexture);
                if (liveCam == null)
                    return;
                CaptureImageCameraScene(state, liveCam, frameCount);
                Color clear = liveCam.backgroundColor;
                if (liveCam.clearFlags != CameraClearFlags.SolidColor || clear.a <= 0.001f)
                    return; // transparent: only its models show
                col = new Color(clear.r, clear.g, clear.b, col.a * clear.a);
            }

            Mesh mesh = null;
            try
            {
                if (_tmpTextType != null && _tmpTextType.IsInstanceOfType(g))
                {
                    if (_tmpForceMeshUpdateMethod != null)
                    {
                        var parms = _tmpForceMeshUpdateMethod.GetParameters();
                        if (parms.Length == 2)
                            _tmpForceMeshUpdateMethod.Invoke(g, new object[] { false, false });
                        else
                            _tmpForceMeshUpdateMethod.Invoke(g, null);
                    }
                    if (_tmpMeshProperty != null)
                        mesh = (Mesh)_tmpMeshProperty.GetValue(g, null);
                }

                if (mesh == null || mesh.vertexCount == 0)
                {
                    if (_doMeshGenerationMethod != null)
                        _doMeshGenerationMethod.Invoke(g, null);
                    if (_workerMeshProperty != null)
                        mesh = (Mesh)_workerMeshProperty.GetValue(null);
                }
            }
            catch { }

            if (mesh == null || mesh.vertexCount == 0)
            {
                if (screenRenderer != null)
                    mesh = UnityCompat.GetCanvasMesh(screenRenderer);
            }

            Vector3[] verts = null;
            Vector3[] normals = null;
            Vector2[] uvs = null;
            Color32[] colors = null;
            int[] tris = null;

            if (mesh != null && mesh.vertexCount > 0 && mesh.isReadable)
            {
                try
                {
                    verts = mesh.vertices;
                    normals = mesh.normals;
                    uvs = mesh.uv;
                    colors = mesh.colors32;
                    tris = mesh.triangles;
                }
                catch { }
            }

            if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
            {
                Rect r = g.rectTransform.rect;
                if (r.width <= 0.001f || r.height <= 0.001f)
                    return;

                verts = new Vector3[]
                {
                    new Vector3(r.xMin, r.yMin, 0f),
                    new Vector3(r.xMin, r.yMax, 0f),
                    new Vector3(r.xMax, r.yMax, 0f),
                    new Vector3(r.xMax, r.yMin, 0f)
                };
                normals = new Vector3[]
                {
                    Vector3.back, Vector3.back, Vector3.back, Vector3.back
                };
                uvs = new Vector2[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(1f, 0f)
                };
                colors = new Color32[] { col, col, col, col };
                tris = new int[]
                {
                    0, 1, 2,
                    0, 2, 3
                };
            }

            if (normals == null || normals.Length != verts.Length)
            {
                normals = new Vector3[verts.Length];
                for (int n = 0; n < normals.Length; n++)
                    normals[n] = Vector3.back;
            }

            if (uvs == null || uvs.Length != verts.Length)
            {
                uvs = new Vector2[verts.Length];
            }

            if (colors == null || colors.Length != verts.Length)
            {
                colors = new Color32[verts.Length];
                Color32 c = col;
                for (int cIdx = 0; cIdx < colors.Length; cIdx++)
                    colors[cIdx] = c;
            }

            Material mat = g.materialForRendering;
            if (mat == null)
                mat = g.defaultMaterial;
            if (mat == null)
                return;

            Texture2D mainTex = g.mainTexture as Texture2D;
            // TextMeshPro reports UnityWhite as its texture; its glyphs are in its material's atlas.
            if (_tmpTextType != null && _tmpTextType.IsInstanceOfType(g) && mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") is Texture2D atlas)
                mainTex = atlas;

            Color32 c32 = col;
            int qR = (c32.r >> 3);
            int qG = (c32.g >> 3);
            int qB = (c32.b >> 3);
            int qA = (c32.a >> 3);
            int colHash = (qA << 15) | (qR << 10) | (qG << 5) | qB;
            int texHash = mainTex != null ? mainTex.GetInstanceID() : 0;
            float emission = onViewModel ? 1.0f : WorldScreenEmission;
            int uiMatId = HashCombine(HashCombine(mat.GetInstanceID(), HashCombine(texHash, colHash)), onViewModel ? 1 : 2);

            materialManager.CaptureMaterialTextures(
                mat,
                uiMatId,
                mpbEmissiveColor: col,
                mpbEmissiveIntensity: emission,
                mpbMainTex: mainTex,
                mpbColor: col,
                suppressLightGeneration: true
            );

            int graphicId = g.GetInstanceID();
            ulong remixMeshHash = (ulong)(uint)graphicId | 0x8000000000000000UL;

            // A screen lies on a surface, and its graphics on one plane (Unity draws UI in
            // hierarchy order, without depth between them); path traced they z-fight. Each
            // is lifted toward the camera, on the side it faces, above what it overlaps.
            int layer = PlaceGraphic(g);
            var rect = g.rectTransform;
            Vector3 facing = rect.forward;
            if (Vector3.Dot(mainCam.transform.position - rect.position, facing) > 0f)
                facing = -facing; // mirrored canvas: its visible side is +Z
            Matrix4x4 screenToWorld = Matrix4x4.Translate(-facing * (WeaponScreenLift + layer * WeaponScreenLayerLift)) * rect.localToWorldMatrix;
            if (VerboseLog.On && loggedWeaponScreens.Add(graphicId) && (onViewModel || loggedWorldScreenGraphics++ < 300))
            {
                Vector3 size = Vector3.Scale(rect.rect.size, rect.lossyScale);
                var shownTex = g.mainTexture;
                logger.LogInfo($"[{(onViewModel ? "WeaponScreen" : "WorldScreen")}] '{g.name}' ({g.GetType().Name}) under '{(rect.parent != null ? rect.parent.name : "-")}' " +
                               $"canvas '{canvas.name}' ({canvas.renderMode}, layer {LayerMask.LayerToName(g.gameObject.layer)}): " +
                               $"{size.x:F3} x {size.y:F3} m, {Vector3.Distance(mainCam.transform.position, rect.position):F3} m from the camera, " +
                               $"facing dot {Vector3.Dot((mainCam.transform.position - rect.position).normalized, rect.forward):F2}, mirrored {facing != rect.forward}, layer {layer}, " +
                               $"texture {(shownTex != null ? $"'{shownTex.name}' ({shownTex.GetType().Name})" : "none")}");
            }

            state.skinned.Add(new SkinnedMeshData
            {
                source = DynamicSource.WorldUI,
                meshId = graphicId,
                remixMeshHash = remixMeshHash,
                materialId = uiMatId,
                vertices = verts,
                normals = normals,
                uvs = uvs,
                colors = colors,
                triangles = tris,
                localToWorld = screenToWorld,
                boneTransforms = null,
                skinningData = null,
                // On a held weapon (under the camera): placed with the view models.
                categoryFlags = onViewModel
                    ? (uint)(RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI |
                             RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_VIEW_MODEL)
                    : (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI,
                bakeFrame = frameCount
            });
        }
    }
}
