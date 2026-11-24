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
    // Static and scrolling meshes: renderers drawn each frame, the view-model camera's, mesh creation.
    public partial class RemixFrameCapture
    {
        /// <summary>
        /// Capture static meshes from scene
        /// </summary>
        // Each renderer's block colours when last seen, and the renderers whose changed.
        private readonly Dictionary<int, int> lastBlockColours = new Dictionary<int, int>();
        private readonly HashSet<int> animatedBlockColours = new HashSet<int>();

        private bool BlockColourAnimates(int rendererId, int blockHash)
        {
            if (animatedBlockColours.Contains(rendererId))
                return true;
            if (lastBlockColours.TryGetValue(rendererId, out int last) && last != blockHash)
            {
                animatedBlockColours.Add(rendererId);
                lastBlockColours.Remove(rendererId);
                return true;
            }
            lastBlockColours[rendererId] = blockHash;
            return false;
        }

        private static readonly int PropBlockColor = Shader.PropertyToID("_Color");
        private static readonly int PropBlockBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int PropBlockEmission = Shader.PropertyToID("_EmissionColor");

        // The colour and emission a renderer shows now, quantized (Quantize): its property
        // block's, else its runtime material copy's (runtimeMaterial; null for a shared one).
        private void ReadLiveColours(Renderer renderer, Material runtimeMaterial, out Color? colour, out Color? emission)
        {
            colour = null;
            emission = null;
            if (renderer.HasPropertyBlock())
            {
                if (sharedStaticMpb == null)
                    sharedStaticMpb = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(sharedStaticMpb);
                if (!sharedStaticMpb.isEmpty)
                {
                    Color c = sharedStaticMpb.GetColor(PropBlockColor);
                    if (c.a <= 0f && c.r <= 0f && c.g <= 0f && c.b <= 0f)
                        c = sharedStaticMpb.GetColor(PropBlockBaseColor);
                    if (c.a > 0f || c.r > 0f || c.g > 0f || c.b > 0f)
                        colour = Quantize(c);
                    Color e = sharedStaticMpb.GetColor(PropBlockEmission);
                    if (e.a > 0f || e.r > 0f || e.g > 0f || e.b > 0f)
                        emission = Quantize(e);
                }
            }
            if (runtimeMaterial == null)
                return;
            if (!colour.HasValue && runtimeMaterial.HasProperty(PropBlockColor))
                colour = Quantize(runtimeMaterial.GetColor(PropBlockColor));
            if (!emission.HasValue && runtimeMaterial.HasProperty(PropBlockEmission))
                emission = Quantize(runtimeMaterial.GetColor(PropBlockEmission));
        }

        private static int ColourBits(Color c)
        {
            Color32 q = c;
            return (q.a << 24) | (q.r << 16) | (q.g << 8) | q.b;
        }

        // A material with the renderer's live colours (ReadLiveColours), in 1/32 steps: a
        // material per step. Its id.
        private int CaptureLiveMaterial(Material mat, Renderer renderer, Color? colour, Color? emission)
        {
            int matId = mat.GetInstanceID();
            if (colour.HasValue)
                matId = HashCombine(matId, ColourBits(colour.Value));
            if (emission.HasValue)
                matId = HashCombine(matId, ColourBits(emission.Value) ^ 0x5EED);
            // Passed on only where they differ from the material's own (a block's): a runtime copy's
            // colours are the material's, captured from it for this step. Passed as overrides,
            // they also read as an emissive display override (a sea of _UseAlbedoAsEmissive
            // glowed white).
            if (colour.HasValue && mat.HasProperty(PropBlockColor) && Quantize(mat.GetColor(PropBlockColor)) == colour.Value)
                colour = null;
            if (emission.HasValue && mat.HasProperty(PropBlockEmission) && Quantize(mat.GetColor(PropBlockEmission)) == emission.Value)
                emission = null;
            materialManager.CaptureMaterialTextures(mat, matId, emission, null, null, colour, renderer);
            return matId;
        }

        // Meshes made at runtime (negative instance ids) whose vertices changed since last seen:
        // the game rewrites them (a trail it builds each frame), so the uploaded copy goes stale.
        // Their last vertex count and bounds; the renderers found rewritten.
        private readonly Dictionary<int, KeyValuePair<int, Bounds>> lastRuntimeMeshShapes = new Dictionary<int, KeyValuePair<int, Bounds>>();
        private readonly HashSet<int> rewrittenMeshRenderers = new HashSet<int>();

        private bool MeshIsRewritten(int rendererId, Mesh mesh, GameObject go)
        {
            if (rewrittenMeshRenderers.Contains(rendererId))
                return true;
            if (mesh.GetInstanceID() >= 0 || !mesh.isReadable)
                return false;
            var shape = new KeyValuePair<int, Bounds>(mesh.vertexCount, mesh.bounds);
            if (lastRuntimeMeshShapes.TryGetValue(rendererId, out var last) && (last.Key != shape.Key || last.Value != shape.Value))
            {
                lastRuntimeMeshShapes.Remove(rendererId);
                rewrittenMeshRenderers.Add(rendererId);
                if (VerboseLog.On)
                    logger.LogInfo($"[Rewritten] '{go.name}' mesh '{mesh.name}' changes at runtime: read each frame");
                return true;
            }
            lastRuntimeMeshShapes[rendererId] = shape;
            return false;
        }

        private readonly List<Vector3> rewrittenVertices = new List<Vector3>();
        private readonly List<Vector3> rewrittenNormals = new List<Vector3>();
        private readonly List<Vector2> rewrittenUvs = new List<Vector2>();
        private readonly List<Color32> rewrittenColors = new List<Color32>();
        private readonly List<int> rewrittenTriangles = new List<int>();

        // A rewritten mesh as it is this frame, uploaded again (bakeFrame), with its live colours.
        private void CaptureRewrittenMesh(MeshRenderer renderer, Mesh mesh, FrameState state, int frameCount)
        {
            int n = mesh.vertexCount;
            if (n < 3)
                return;
            mesh.GetVertices(rewrittenVertices);
            mesh.GetNormals(rewrittenNormals);
            mesh.GetUVs(0, rewrittenUvs);
            mesh.GetColors(rewrittenColors);
            var vertices = rewrittenVertices.ToArray();
            var uvs = rewrittenUvs.Count == n ? rewrittenUvs.ToArray() : new Vector2[n];
            var colors = rewrittenColors.Count == n ? rewrittenColors.ToArray() : null;
            var triangles = new int[mesh.subMeshCount][];
            for (int s = 0; s < triangles.Length; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                    continue;
                mesh.GetTriangles(rewrittenTriangles, s);
                if (rewrittenTriangles.Count >= 3)
                    triangles[s] = rewrittenTriangles.ToArray();
            }
            var normals = rewrittenNormals.Count == n ? rewrittenNormals.ToArray() : NativeMeshReader.ComputeFaceNormals(vertices, triangles);

            var materials = renderer.sharedMaterials;
            int rendererId = renderer.GetInstanceID();
            var firstMaterial = renderer.sharedMaterial;
            ReadLiveColours(renderer, IsRuntimeMaterial(firstMaterial) ? firstMaterial : null, out Color? colour, out Color? emission);
            Matrix4x4 localToWorld = SkyPlaced(renderer.gameObject.layer, renderer.transform.localToWorldMatrix);
            for (int s = 0; s < triangles.Length; s++)
            {
                var tris = triangles[s];
                Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                if (tris == null || mat == null || SceneMeshScanner.IsMaterialOrShaderInvisible(mat))
                    continue;
                int matId;
                var drawColors = colors;
                bool particleShader = mat.shader != null && mat.shader.name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
                if (particleShader)
                {
                    // A particle shader on the mesh (a trail): drawn as particle trails are, its
                    // vertex colours blended by the material's look, and its emission (which vertex
                    // colours do not tint) the colour of its most opaque vertex, in 1/15 steps.
                    ParticleLook look = GetParticleLook(mat, false);
                    Color vertexColour = Color.white;
                    if (colors != null)
                    {
                        int best = -1;
                        for (int v = 0; v < colors.Length; v++)
                            if (best < 0 || colors[v].a > colors[best].a)
                                best = v;
                        if (best >= 0)
                            vertexColour = colors[best];
                    }
                    bool emissive = look.IsAdditive;
                    Color glow = look.EmissionColor(vertexColour);
                    int r4 = Mathf.Clamp((int)(glow.r * 15f + 0.5f), 0, 15);
                    int g4 = Mathf.Clamp((int)(glow.g * 15f + 0.5f), 0, 15);
                    int b4 = Mathf.Clamp((int)(glow.b * 15f + 0.5f), 0, 15);
                    matId = unchecked(mat.GetInstanceID() * 397 ^ (emissive ? (1 << 12) | (r4 << 8) | (g4 << 4) | b4 : 0) ^ 0x52570000);
                    materialManager.CaptureMaterialTextures(mat, matId,
                        mpbEmissiveColor: emissive ? (Color?)new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f) : null,
                        mpbEmissiveIntensity: emissive ? 0.8f : 0f,
                        mpbColor: look.MaterialColor,
                        suppressLightGeneration: true,
                        renderer: renderer,
                        particleLike: true);
                    if (colors != null)
                    {
                        drawColors = (Color32[])colors.Clone();
                        look.ApplyTo(drawColors, drawColors.Length);
                    }
                }
                else
                    matId = CaptureLiveMaterial(mat, renderer, colour, emission);
                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.Rewritten,
                    meshId = rendererId ^ (s * 10007),
                    remixMeshHash = 0x52575254UL << 32 | ((uint)rendererId ^ ((uint)s * 0x9E3779B9U) ^ ((uint)matId * 0x85EBCA6BU)),
                    materialId = matId,
                    vertices = vertices,
                    normals = normals,
                    uvs = uvs,
                    colors = drawColors,
                    triangles = tris,
                    localToWorld = localToWorld,
                    boneTransforms = null,
                    skinningData = null,
                    uvST = materialManager.GetMainTexST(matId),
                    bakeFrame = frameCount,
                    // A particle shader's mesh blends as particles do (alpha over), not a surface.
                    categoryFlags = particleShader ? (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE : 0u,
                    glowFade = materialManager.GlowFade(matId, mat)
                });
            }
        }

        private static float QuantizeChannel(float v) => Mathf.Round(v * 32f) / 32f;

        private static Color Quantize(Color c) =>
            new Color(QuantizeChannel(c.r), QuantizeChannel(c.g), QuantizeChannel(c.b), QuantizeChannel(c.a));

        public void CaptureStaticMeshes(FrameState state, int frameCount)
        {
            if (invalidateRequested)
                InvalidateCaches();
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            TrackSpawnedObjects();

            // Get camera
            Camera mainCam = cameraHandler.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;

            if (mainCam != null)
            {
                EnsureCameraRenderersTracked(mainCam, frameCount);
            }
            
            // Capture camera
            if (mainCam != null)
            {
                var t = mainCam.transform;
                state.camera = new CameraData
                {
                    position = t.position,
                    forward = t.forward,
                    up = t.up,
                    right = t.right,
                    fov = mainCam.fieldOfView,
                    aspect = mainCam.aspect,
                    nearPlane = mainCam.nearClipPlane,
                    farPlane = mainCam.farClipPlane,
                    valid = true
                };
                var viewModelCam = Games.GameProfiles.FindViewModelCamera(mainCam);
                UpdateViewModelLayers(mainCam, viewModelCam);
                var vm = viewModelCam != null ? viewModelCam : mainCam;
                var vt = vm.transform;
                state.camera.viewModelPosition = vt.position;
                state.camera.viewModelForward = vt.forward;
                state.camera.viewModelUp = vt.up;
                state.camera.viewModelRight = vt.right;
                state.camera.viewModelFov = vm.fieldOfView;
                state.camera.viewModelNearPlane = vm.nearClipPlane;
                state.camera.viewModelFarPlane = vm.farClipPlane;
                state.camera.portalActive = Games.GameProfiles.GetRayPortalView(mainCam, out state.camera.portal);
                SceneMeshScanner.PortalExitRegion = state.camera.portalActive ? state.camera.portal : (Games.RayPortalView?)null;
                Games.GameProfiles.GetPortals(mainCam, framePortals);
                state.camera.portals = framePortals.Count > 0 ? framePortals.ToArray() : null;
                if (state.camera.portalActive || state.camera.portals != null)
                    RayPortals.Ensure(this, materialManager);
                CaptureSkybox(mainCam, ref state.camera);

                // Update skybox state on main thread
                skyboxManager?.UpdateSkybox(mainCam, frameCount);
            }
            
            // Debug toggle check
            if (!configCaptureStaticMeshes.Value)
            {
                if (mainCam != null)
                {
                    skyboxManager?.EmitSkyboxInstance(state, mainCam);
                }
                return;
            }

            // Static mesh frame skipping: reuse cached static instances if enabled and available
            int frameSkip = configStaticMeshFrameSkip != null ? configStaticMeshFrameSkip.Value : 1;
            if (frameSkip > 1 && (frameCount % frameSkip != 0) && cachedStaticInstances.Count > 0)
            {
                state.instances.AddRange(cachedStaticInstances);
                if (mainCam != null)
                {
                    CaptureCameraViewModelMeshes(state, mainCam);
                    CaptureAllScrollingMeshes(state, frameCount, camPos);
                    skyboxManager?.EmitSkyboxInstance(state, mainCam);
                }
                return;
            }

            cachedStaticInstances.Clear();
            staticCaptureCount++;
            int totalDrawn = 0;
            
            // Capture static meshes
            Transform mainCamTransform = mainCam != null ? mainCam.transform : null;
            float maxDistSqr = configMaxRenderDistance.Value * configMaxRenderDistance.Value;
            bool useDistCulling = configUseDistanceCulling.Value;
            bool useVisCulling = configUseVisibilityCulling.Value;

            foreach (var renderer in cachedRenderers)
            {
                if (renderer == null || !renderer.enabled || SceneMeshScanner.IsRenderingOff(renderer))
                    continue;
                // Drawn as a volume instead (LightShafts).
                if (LightShafts.Active && LightShafts.IsShaft(renderer))
                    continue;

                var info = GetOrCreateStaticInfo(renderer);
                if (info.isPartOfStaticBatch && !scrollingRendererIds.Contains(info.rendererId))
                {
                    AskScan(info.filter, info.gameObject);
                    continue;
                }

                var go = info.gameObject;
                if (go == null || !go.activeInHierarchy)
                    continue;

                int layer = go.layer;
                // The player's body (its held weapons' parts too): reflection-only, whatever its layer.
                bool playerBody = IsPlayerBodyRenderer(renderer);
                if (!playerBody && IsLayerDisabled(layer))
                    continue;
                if (previewRendererIds.Contains(info.rendererId))
                    continue; // a live image's model (CaptureImageCameraScene)

                int rendererInstanceId = info.rendererId;
                if (IsRendererDisabled(rendererInstanceId))
                    continue;

                var tr = info.transform;
                var lossy = tr.lossyScale;
                if (lossy.sqrMagnitude < 0.0001f)
                    continue;
                
                // Optional visibility culling (can cause issues in some games)
                if (useVisCulling && !renderer.isVisible)
                    continue;

                var meshFilter = info.filter;
                if (meshFilter == null)
                    continue;
                var mesh = meshFilter.sharedMesh;
                if (mesh == null)
                    continue;

                Bounds bounds = renderer.bounds;
                
                // Optional distance culling
                // (never the 3D skybox's: the distant land)
                if (useDistCulling && !IsSkyLayer(layer))
                {
                    float sqrDistance = (bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDistSqr)
                        continue;
                }
                
                int meshId = mesh.GetInstanceID();
                var materials = info.sharedMaterials;
                int baseMatSig = info.materialSignature;
                var dedupeKey = StaticGeometryDedupe.BuildKey(meshId, layer, baseMatSig, in bounds);

                // Animated / scrolling UV meshes (water, lava, conveyor belts, scrolling textures)
                // must be rendered dynamically via state.skinned with live UV offsets every frame.
                if (scrollingRendererIds.Contains(rendererInstanceId) || RemixScrollingTextureDetector.IsScrollingRenderer(renderer))
                {
                    if (scrollingRendererIds.Add(rendererInstanceId))
                    {
                        cachedScrollingRenderers.Add(renderer);
                        logger.LogInfo($"[ScrollingTexture] Registered scrolling renderer '{go.name}' (id={rendererInstanceId}, mesh='{mesh.name}') - total scrolling: {scrollingRendererIds.Count}");
                    }
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    CaptureScrollingMesh(renderer, mesh, state, frameCount);
                    continue;
                }

                // A mesh the game rewrites (a weapon trail built each frame): read each frame.
                if (MeshIsRewritten(rendererInstanceId, mesh, go))
                {
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    CaptureRewrittenMesh(renderer, mesh, state, frameCount);
                    continue;
                }

                if (ShouldPreferSceneScan(renderer, mesh))
                {
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    AskScan(meshFilter, go);
                    continue;
                }
                PrimitiveDiagnostics.Note(renderer, mesh, "Runtime");

                Texture mpbMainTex = null;
                Color? mpbColor = null;
                Color? mpbEmissive = null;
                int mpbHash = 0;

                if (sharedStaticMpb == null)
                    sharedStaticMpb = new MaterialPropertyBlock();
                else
                    sharedStaticMpb.Clear();

                if (renderer.HasPropertyBlock())
                {
                    renderer.GetPropertyBlock(sharedStaticMpb);

                    Vector4 sOff = sharedStaticMpb.GetVector(PropScrollOffset);
                    if (sOff.x != 0f || sOff.y != 0f)
                    {
                        if (scrollingRendererIds.Add(rendererInstanceId))
                        {
                            cachedScrollingRenderers.Add(renderer);
                        }
                        lock (persistentStaticLock)
                        {
                            persistentStaticInstances.Remove(rendererInstanceId);
                        }
                        CaptureScrollingMesh(renderer, mesh, state, frameCount);
                        continue;
                    }

                    Texture tex = sharedStaticMpb.GetTexture(PropMainTex);
                    if (tex == null) tex = sharedStaticMpb.GetTexture(PropBaseMap);
                    if (tex == null) tex = sharedStaticMpb.GetTexture(PropDiffuse);
                    if (tex == null) tex = sharedStaticMpb.GetTexture(PropTexture);
                    if (tex != null)
                    {
                        mpbMainTex = tex;
                        mpbHash = HashCombine(mpbHash, tex.GetInstanceID());
                    }
                    Color col = sharedStaticMpb.GetColor(PropColor);
                    if (col.a <= 0f && col.r <= 0f && col.g <= 0f && col.b <= 0f)
                        col = sharedStaticMpb.GetColor(PropBaseColor);
                    if (col.a > 0f || col.r > 0f || col.g > 0f || col.b > 0f)
                    {
                        // To 1/32 steps: a colour animated through the block (a fade) makes a
                        // material per step, not one per frame.
                        col = Quantize(col);
                        mpbColor = col;
                        Color32 c32 = col;
                        int colInt = (c32.a << 24) | (c32.r << 16) | (c32.g << 8) | c32.b;
                        mpbHash = HashCombine(mpbHash, colInt);
                    }
                    Color emis = sharedStaticMpb.GetColor(PropEmissiveColor);
                    if (emis.a > 0f || emis.r > 0f || emis.g > 0f || emis.b > 0f)
                    {
                        emis = Quantize(emis);
                        mpbEmissive = emis;
                        Color32 e32 = emis;
                        int emisInt = (e32.a << 24) | (e32.r << 16) | (e32.g << 8) | e32.b;
                        mpbHash = HashCombine(mpbHash, emisInt);
                    }
                    // Recolour masks' colours (weapon colours set through the block).
                    var blockMaterials = info.sharedMaterials;
                    for (int m = 0; blockMaterials != null && m < blockMaterials.Length; m++)
                    {
                        if (blockMaterials[m] != null && Games.GameProfiles.GetColorMask(blockMaterials[m], sharedStaticMpb, out var blockMask))
                            mpbHash = HashCombine(mpbHash, blockMask.Hash());
                        if (blockMaterials[m] != null && Games.GameProfiles.GetIridescence(blockMaterials[m], sharedStaticMpb, out var blockFilm))
                            mpbHash = HashCombine(mpbHash, blockFilm.Hash());
                    }
                }

                // A live colour that changes (animation fading or tinting a material through the
                // block, or a script its runtime copy): drawn as
                // the scrolling meshes are, its mesh uploaded once and a material bound per
                // colour step in the same frame. As a static mesh, each colour step would be a
                // new mesh, queued and ready frames later, the fade over before it shows.
                ReadLiveColours(renderer, info.runtimeMaterial ? info.primarySharedMaterial : null, out var liveColour, out var liveEmission);
                if ((liveColour.HasValue || liveEmission.HasValue) &&
                    BlockColourAnimates(rendererInstanceId, unchecked(ColourBits(liveColour ?? Color.clear) * 31 + ColourBits(liveEmission ?? Color.clear))))
                {
                    if (scrollingRendererIds.Add(rendererInstanceId))
                    {
                        cachedScrollingRenderers.Add(renderer);
                        if (VerboseLog.On)
                            logger.LogInfo($"[BlockColour] '{go.name}' animates its colour through its property block: drawn each frame with it");
                    }
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    CaptureScrollingMesh(renderer, mesh, state, frameCount);
                    continue;
                }

                int matSig = baseMatSig;
                if (mpbHash != 0)
                {
                    matSig = HashCombine(matSig, mpbHash);
                }
                ulong meshKey = RemixMeshConverter.GetMeshKey(meshId, matSig);
                bool isViewModel = !playerBody && IsViewModel(tr, layer, mainCamTransform, "mesh");
                uint categoryFlags = 0;
                if (isViewModel)
                {
                    categoryFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_VIEW_MODEL;
                }
                if (playerBody)
                    categoryFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_THIRD_PERSON_PLAYER_BODY;
                
                // Queue mesh for creation if not cached, not already queued, and not previously failed
                bool needsQueue;
                lock (meshQueueLock)
                {
                    needsQueue = !meshConverter.IsMeshCached(meshKey) && !meshesInQueue.Contains(meshKey) && !failedMeshKeys.Contains(meshKey);
                }
                
                if (needsQueue)
                {
                    Vector3[] vertices = null;
                    Vector3[] normals = null;
                    Vector2[] uvs = null;
                    Color32[] colors = null;
                    var submeshIndices = new List<uint[]>();
                    var submeshMaterials = new List<Material>();

                    if (mesh.isReadable)
                    {
                        try
                        {
                            vertices = mesh.vertices;
                            normals = mesh.normals;
                            uvs = mesh.uv;
                            colors = mesh.colors32;
                            if (colors != null && colors.Length == 0) colors = null;

                            int subMeshCount = mesh.subMeshCount;
                            for (int s = 0; s < subMeshCount; s++)
                            {
                                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                                    continue;
                                var subTris = mesh.GetTriangles(s);
                                if (subTris == null || subTris.Length == 0 || subTris.Length % 3 != 0)
                                    continue;
                                bool valid = true;
                                uint[] sIdx = new uint[subTris.Length];
                                for (int j = 0; j < subTris.Length; j++)
                                {
                                    if (subTris[j] < 0 || subTris[j] >= vertices.Length)
                                    {
                                        valid = false;
                                        break;
                                    }
                                    sIdx[j] = (uint)subTris[j];
                                }
                                if (valid)
                                {
                                    submeshIndices.Add(sIdx);
                                    Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                                    submeshMaterials.Add(mat);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (configDebugLogInterval.Value > 0)
                                logger.LogWarning($"Failed reading readable mesh '{mesh.name}': {ex.Message}");
                        }
                    }

                    if (vertices == null || vertices.Length == 0 || submeshIndices.Count == 0)
                    {
                        try
                        {
                            if (NativeMeshReader.ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out int[][] subTris))
                            {
                                if (LayeredMaterialCompositor.UsesVertexColorAsColor(materials))
                                    colors = SkinnedMeshGpuReader.TryReadColors(mesh);
                                submeshIndices.Clear();
                                submeshMaterials.Clear();
                                for (int s = 0; s < subTris.Length; s++)
                                {
                                    var tris = subTris[s];
                                    if (tris == null || tris.Length == 0 || tris.Length % 3 != 0)
                                        continue;
                                    bool valid = true;
                                    uint[] sIdx = new uint[tris.Length];
                                    for (int j = 0; j < tris.Length; j++)
                                    {
                                        if (tris[j] < 0 || tris[j] >= vertices.Length)
                                        {
                                            valid = false;
                                            break;
                                        }
                                        sIdx[j] = (uint)tris[j];
                                    }
                                    if (valid)
                                    {
                                        submeshIndices.Add(sIdx);
                                        Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                                        submeshMaterials.Add(mat);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (configDebugLogInterval.Value > 0)
                                logger.LogWarning($"GPU readback failed for mesh '{mesh.name}': {ex.Message}");
                        }
                    }

                    if (vertices == null || vertices.Length == 0 || submeshIndices.Count == 0)
                    {
                        lock (meshQueueLock) { failedMeshKeys.Add(meshKey); }
                        continue;
                    }

                    RemixMeshConverter.MapPanoramicSkies(ref vertices, ref normals, ref uvs, ref colors, submeshIndices, submeshMaterials);

                    int totalIndices = 0;
                    foreach (var sIdx in submeshIndices) totalIndices += sIdx.Length;
                    ulong meshHash = RemixMeshConverter.GenerateMeshHash(mesh.name, vertices, totalIndices, matSig);

                    // Capture material textures (pixel data gathered here, Remix API deferred to render thread)
                    List<int> submeshMaterialIds = null;
                    if (materials != null)
                    {
                        submeshMaterialIds = new List<int>(materials.Length);
                        for (int m = 0; m < materials.Length; m++)
                        {
                            if (materials[m] != null)
                            {
                                int matId = StaticGeometryDedupe.GetCanonicalMaterialId(materials[m]);
                                if (mpbHash != 0)
                                {
                                    matId = HashCombine(matId, mpbHash);
                                }
                                try
                                {
                                    var mMat = materials[m];
                                    if (mMat.HasProperty("_Hue") || mMat.HasProperty("_HueShift") || mMat.HasProperty("_HueAdjust") ||
                                        mMat.HasProperty("_Brightness") || mMat.HasProperty("_Contrast") || mMat.HasProperty("_Saturation") ||
                                        mMat.HasProperty("_HSBC"))
                                    {
                                        float h = mMat.HasProperty("_Hue") ? mMat.GetFloat("_Hue") :
                                                  (mMat.HasProperty("_HueShift") ? mMat.GetFloat("_HueShift") :
                                                  (mMat.HasProperty("_HueAdjust") ? mMat.GetFloat("_HueAdjust") : 0f));
                                        float b = mMat.HasProperty("_Brightness") ? mMat.GetFloat("_Brightness") : 0f;
                                        float c = mMat.HasProperty("_Contrast") ? mMat.GetFloat("_Contrast") : 1f;
                                        float s = mMat.HasProperty("_Saturation") ? mMat.GetFloat("_Saturation") : 1f;
                                        if (mMat.HasProperty("_HSBC"))
                                        {
                                            Vector4 v = mMat.GetVector("_HSBC");
                                            h = v.x; s = v.y; b = v.z; c = v.w;
                                        }

                                        if (Mathf.Abs(h) > 0.001f || Mathf.Abs(b) > 0.001f || Mathf.Abs(c - 1f) > 0.001f || Mathf.Abs(s - 1f) > 0.001f)
                                        {
                                            int hInt = Mathf.RoundToInt(h * 1000f);
                                            int bInt = Mathf.RoundToInt(b * 1000f);
                                            int cInt = Mathf.RoundToInt(c * 1000f);
                                            int sInt = Mathf.RoundToInt(s * 1000f);
                                            int hsbcHash = HashCombine(HashCombine(hInt, bInt), HashCombine(cInt, sInt));
                                            matId = HashCombine(matId, hsbcHash);
                                        }
                                    }
                                }
                                catch { }
                                var colorMask = ResolveColorMask(renderer, materials[m], ref matId, out var film);
                                submeshMaterialIds.Add(matId);
                                materialManager.CaptureMaterialTextures(materials[m], matId, mpbEmissive, null, mpbMainTex as Texture2D, mpbColor, renderer, colorMask: colorMask, film: film, coat: resolvedCoat);
                            }
                            else
                            {
                                submeshMaterialIds.Add(0);
                            }
                        }
                    }

                    // Queue pre-extracted mesh data
                    var preparedData = new PreparedMeshData
                    {
                        MeshKey = meshKey,
                        MeshId = meshId,
                        MeshName = mesh.name,
                        MeshHash = meshHash,
                        Vertices = vertices,
                        Normals = normals,
                        UVs = uvs,
                        Colors = colors,
                        SubmeshIndices = submeshIndices,
                        SubmeshMaterials = submeshMaterials,
                        CameraOnly = RemixMeshConverter.IsCameraOnly(submeshMaterials),
                        SubmeshMaterialIds = submeshMaterialIds,
                        ObjectUp = renderer.transform.InverseTransformDirection(Vector3.up),
                        ObjectScale = renderer.transform.lossyScale
                    };

                    lock (meshQueueLock)
                    {
                        if (isViewModel)
                        {
                            priorityMeshesToCreate.Enqueue(preparedData);
                        }
                        else
                        {
                            meshesToCreate.Enqueue(preparedData);
                        }
                        meshesInQueue.Add(meshKey);
                    }
                    
                    if (!isViewModel)
                        continue;
                }
                
                // Add instance
                var transform = SkyPlaced(layer, tr.localToWorldMatrix);
                var instanceData = new MeshInstanceData
                {
                    meshKey = meshKey,
                    meshId = meshId,
                    localToWorld = transform,
                    rendererInstanceId = rendererInstanceId,
                    dedupeKey = dedupeKey,
                    categoryFlags = categoryFlags
                };
                state.instances.Add(instanceData);
                totalDrawn++;
                
                // Remember this renderer's transform so we can keep drawing it if it gets disabled.
                // Do NOT persist viewmodels/weapons (they should disappear when unequipped), nor what
                // the game shows and hides by deactivating it (a flickering lamp's light shaft).
                if (!isViewModel)
                {
                    cachedStaticInstances.Add(instanceData);
                    if (!followsActiveState.TryGetValue(rendererInstanceId, out bool follows))
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        follows = filter != null && Games.GameProfiles.FollowsActiveState(filter, renderer);
                        followsActiveState[rendererInstanceId] = follows;
                    }
                    if (!follows)
                    {
                        lock (persistentStaticLock)
                        {
                            persistentStaticInstances[rendererInstanceId] = new PersistentStaticInstance
                            {
                                renderer = renderer,
                                meshKey = meshKey,
                                meshId = meshId,
                                localToWorld = transform,
                                dedupeKey = dedupeKey,
                                drawnFrame = frameCount
                            };
                        }
                    }
                }
            }
            
            // Draw persistent instances for disabled (but not destroyed) renderers.
            // This keeps objects visible that the game deactivates (e.g. CyberGrind cubes after wave settles).
            // Gated by config — disabled by default since games with scene variants (e.g. Stanley Parable)
            // use inactive GameObjects for alternate rooms that should NOT be rendered.
            int persistentDrawn = 0;
            if (configPersistDisabledRenderers.Value)
            {
                var keysToRemove = (List<int>)null;
                lock (persistentStaticLock)
                {
                    foreach (var kv in persistentStaticInstances)
                    {
                        var entry = kv.Value;
                        // Drawn by the loop above this frame: alive and active, nothing to check.
                        if (entry.drawnFrame == frameCount)
                            continue;

                        // Unity null check: renderer was destroyed
                        if (entry.renderer == null)
                        {
                            if (keysToRemove == null) keysToRemove = new List<int>();
                            keysToRemove.Add(kv.Key);
                            continue;
                        }
                        
                        // Skip if the renderer is active — it was already drawn above
                        if (entry.renderer.enabled && entry.renderer.gameObject.activeInHierarchy)
                            continue;

                        // Kept only for a disabled renderer or a hidden root (a room the game culls).
                        // Deactivated itself or under its root, the game has replaced or removed it
                        // (a ceiling swapped for its breaking pieces), as the scene scan judges it.
                        // Checked when it goes inactive, then staggered.
                        if (frameCount - entry.drawnFrame <= 2 || ((kv.Key + frameCount) & 15) == 0)
                        {
                            var go = entry.renderer.gameObject;
                            if (!go.activeInHierarchy && (!go.activeSelf || SceneMeshScanner.IsIntermediateAncestorDisabled(go.transform)))
                            {
                                if (keysToRemove == null) keysToRemove = new List<int>();
                                keysToRemove.Add(kv.Key);
                                continue;
                            }
                        }

                        var lossy = entry.renderer.transform.lossyScale;
                        if (lossy.sqrMagnitude < 0.0001f)
                        {
                            if (keysToRemove == null) keysToRemove = new List<int>();
                            keysToRemove.Add(kv.Key);
                            continue;
                        }

                        var meshFilter = entry.renderer.GetComponent<MeshFilter>();
                        var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                        if (ShouldPreferSceneScan(entry.renderer, mesh))
                        {
                            if (keysToRemove == null) keysToRemove = new List<int>();
                            keysToRemove.Add(kv.Key);
                            continue;
                        }
                        
                        // Skip if mesh isn't cached in Remix yet
                        if (!meshConverter.IsMeshCached(entry.meshKey))
                            continue;
                        
                        // Skip if layer is disabled by user
                        if (IsLayerDisabled(entry.renderer.gameObject.layer))
                            continue;
                        
                        // Distance culling using stored transform position
                        if (configUseDistanceCulling.Value)
                        {
                            float maxDist = configMaxRenderDistance.Value;
                            Vector3 objPos = new Vector3(entry.localToWorld.m03, entry.localToWorld.m13, entry.localToWorld.m23);
                            float sqrDistance = (objPos - camPos).sqrMagnitude;
                            if (sqrDistance > maxDist * maxDist)
                                continue;
                        }
                        
                        // Draw with last-known transform
                        var pInstanceData = new MeshInstanceData
                        {
                            meshKey = entry.meshKey,
                            meshId = entry.meshId,
                            localToWorld = entry.localToWorld,
                            rendererInstanceId = entry.renderer.GetInstanceID(),
                            dedupeKey = entry.dedupeKey
                        };
                        state.instances.Add(pInstanceData);
                        cachedStaticInstances.Add(pInstanceData);
                        persistentDrawn++;
                    }
                    if (keysToRemove != null)
                    {
                        foreach (var key in keysToRemove)
                            persistentStaticInstances.Remove(key);
                    }
                }
            }
            
            // Periodic tracking
            if (configDebugLogInterval.Value > 0 && staticCaptureCount % 300 == 1)
            {
                int persistentCount;
                lock (persistentStaticLock) { persistentCount = persistentStaticInstances.Count; }
                logger.LogInfo($"[StaticCapture] frame={frameCount} drawn={totalDrawn} persistent={persistentDrawn} total={persistentCount} queued={meshesToCreate.Count} failedMeshes={failedMeshKeys.Count}");
            }

            // Emit skybox instance into frame state
            if (mainCam != null)
            {
                skyboxManager?.EmitSkyboxInstance(state, mainCam);
            }
        }

        /// <summary>
        /// Captures camera-attached viewmodels/weapons on skipped frames so weapons never jitter or lag behind camera motion.
        /// </summary>
        private void CaptureCameraViewModelMeshes(FrameState state, Camera mainCam)
        {
            if (mainCam == null) return;
            var viewmodelRenderers = mainCam.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < viewmodelRenderers.Length; i++)
            {
                var renderer = viewmodelRenderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || SceneMeshScanner.IsRenderingOff(renderer))
                    continue;
                if (IsLayerDisabled(renderer.gameObject.layer) || IsRendererDisabled(renderer.GetInstanceID()) ||
                    SceneMeshScanner.IsRendererInvisible(renderer))
                    continue;
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                var mesh = meshFilter.sharedMesh;
                int meshId = mesh.GetInstanceID();
                int rendererInstanceId = renderer.GetInstanceID();
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);
                var materials = renderer.sharedMaterials;
                int matSig = StaticGeometryDedupe.ComputeMaterialSignature(materials);
                ulong meshKey = RemixMeshConverter.GetMeshKey(meshId, matSig);
                state.instances.Add(new MeshInstanceData
                {
                    meshKey = meshKey,
                    meshId = meshId,
                    localToWorld = renderer.transform.localToWorldMatrix,
                    rendererInstanceId = rendererInstanceId,
                    dedupeKey = dedupeKey,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_VIEW_MODEL
                });
            }
        }

        /// <summary>
        /// Retrieves or extracts cached base geometry for a scrolling UV mesh.
        /// Base geometry (positions, normals, base UVs, indices) is read only once.
        /// A renderer in a static batch draws the batch's combined mesh (world-space vertices):
        /// only its own submeshes, [subMeshStartIndex, + its materials), are its geometry; the
        /// rest are other objects' (drawn without a material, they came out as white boxes).
        /// Its slice is cached per renderer, compacted to the vertices it uses.
        /// submeshTriangles is indexed by the renderer's material slot (null: nothing to draw).
        /// </summary>
        private CachedScrollingMesh GetOrCreateScrollingMesh(MeshRenderer renderer, Mesh mesh)
        {
            if (mesh == null) return null;
            bool batched = renderer.isPartOfStaticBatch;
            int cacheId = batched ? renderer.GetInstanceID() : mesh.GetInstanceID();
            if (cachedScrollingMeshes.TryGetValue(cacheId, out var cached))
                return cached;

            int subStart = 0, subEnd = mesh.subMeshCount;
            if (batched)
            {
                var materials = renderer.sharedMaterials;
                subStart = renderer.subMeshStartIndex;
                subEnd = Math.Min(subStart + (materials != null ? materials.Length : 0), mesh.subMeshCount);
                if (subStart >= subEnd)
                {
                    cachedScrollingMeshes[cacheId] = null;
                    return null;
                }
            }

            Vector3[] vertices = null;
            Vector3[] normals = null;
            Vector2[] uvs = null;
            Color32[] colors = null;
            var submeshTriangles = new List<int[]>();

            if (mesh.isReadable)
            {
                try
                {
                    vertices = mesh.vertices;
                    normals = mesh.normals;
                    uvs = mesh.uv;
                    colors = mesh.colors32;
                    if (colors != null && colors.Length == 0) colors = null;

                    for (int s = subStart; s < subEnd; s++)
                    {
                        int[] tris = mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : null;
                        submeshTriangles.Add(tris != null && tris.Length > 0 && tris.Length % 3 == 0 ? tris : null);
                    }
                }
                catch { }
            }

            if (vertices == null || vertices.Length == 0 || submeshTriangles.TrueForAll(t => t == null))
            {
                try
                {
                    submeshTriangles.Clear();
                    if (NativeMeshReader.ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out int[][] subTris))
                    {
                        for (int s = subStart; s < subEnd; s++)
                        {
                            var tris = s < subTris.Length ? subTris[s] : null;
                            submeshTriangles.Add(tris != null && tris.Length > 0 && tris.Length % 3 == 0 ? tris : null);
                        }
                    }
                }
                catch { }
            }

            int[] firstTris = submeshTriangles.Find(t => t != null);
            if (vertices == null || vertices.Length == 0 || firstTris == null)
            {
                cachedScrollingMeshes[cacheId] = null;
                return null;
            }

            if (uvs == null || uvs.Length != vertices.Length)
                uvs = new Vector2[vertices.Length];

            if (normals == null || normals.Length != vertices.Length)
                normals = RemixMeshConverter.ComputeFaceNormals(vertices, firstTris);
            if (colors != null && colors.Length != vertices.Length)
                colors = null;

            if (batched)
            {
                // Only the vertices this renderer's slice uses, renumbered.
                var remap = new Dictionary<int, int>();
                var used = new List<int>();
                for (int s = 0; s < submeshTriangles.Count; s++)
                {
                    var tris = submeshTriangles[s];
                    if (tris == null) continue;
                    var local = new int[tris.Length];
                    for (int i = 0; i < tris.Length; i++)
                    {
                        if (!remap.TryGetValue(tris[i], out int v))
                        {
                            v = used.Count;
                            remap[tris[i]] = v;
                            used.Add(tris[i]);
                        }
                        local[i] = v;
                    }
                    submeshTriangles[s] = local;
                }
                var cv = new Vector3[used.Count];
                var cn = new Vector3[used.Count];
                var cu = new Vector2[used.Count];
                var cc = colors != null ? new Color32[used.Count] : null;
                for (int i = 0; i < used.Count; i++)
                {
                    int v = used[i];
                    cv[i] = vertices[v];
                    cn[i] = normals[v];
                    cu[i] = uvs[v];
                    if (cc != null) cc[i] = colors[v];
                }
                vertices = cv; normals = cn; uvs = cu; colors = cc;
            }

            cached = new CachedScrollingMesh
            {
                vertices = vertices,
                normals = normals,
                baseUVs = uvs,
                colors = colors,
                submeshTriangles = submeshTriangles
            };

            cachedScrollingMeshes[cacheId] = cached;
            return cached;
        }

        /// <summary>
        /// Captures a renderer with scrolling UVs and submits it to state.skinned with live animated UV coordinates.
        /// </summary>
        private void CaptureScrollingMesh(MeshRenderer renderer, Mesh mesh, FrameState state, int frameCount)
        {
            var cachedMesh = GetOrCreateScrollingMesh(renderer, mesh);
            if (cachedMesh == null)
                return;

            if (!RemixScrollingTextureDetector.TryGetAnimatedUVTransform(renderer, sharedStaticMpb, out Vector4 uvST))
            {
                uvST = new Vector4(1f, 1f, 0f, 0f);
            }

            var materials = renderer.sharedMaterials;
            int rendererId = renderer.GetInstanceID();
            var firstMaterial = renderer.sharedMaterial;
            ReadLiveColours(renderer, IsRuntimeMaterial(firstMaterial) ? firstMaterial : null, out Color? blockColour, out Color? blockEmission);
            // A static batch's vertices are already in world space.
            Matrix4x4 localToWorld = SkyPlaced(renderer.gameObject.layer,
                renderer.isPartOfStaticBatch ? Matrix4x4.identity : renderer.transform.localToWorldMatrix);

            for (int s = 0; s < cachedMesh.submeshTriangles.Count; s++)
            {
                var tris = cachedMesh.submeshTriangles[s];
                if (tris == null || tris.Length == 0) continue;

                Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                int matId = mat != null ? mat.GetInstanceID() : 0;
                if (mat != null)
                    matId = CaptureLiveMaterial(mat, renderer, blockColour, blockEmission);

                uint catFlags = 0;
                if (mat != null)
                {
                    string mName = mat.name;
                    if (!string.IsNullOrEmpty(mName) && mName.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        catFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_ANIMATED_WATER;
                    }
                }

                // The material too: a renderer swapping materials (an explosion's white flash, then
                // its own) is a new mesh, not the first material's kept.
                ulong remixMeshHash = 0x5343524CUL << 32 | ((uint)rendererId ^ ((uint)s * 0x9E3779B9U) ^ ((uint)matId * 0x85EBCA6BU));

                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.Scrolling,
                    meshId = rendererId ^ (s * 10007),
                    remixMeshHash = remixMeshHash,
                    materialId = matId,
                    vertices = cachedMesh.vertices,
                    normals = cachedMesh.normals,
                    uvs = cachedMesh.baseUVs,
                    colors = cachedMesh.colors,
                    triangles = tris,
                    localToWorld = localToWorld,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = catFlags,
                    uvST = uvST,
                    // The geometry is the mesh's own: uploaded once, scrolled by the draw (uvST).
                    bakeFrame = 1,
                    glowFade = materialManager.GlowFade(matId, mat),
                    cameraOnly = mat != null && (RemixMaterialManager.IsPanoramicSky(mat) || Games.GameProfiles.IsSkyDome(mat))
                });
            }
        }

        /// <summary>
        /// On frames where static geometry is skipped (StaticMeshFrameSkip > 1), captures all active
        /// scrolling UV meshes so animated water, lava, and conveyors continue moving smoothly.
        /// </summary>
        private void CaptureAllScrollingMeshes(FrameState state, int frameCount, Vector3 camPos)
        {
            for (int i = 0; i < cachedScrollingRenderers.Count; i++)
            {
                var r = cachedScrollingRenderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.transform.lossyScale.sqrMagnitude < 0.0001f) continue;
                if (IsLayerDisabled(r.gameObject.layer) || IsRendererDisabled(r.GetInstanceID())) continue;
                if (configUseVisibilityCulling.Value && !r.isVisible) continue;
                if (configUseDistanceCulling.Value && !IsSkyLayer(r.gameObject.layer))
                {
                    float maxDist = configMaxRenderDistance.Value;
                    if ((r.bounds.center - camPos).sqrMagnitude > maxDist * maxDist) continue;
                }
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    CaptureScrollingMesh(r, mf.sharedMesh, state, frameCount);
                }
            }
        }
        
        /// <summary>
        /// Process queued mesh creation (batched with frame time budget)
        /// </summary>
        public void ProcessMeshCreationBatch()
        {
            // Upload any textures queued by the main thread before creating meshes/materials
            materialManager.ProcessPendingTextureUploads();
            
            // First process high-priority viewmodel/weapon meshes immediately so they appear without delay
            while (true)
            {
                PreparedMeshData prioData = null;
                lock (meshQueueLock)
                {
                    if (priorityMeshesToCreate.Count > 0)
                        prioData = priorityMeshesToCreate.Dequeue();
                }
                if (prioData == null) break;
                
                ulong meshKey = prioData.MeshKey != 0 ? prioData.MeshKey : (ulong)(uint)prioData.MeshId;
                lock (meshQueueLock) { meshesInQueue.Remove(meshKey); }
                if (meshConverter.IsMeshCached(meshKey)) continue;
                
                try
                {
                    IntPtr handle = meshConverter.CreateRemixMeshFromPrepared(prioData);
                    if (handle == IntPtr.Zero)
                    {
                        if (configDebugLogInterval.Value > 0)
                            logger.LogWarning($"[MeshFail] Failed to create priority viewmodel mesh '{prioData.MeshName}'");
                        lock (meshQueueLock) { failedMeshKeys.Add(meshKey); }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Exception creating priority viewmodel mesh: {ex.Message}");
                }
            }

            // Adaptive batch size based on queue length
            int batchSize;
            lock (meshQueueLock)
            {
                batchSize = meshesToCreate.Count > 100 ? 3 : Math.Min(5, meshesToCreate.Count);
            }
            
            // Frame time budget: max 2ms for mesh creation
            var startTime = System.Diagnostics.Stopwatch.StartNew();
            const float maxMilliseconds = 2.0f;
            
            for (int i = 0; i < batchSize; i++)
            {
                PreparedMeshData meshData;
                lock (meshQueueLock)
                {
                    if (meshesToCreate.Count == 0) break;
                    meshData = meshesToCreate.Dequeue();
                }
                if (meshData == null) continue;
                
                ulong meshKey = meshData.MeshKey != 0 ? meshData.MeshKey : (ulong)(uint)meshData.MeshId;
                
                // Remove from queue tracking
                lock (meshQueueLock)
                {
                    meshesInQueue.Remove(meshKey);
                }
                
                if (meshConverter.IsMeshCached(meshKey))
                    continue;
                
                try
                {
                    // Create mesh with its pre-extracted data!
                    IntPtr handle = meshConverter.CreateRemixMeshFromPrepared(meshData);
                    
                    if (handle == IntPtr.Zero)
                    {
                        if (configDebugLogInterval.Value > 0)
                            logger.LogWarning($"[MeshFail] Failed to create mesh '{meshData.MeshName}' vertCount={meshData.Vertices?.Length} subMeshCount={meshData.SubmeshIndices?.Count}");
                        lock (meshQueueLock) { failedMeshKeys.Add(meshKey); }
                    }
                    
                    // Check time budget - break if exceeded
                    if (startTime.Elapsed.TotalMilliseconds > maxMilliseconds)
                        break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Exception creating mesh: {ex.Message}");
                }
            }
        }
    }
}
