using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityRemix
{
    /// <summary>
    /// Scans the Unity scene graph to find all static mesh geometry, including
    /// inactive GameObjects that the runtime capture misses. Rescans periodically
    /// after scene load to catch objects that arrive via async/Addressable loading.
    /// Streams mesh creation to the render thread to avoid frame hitches.
    /// </summary>
    public class SceneMeshScanner
    {
        private readonly ManualLogSource logger;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        private readonly object apiLock;

        private struct SubMeshSurface
        {
            public int[] Indices;
            public int MaterialId;
        }

        [Flags]
        public enum InstanceFlags : byte
        {
            None = 0,
            IsCombinedMesh = 1 << 0,
            // The game toggles it by deactivation (GameProfile.FollowsActiveState).
            FollowsActiveState = 1 << 1,
            // Seen active in the hierarchy since the scan: only such an object is kept while
            // inactive (one the game hid after showing it, a CyberGrind block). Geometry inactive
            // from the start (a later state, a level's hidden black copies of its walls) never is.
            SeenActive = 1 << 2,
            HasMoved = 1 << 3,
            IsScaleZero = 1 << 4,
            // Inactive in hierarchy: AncestorDisabled caches the activeSelf / ancestor walk
            // (UpdateVisibility), redone when it goes inactive and then every 16th frame.
            WasInactive = 1 << 5,
            AncestorDisabled = 1 << 6,
        }

        private struct ScannedRendererRef
        {
            public Renderer Renderer;
            public GameObject GameObject;
            public Transform Transform;
        }

        private struct ScannedMeshData
        {
            public ulong MeshHash;
            public StaticGeometryKey DedupeKey;
            public int RendererInstanceId;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] UVs;
            public Color32[] Colors;
            public SubMeshSurface[] Surfaces;
            public Matrix4x4 LocalToWorld;
            public int Layer;
            public Renderer SourceRenderer;
            public GameObject SourceGameObject;
            public Transform SourceTransform;
            public InstanceFlags Flags;
            // World up in the mesh's space: which faces take a top-colored material's top variant.
            public Vector3 ObjectUp;
            // Its object's scale (quantized as in the shared hash): projected UVs repeat per world unit.
            public Vector3 ObjectScale;
            // Only camera rays meet its instances (a panoramic sky; RemixMeshConverter.IsCameraOnly).
            public bool CameraOnly;
        }

        public struct InstanceData
        {
            public IntPtr MeshHandle;
            public StaticGeometryKey DedupeKey;
            public int RendererInstanceId;
            public RemixAPI.remixapi_Transform Transform;
            public Matrix4x4 LastLocalToWorld;
            public Vector3 LocalBoundsCenter;
            public int Layer;
            public Vector3 BoundsCenter;
            public InstanceFlags Flags;
            public bool IsCombinedMesh => (Flags & InstanceFlags.IsCombinedMesh) != 0;
        }

        public struct DedupeEntry
        {
            public StaticGeometryKey DedupeKey;
            public int RendererInstanceId;
            public int Layer;
        }

        // Streaming: main thread pushes extracted mesh data, render thread drains batches
        private readonly Queue<ScannedMeshData> streamingQueue = new Queue<ScannedMeshData>();
        private readonly object streamLock = new object();
        private volatile bool streamingActive;

        // Completed instances drawn every frame
        private readonly List<InstanceData> currentInstances = new List<InstanceData>();
        private readonly List<ScannedRendererRef> instanceRenderers = new List<ScannedRendererRef>();
        private readonly List<InstanceData> reusableVisibleInstances = new List<InstanceData>();
        private readonly object instanceLock = new object();

        private sealed class InstanceSnapshot
        {
            public InstanceData[] Buffer = new InstanceData[256];
            public int Count;
        }

        // Visibility-filtered snapshot: built on main thread, read on render thread
        private InstanceSnapshot visibleSnapshot;
        private readonly InstanceSnapshot snapshotA = new InstanceSnapshot();
        private readonly InstanceSnapshot snapshotB = new InstanceSnapshot();
        private bool useSnapshotA = true;
        private int clearGeneration = 0;

        // Mesh handle dedup: same geometry → same Remix handle
        private readonly Dictionary<ulong, IntPtr> meshHandles = new Dictionary<ulong, IntPtr>();

        // Diagnostics (LookAtDiagnostics): what each renderer was scanned as, formatted when asked
        // (a level scans thousands). Main thread.
        private struct ScanRecord
        {
            public string meshName;
            public int vertexCount;
            public bool colors;
            public Material[] materials;
            public int[] materialIds;
        }
        private readonly Dictionary<int, ScanRecord> scannedDescriptions = new Dictionary<int, ScanRecord>();

        public string DescribeScanned(Renderer renderer)
        {
            if (renderer == null || !scannedDescriptions.TryGetValue(renderer.GetInstanceID(), out var record))
                return null;
            var described = new System.Text.StringBuilder($"mesh '{record.meshName}' {record.vertexCount} verts, colors {record.colors}");
            for (int s = 0; s < record.materialIds.Length; s++)
            {
                var m = record.materials[s];
                described.Append($" | surface {s} material '{(m != null ? m.name : "-")}' shader '{(m != null && m.shader != null ? m.shader.name : "-")}' id {record.materialIds[s]}");
            }
            string scanned = described.ToString();
            var text = new System.Text.StringBuilder(scanned);
            foreach (System.Text.RegularExpressions.Match id in System.Text.RegularExpressions.Regex.Matches(scanned, @" id (-?\d+)"))
            {
                if (materialManager.TryDescribeMaterial(int.Parse(id.Groups[1].Value), out string description))
                    text.Append($" || material {id.Groups[1].Value}: {description}");
            }
            text.Append(DescribeInstances(renderer));
            return text.ToString();
        }

        // The scanned instances of a renderer as drawn now: why one is left out of the frame.
        private string DescribeInstances(Renderer renderer)
        {
            var text = new System.Text.StringBuilder();
            lock (instanceLock)
            {
                var snapshot = Volatile.Read(ref visibleSnapshot);
                int found = 0;
                for (int i = 0; i < currentInstances.Count && i < instanceRenderers.Count; i++)
                {
                    if (instanceRenderers[i].Renderer != renderer)
                        continue;
                    var inst = currentInstances[i];
                    bool visible = false;
                    if (snapshot != null)
                        for (int v = 0; v < snapshot.Count && !visible; v++)
                            visible = snapshot.Buffer[v].MeshHandle == inst.MeshHandle && snapshot.Buffer[v].RendererInstanceId == inst.RendererInstanceId;
                    var go = instanceRenderers[i].GameObject;
                    text.Append($" || instance {i}: handle {(inst.MeshHandle != IntPtr.Zero ? "yes" : "NONE")}, scan layer {inst.Layer}{(IsLayerExcluded(inst.Layer) ? " (EXCLUDED)" : "")}, " +
                                $"flags {inst.Flags}, enabled {renderer.enabled}, renderingOff {IsRenderingOff(renderer)}, active {(go != null && go.activeInHierarchy)}, " +
                                $"bounds centre {inst.BoundsCenter}, in the visible list {visible}");
                    found++;
                }
                if (found == 0)
                    text.Append($" || NO INSTANCE drawn for it ({currentInstances.Count} instances)");
            }
            return text.ToString();
        }

        // Track which MeshFilter instance IDs we've already scanned (avoids duplicates across rescans)
        private readonly HashSet<int> scannedFilterIds = new HashSet<int>();

        // Layer exclusion callback (from frameCapture)
        private readonly Func<int, bool> isLayerDisabled;

        // When true, skip inactive renderers during scan (saves memory, prevents scanning ghost geometry)
        private bool scanActiveOnly;

        public bool ScanActiveOnly
        {
            get => scanActiveOnly;
            set => scanActiveOnly = value;
        }

        /// <summary>Layers only the game's view-model camera draws (set by RemixFrameCapture each frame).</summary>
        public static int ViewModelLayers;
        /// <summary>The game camera: what is under it is a view model (set by RemixFrameCapture each frame).</summary>
        public static Transform ViewModelRoot;
        /// <summary>GameProfile.IsPlayerBody, cached (set by RemixFrameCapture): the frame capture draws the body, reflection-only.</summary>
        public static Func<Renderer, bool> IsPlayerBody;

        // Per frame: also the layers no game camera draws and the user's disabled layers,
        // which can change while the instance stays scanned.
        public bool IsLayerExcluded(int layer)
        {
            if (isLayerDisabled != null && isLayerDisabled(layer))
                return true;
            // View-model layers (RemixFrameCapture.IsViewModel): the frame capture draws what is
            // on them from the view-model camera; a scanned copy would be in the world.
            if ((ViewModelLayers & (1 << layer)) != 0)
                return true;
            return IsLayerNeverRendered(layer);
        }

        // Scan time: layers never worth reading (a skipped filter is not rescanned).
        private static bool IsLayerNeverRendered(int layer)
        {
            // Unity's built-in UI layer, and the game's own (GameProfile.IsNeverRenderedLayer).
            // Layers no game camera draws are left out per frame (IsLayerExcluded).
            return layer == 5 || Games.GameProfiles.IsNeverRenderedLayer(layer);
        }

        public static bool IsIntermediateAncestorDisabled(Transform t)
        {
            if (t == null) return false;
            Transform curr = t.parent;
            while (curr != null && curr.parent != null)
            {
                if (!curr.gameObject.activeSelf)
                    return true;
                curr = curr.parent;
            }
            return false;
        }

        // Vertex colors of the first scanned mesh of each shader, logged once: shaders that use
        // them as blend masks rather than as color show here (Remix multiplies them into albedo).
        private readonly HashSet<string> vertexColorShaders = new HashSet<string>();

        private void DescribeVertexColorsOnce(Material mat, Mesh mesh, Color32[] colors)
        {
            string shaderName = mat.shader != null ? mat.shader.name : "(none)";
            if (!vertexColorShaders.Add(shaderName))
                return;
            if (colors == null || colors.Length == 0)
            {
                logger.LogInfo($"[VertexColors] shader '{shaderName}' mesh '{mesh.name}': none ({(mesh.isReadable ? "readable" : "GPU readback")}, mesh has colors: {UnityCompat.MeshHasAttribute(mesh, UnityCompat.AttributeColor)?.ToString() ?? "unknown"})");
                return;
            }
            long r = 0, g = 0, b = 0, a = 0;
            byte minR = 255, minG = 255, minB = 255, minA = 255, maxR = 0, maxG = 0, maxB = 0, maxA = 0;
            foreach (var c in colors)
            {
                r += c.r; g += c.g; b += c.b; a += c.a;
                if (c.r < minR) minR = c.r; if (c.r > maxR) maxR = c.r;
                if (c.g < minG) minG = c.g; if (c.g > maxG) maxG = c.g;
                if (c.b < minB) minB = c.b; if (c.b > maxB) maxB = c.b;
                if (c.a < minA) minA = c.a; if (c.a > maxA) maxA = c.a;
            }
            int n = colors.Length;
            logger.LogInfo($"[VertexColors] shader '{shaderName}' mesh '{mesh.name}' material '{mat.name}': {n} verts, mean ({r / n},{g / n},{b / n},{a / n}), " +
                           $"range R {minR}-{maxR} G {minG}-{maxG} B {minB}-{maxB} A {minA}-{maxA}");
        }

        private static Color32 WeightVertexColor(Color32 c, float weight) => RemixMeshConverter.WeightVertexColor(c, weight);

        /// <summary>True if the renderer has materials and all of them are invisible.</summary>
        // Enabled, but drawn by Unity only into shadow maps or not at all: checked per frame
        // (both can change at runtime), next to renderer.enabled.
        public static bool IsRenderingOff(Renderer renderer) =>
            UnityCompat.IsForceRenderingOff(renderer) || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

        public static bool IsRendererInvisible(Renderer renderer)
        {
            if (Games.GameProfiles.IsHiddenRenderer(renderer))
                return true;
            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return false;
            foreach (var mat in materials)
            {
                if (mat == null || !IsMaterialOrShaderInvisible(mat))
                    return false;
            }
            return true;
        }

        public static bool IsMaterialOrShaderInvisible(Material mat)
        {
            if (mat == null) return false;
            if (Games.GameProfiles.IsHiddenMaterial(mat)) return true;

            string matName = mat.name;
            if (!string.IsNullOrEmpty(matName))
            {
                if (matName.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    matName.IndexOf("NoDraw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    matName.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    matName.IndexOf("CollisionOnly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    matName.IndexOf("ZeroStencilBuffer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    matName.IndexOf("PortalOcclusion", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            var shader = mat.shader;
            if (shader != null && !string.IsNullOrEmpty(shader.name))
            {
                string sName = shader.name;
                // Fog effects (screen fog quads, fog spheres and walls) shade from scene depth, which a
                // Remix surface cannot; drawn as surfaces they become white walls hiding the sky.
                if (sName.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sName.IndexOf("Clear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sName.IndexOf("Fog", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Rescan state: async-loaded objects appear after OnSceneLoaded
        private Scene activeScene;
        private float timeSinceSceneLoad;
        private float rescanTimer;
        private const float RescanInterval = 1.0f;
        private const float RescanDuration = 30.0f;
        private int visLogTimer;

        // Streaming: meshes created per render frame until StreamBudgetMs is spent (at least
        // MinMeshesPerFrame, at most MaxMeshesPerFrame).
        private const int MinMeshesPerFrame = 8, MaxMeshesPerFrame = 1024;
        private const double StreamBudgetMs = 4.0;
        // This load's streaming totals, for the "Streaming complete" line (render thread).
        private double streamMs, streamTextureMs;
        private int streamFrames, streamMeshes;

        public bool HasData
        {
            get { lock (instanceLock) { return currentInstances.Count > 0; } }
        }

        public bool IsStreaming => streamingActive;

        public int TotalInstanceCount
        {
            get { lock (instanceLock) { return currentInstances.Count; } }
        }

        public int StreamingQueueCount
        {
            get { lock (streamLock) { return streamingQueue.Count; } }
        }

        public DedupeEntry[] GetDedupeEntries()
        {
            lock (instanceLock)
            {
                if (currentInstances.Count == 0)
                    return Array.Empty<DedupeEntry>();

                var entries = new DedupeEntry[currentInstances.Count];
                for (int i = 0; i < currentInstances.Count; i++)
                {
                    entries[i] = new DedupeEntry
                    {
                        DedupeKey = currentInstances[i].DedupeKey,
                        RendererInstanceId = currentInstances[i].RendererInstanceId,
                        Layer = currentInstances[i].Layer
                    };
                }
                return entries;
            }
        }

        /// <summary>
        /// Collect per-instance debug entries for 3D box overlay. Called on main thread only when HUD is visible.
        /// </summary>
        public List<RemixFrameCapture.DebugMeshEntry> CollectDebugEntries()
        {
            lock (instanceLock)
            {
                var entries = new List<RemixFrameCapture.DebugMeshEntry>(currentInstances.Count);
                for (int i = 0; i < currentInstances.Count; i++)
                {
                    if (i >= instanceRenderers.Count) break;
                    var refObj = instanceRenderers[i];
                    var r = refObj.Renderer;
                    var go = refObj.GameObject;
                    if (r == null || go == null) continue;
                    if (!r.enabled || !go.activeInHierarchy) continue;

                    var mf = r.GetComponent<MeshFilter>();
                    entries.Add(new RemixFrameCapture.DebugMeshEntry
                    {
                        Name = go.name,
                        MeshName = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "",
                        MeshId = mf != null && mf.sharedMesh != null ? mf.sharedMesh.GetInstanceID() : 0,
                        LayerIndex = go.layer,
                        RendererInstanceId = r.GetInstanceID(),
                        LayerName = LayerMask.LayerToName(go.layer),
                        Origin = "Scanner",
                        BoundsCenter = r.bounds.center,
                        BoundsExtents = r.bounds.extents,
                        MaterialName = r.sharedMaterial != null ? r.sharedMaterial.name : "(null)",
                        NoTexture = r.sharedMaterial == null,
                    });
                }
                return entries;
            }
        }

        /// <summary>
        /// Returns the set of layer indices that have scanned instances.
        /// </summary>
        public Dictionary<int, int> GetLayerCounts()
        {
            var counts = new Dictionary<int, int>();
            lock (instanceLock)
            {
                foreach (var inst in currentInstances)
                {
                    if (!counts.ContainsKey(inst.Layer))
                        counts[inst.Layer] = 0;
                    counts[inst.Layer]++;
                }
            }
            return counts;
        }

        public SceneMeshScanner(
            ManualLogSource logger,
            RemixMeshConverter meshConverter,
            RemixMaterialManager materialManager,
            object apiLock,
            Func<int, bool> isLayerDisabled = null,
            bool scanActiveOnly = true)
        {
            this.logger = logger;
            this.meshConverter = meshConverter;
            this.materialManager = materialManager;
            this.apiLock = apiLock;
            this.isLayerDisabled = isLayerDisabled;
            this.scanActiveOnly = scanActiveOnly;
            NativeMeshReader.SetLogger(logger);
        }

        /// <summary>
        /// Must be called on the main thread. Performs the initial scan and starts
        /// periodic rescanning to catch async-loaded objects.
        /// </summary>
        public void OnSceneLoaded(Scene scene)
        {
            if (!scene.IsValid())
                return;

            scannedFilterIds.Clear();
            activeScene = scene;
            timeSinceSceneLoad = 0f;
            rescanTimer = 0f;

            StartScan(scene, initial: true);
        }

        // A scan in progress (ScanSteps), a slice of ScanSliceMs each frame: a level's thousands
        // of meshes and materials are read over several frames rather than in one long one.
        // Off for now (the whole scan in one frame): once, a sliced scan of 0-1 drew scrambled
        // geometry (meshes the game rebuilds while loading caught half-built?), not reproduced
        // since. 8.0 slices it.
        private const double ScanSliceMs = 1e9;
        private IEnumerator<int> scanJob;
        private bool scanInitial;
        private int lastScanQueued;
        private volatile bool scanInProgress;

        private void StartScan(Scene scene, bool initial)
        {
            scanJob = ScanSteps(scene, logDiagnostics: initial);
            scanInitial = initial;
            scanInProgress = true;
            StepScan();
        }

        // One slice of the scan in progress; at its end, what it queued.
        private void StepScan()
        {
            bool more;
            try { more = scanJob.MoveNext(); }
            catch (Exception ex)
            {
                logger.LogWarning($"Scene scan failed: {ex.Message}");
                more = false;
                lastScanQueued = 0;
            }
            if (more)
            {
                if (scanJob.Current > 0)
                    streamingActive = true; // stream what is queued while the scan goes on
                return;
            }
            scanJob = null;
            scanInProgress = false;
            int queued = lastScanQueued;
            if (queued > 0)
                streamingActive = true;
            if (scanInitial)
                logger.LogInfo(queued > 0
                    ? $"Scene scan queued {queued} meshes for streaming (rescanning for {RescanDuration}s to catch async-loaded objects)"
                    : $"Scene scan found no meshes yet (will rescan for {RescanDuration}s)");
            else if (queued > 0)
                logger.LogInfo($"Rescan found {queued} new meshes ({timeSinceSceneLoad:F0}s after scene load)");
        }

        /// <summary>
        /// Must be called on the main thread each frame (e.g. from plugin Update).
        /// Periodically rescans to pick up objects that loaded asynchronously.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (scanJob != null)
            {
                StepScan();
                return;
            }
            if (!activeScene.IsValid())
                return;

            timeSinceSceneLoad += deltaTime;
            if (timeSinceSceneLoad > RescanDuration)
            {
                activeScene = default;
                return;
            }

            rescanTimer += deltaTime;
            if (rescanTimer < RescanInterval)
                return;
            rescanTimer = 0f;

            StartScan(activeScene, initial: false);
        }

        /// <summary>
        /// Called on the render thread each frame. Drains a batch from the queue
        /// and returns the visibility-filtered snapshot built by UpdateVisibility().
        /// </summary>
        public InstanceData[] GetInstances(out int count)
        {
            DrainStreamingBatch();

            var snapshot = Volatile.Read(ref visibleSnapshot);
            if (snapshot != null && snapshot.Count > 0)
            {
                count = snapshot.Count;
                return snapshot.Buffer;
            }

            count = 0;
            return Array.Empty<InstanceData>();
        }

        /// <summary>
        /// Must be called on the main thread each frame. Filters scanned instances by
        /// active state, scale-zero, distance culling, and visibility culling,
        /// and dynamically updates transforms for moving non-combined objects.
        /// </summary>
        // The game's view portal exit region this frame (main thread; GameProfile.GetRayPortalView):
        // geometry seen through the portal, wherever the player is. Null without one.
        public static Games.RayPortalView? PortalExitRegion;

        public void UpdateVisibility(Vector3 cameraPosition, bool useDistanceCulling, float maxRenderDistance, bool useVisibilityCulling)
        {
            var portalExit = PortalExitRegion;
            lock (instanceLock)
            {
                if (currentInstances.Count == 0)
                {
                    Volatile.Write(ref visibleSnapshot, null);
                    return;
                }

                reusableVisibleInstances.Clear();
                if (reusableVisibleInstances.Capacity < currentInstances.Count)
                    reusableVisibleInstances.Capacity = currentInstances.Count;

                float maxDistSqr = maxRenderDistance * maxRenderDistance;
                int frameCount = Time.frameCount;

                int culledNull = 0, culledDisabled = 0, culledInactive = 0, culledLayer = 0, culledScale = 0, culledVis = 0, culledDist = 0;

                for (int i = 0; i < currentInstances.Count; i++)
                {
                    var instance = currentInstances[i];

                    // 0. Excluded layer (disabled in config, or standard non-rendered layer like Invisible, PlayerOnly, etc.)
                    if (IsLayerExcluded(instance.Layer))
                    {
                        culledLayer++;
                        continue;
                    }

                    if (i < instanceRenderers.Count)
                    {
                        var refObj = instanceRenderers[i];
                        var renderer = refObj.Renderer;

                        // 1. Destroyed in Unity (e.g. shattered glass, broken crates/props)
                        if (renderer == null)
                        {
                            culledNull++;
                            continue;
                        }

                        // 2. Disabled renderer component (e.g. invisible brushes, disabled lights/props)
                        if (!renderer.enabled || IsRenderingOff(renderer))
                        {
                            culledDisabled++;
                            continue;
                        }

                        var go = refObj.GameObject;
                        bool activeInHierarchy = go.activeInHierarchy;

                        // 3. Fast-path active-only check (prevents expensive parent hierarchy walking)
                        if (scanActiveOnly && !activeInHierarchy)
                        {
                            culledInactive++;
                            continue;
                        }

                        // 4. Objects the game toggles by deactivation (its profile names them)
                        if ((instance.Flags & InstanceFlags.FollowsActiveState) != 0 && !activeInHierarchy)
                        {
                            culledInactive++;
                            continue;
                        }

                        // 5. Hierarchical active state check (only when scanActiveOnly is false), for
                        // objects once seen active.
                        // The walk is cached: done when the object goes inactive, then staggered.
                        if (!activeInHierarchy && (instance.Flags & InstanceFlags.SeenActive) == 0)
                        {
                            culledInactive++;
                            continue;
                        }
                        if (activeInHierarchy && (instance.Flags & InstanceFlags.SeenActive) == 0)
                        {
                            instance.Flags |= InstanceFlags.SeenActive;
                            currentInstances[i] = instance;
                        }
                        if (!activeInHierarchy)
                        {
                            if ((instance.Flags & InstanceFlags.WasInactive) == 0 || ((i + frameCount) & 15) == 0)
                            {
                                var flags = (instance.Flags | InstanceFlags.WasInactive) & ~InstanceFlags.AncestorDisabled;
                                if (!go.activeSelf || IsIntermediateAncestorDisabled(refObj.Transform))
                                    flags |= InstanceFlags.AncestorDisabled;
                                if (flags != instance.Flags)
                                {
                                    instance.Flags = flags;
                                    currentInstances[i] = instance;
                                }
                            }
                            if ((instance.Flags & InstanceFlags.AncestorDisabled) != 0)
                            {
                                culledInactive++;
                                continue;
                            }
                        }
                        else if ((instance.Flags & InstanceFlags.WasInactive) != 0)
                        {
                            instance.Flags &= ~(InstanceFlags.WasInactive | InstanceFlags.AncestorDisabled);
                            currentInstances[i] = instance;
                        }

                        // 5b. A moving object now held by the camera or on a view-model layer (an
                        // item picked up, a weapon's part): the frame capture draws it as a view
                        // model; this scanned copy would follow it in the world, a second one.
                        if ((instance.Flags & InstanceFlags.HasMoved) != 0 &&
                            ((ViewModelLayers & (1 << go.layer)) != 0 ||
                             (ViewModelRoot != null && refObj.Transform.IsChildOf(ViewModelRoot)) ||
                             (IsPlayerBody != null && IsPlayerBody(renderer))))
                        {
                            culledLayer++;
                            continue;
                        }

                        // 6. Visibility culling (only if enabled in config)
                        if (useVisibilityCulling && !renderer.isVisible)
                        {
                            culledVis++;
                            continue;
                        }

                        // 7. Scale-zero check & dynamic transform update
                        if ((instance.Flags & InstanceFlags.IsCombinedMesh) == 0)
                        {
                            // Moving objects check every frame; unmoved static props stagger localToWorldMatrix checks across 4 frames
                            if ((instance.Flags & InstanceFlags.HasMoved) != 0 || ((i + frameCount) & 3) == 0)
                            {
                                var m = refObj.Transform.localToWorldMatrix;
                                float scaleSqr = (m.m00 * m.m00 + m.m10 * m.m10 + m.m20 * m.m20) +
                                                 (m.m01 * m.m01 + m.m11 * m.m11 + m.m21 * m.m21) +
                                                 (m.m02 * m.m02 + m.m12 * m.m12 + m.m22 * m.m22);
                                if (scaleSqr < 0.0001f)
                                {
                                    if ((instance.Flags & InstanceFlags.IsScaleZero) == 0)
                                    {
                                        instance.Flags |= InstanceFlags.IsScaleZero;
                                        currentInstances[i] = instance;
                                    }
                                    culledScale++;
                                    continue;
                                }

                                bool changed = false;
                                if ((instance.Flags & InstanceFlags.IsScaleZero) != 0)
                                {
                                    instance.Flags &= ~InstanceFlags.IsScaleZero;
                                    changed = true;
                                }

                                if (!MatrixEquals(in m, in instance.LastLocalToWorld))
                                {
                                    instance.Flags |= InstanceFlags.HasMoved;
                                    instance.LastLocalToWorld = m;
                                    instance.Transform = RemixAPI.remixapi_Transform.FromMatrix(
                                        m.m00, m.m02, m.m01, m.m03,
                                        m.m20, m.m22, m.m21, m.m23,
                                        m.m10, m.m12, m.m11, m.m13
                                    );
                                    instance.BoundsCenter = m.MultiplyPoint3x4(instance.LocalBoundsCenter);
                                    changed = true;
                                }

                                if (changed)
                                    currentInstances[i] = instance;
                            }
                            else if ((instance.Flags & InstanceFlags.IsScaleZero) != 0)
                            {
                                culledScale++;
                                continue;
                            }
                        }
                        else
                        {
                            // Stagger lossyScale check on combined meshes across 4 frames (vertices are static world-space)
                            if (((i + frameCount) & 3) == 0)
                            {
                                var scale = refObj.Transform.lossyScale;
                                bool isZero = scale.sqrMagnitude < 0.0001f;
                                bool wasZero = (instance.Flags & InstanceFlags.IsScaleZero) != 0;
                                if (isZero != wasZero)
                                {
                                    if (isZero) instance.Flags |= InstanceFlags.IsScaleZero;
                                    else instance.Flags &= ~InstanceFlags.IsScaleZero;
                                    currentInstances[i] = instance;
                                }
                            }

                            if ((instance.Flags & InstanceFlags.IsScaleZero) != 0)
                            {
                                culledScale++;
                                continue;
                            }
                        }
                    }

                    // Distance culling using up-to-date bounds center (not what a view portal shows)
                    if (useDistanceCulling && !(portalExit.HasValue && portalExit.Value.InExitRegion(instance.BoundsCenter)))
                    {
                        float sqrDist = (instance.BoundsCenter - cameraPosition).sqrMagnitude;
                        if (sqrDist > maxDistSqr)
                        {
                            culledDist++;
                            continue;
                        }
                    }

                    reusableVisibleInstances.Add(instance);
                }

                visLogTimer++;
                if (visLogTimer % 180 == 1)
                {
                    if (VerboseLog.On) logger.LogInfo($"[VisDiag] total={currentInstances.Count} visible={reusableVisibleInstances.Count} null={culledNull} disabled={culledDisabled} inactive={culledInactive} layer={culledLayer} scale={culledScale} vis={culledVis} dist={culledDist}");
                }

                // Destroyed renderers never come back: drop them instead of checking them every frame.
                if (culledNull >= 64)
                    RemoveDestroyedInstances();

                int count = reusableVisibleInstances.Count;
                if (count == 0)
                {
                    Volatile.Write(ref visibleSnapshot, null);
                }
                else
                {
                    InstanceSnapshot backSnapshot = useSnapshotA ? snapshotB : snapshotA;
                    if (backSnapshot.Buffer.Length < count)
                    {
                        int newCap = Math.Max(count, backSnapshot.Buffer.Length * 2);
                        backSnapshot.Buffer = new InstanceData[newCap];
                    }

                    reusableVisibleInstances.CopyTo(backSnapshot.Buffer, 0);

                    if (backSnapshot.Count > count && backSnapshot.Buffer.Length >= backSnapshot.Count)
                    {
                        Array.Clear(backSnapshot.Buffer, count, backSnapshot.Count - count);
                    }

                    backSnapshot.Count = count;
                    useSnapshotA = !useSnapshotA;
                    Volatile.Write(ref visibleSnapshot, backSnapshot);
                }
            }
        }

        // Caller holds instanceLock. Keeps currentInstances and instanceRenderers in step.
        private void RemoveDestroyedInstances()
        {
            int count = currentInstances.Count;
            if (instanceRenderers.Count != count)
                return;
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                if (instanceRenderers[i].Renderer == null)
                    continue;
                if (kept != i)
                {
                    currentInstances[kept] = currentInstances[i];
                    instanceRenderers[kept] = instanceRenderers[i];
                }
                kept++;
            }
            currentInstances.RemoveRange(kept, count - kept);
            instanceRenderers.RemoveRange(kept, count - kept);
        }

        private static bool MatrixEquals(in Matrix4x4 a, in Matrix4x4 b)
        {
            return a.m03 == b.m03 && a.m13 == b.m13 && a.m23 == b.m23 &&
                   a.m00 == b.m00 && a.m11 == b.m11 && a.m22 == b.m22 &&
                   a.m01 == b.m01 && a.m02 == b.m02 && a.m10 == b.m10 &&
                   a.m12 == b.m12 && a.m20 == b.m20 && a.m21 == b.m21;
        }

        private static Vector3 ComputeLocalBoundsCenter(Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
                return Vector3.zero;
            Vector3 min = vertices[0], max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            return (min + max) * 0.5f;
        }

        public void ClearData()
        {
            Volatile.Write(ref visibleSnapshot, null);
            lock (instanceLock)
            {
                currentInstances.Clear();
                instanceRenderers.Clear();
                reusableVisibleInstances.Clear();
                if (snapshotA.Count > 0)
                {
                    Array.Clear(snapshotA.Buffer, 0, Math.Min(snapshotA.Count, snapshotA.Buffer.Length));
                    snapshotA.Count = 0;
                }
                if (snapshotB.Count > 0)
                {
                    Array.Clear(snapshotB.Buffer, 0, Math.Min(snapshotB.Count, snapshotB.Buffer.Length));
                    snapshotB.Count = 0;
                }
            }
            lock (streamLock)
            {
                streamingQueue.Clear();
                streamingActive = false;
                clearGeneration++;
            }
            scannedFilterIds.Clear();
            scannedDescriptions.Clear();
            activeScene = default;
            scanJob = null;
            scanInProgress = false;
        }

        private static InstanceFlags DetermineInstanceFlags(MeshFilter filter, Renderer renderer, bool isCombinedMesh)
        {
            if (isCombinedMesh)
                return InstanceFlags.IsCombinedMesh;
            return Games.GameProfiles.FollowsActiveState(filter, renderer) ? InstanceFlags.FollowsActiveState : InstanceFlags.None;
        }

        private static bool IsDynamicOrIgnored(MeshFilter filter, Renderer renderer, bool isCombinedMesh)
        {
            // Skip scrolling / animated UV renderers — they are captured dynamically per-frame with live
            // UV offsets (a static batch's too: its slice of the combined mesh).
            if (RemixScrollingTextureDetector.IsScrollingRenderer(renderer))
                return true;

            if (isCombinedMesh)
                return false;

            // Objects the game moves, spawns or swaps (its GameProfile knows which)
            return Games.GameProfiles.IsDynamicObject(filter, renderer);
        }

        // The scan as slices: yields what it has queued so far each time ScanSliceMs is spent (the
        // caller resumes it next frame); lastScanQueued holds the total at its end.
        private IEnumerator<int> ScanSteps(Scene scene, bool logDiagnostics)
        {
            long scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long sliceStart = scanStart;
            double workMs = 0; // time spent scanning, not the frames between slices
            // Phase timings (ms) for the scan line: mesh reads (CPU, GPU readback), triangles,
            // material capture, combined-mesh compaction, everything else per renderer.
            double readMs = 0, gpuMs = 0, trianglesMs = 0, materialMs = 0, compactMs = 0;
            long phase;
            var filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            double findMs = RemixProfiler.ElapsedMs(scanStart);
            var combinedDataCache = new Dictionary<int, (Vector3[] verts, Vector3[] norms, Vector2[] uvs, Color32[] cols, int[][] subIndices)>();
            // Every other mesh, read once per scan and shared by all its renderers (a level places
            // the same rock or plant thousands of times; nothing downstream writes to these arrays).
            var sharedMeshData = new Dictionary<int, (Vector3[] verts, Vector3[] norms, Vector2[] uvs, Color32[] cols, int[][] subIndices)>();
            int sharedMeshReuses = 0;
            // Shared meshes whose indices were validated this scan (their arrays are shared), and
            // the hash inputs of meshes and materials (GenerateSharedMeshHash), computed once.
            var validatedMeshes = new HashSet<int>();
            var meshHashInputs = new Dictionary<int, ulong[]>();
            var materialHashInputs = new Dictionary<int, ulong[]>();
int queued = 0;
            int skippedAlreadyScanned = 0, skippedWrongScene = 0, skippedNoRenderer = 0;
            int skippedInactive = 0, skippedDynamic = 0, skippedLayer = 0;
            int skippedNoMesh = 0, skippedNoVerts = 0, skippedNoTris = 0, skippedReadError = 0;
            int gpuReadbackCount = 0;
            int vertexColorCount = 0;
            var lowerLodRenderers = LodFilter.CollectLowerLevelRenderers(out int lodGroups);
            int skippedLowerLod = 0;

            foreach (var filter in filters)
            {
                if (RemixProfiler.ElapsedMs(sliceStart) >= ScanSliceMs)
                {
                    workMs += RemixProfiler.ElapsedMs(sliceStart);
                    yield return queued;
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        lastScanQueued = queued;
                        yield break;
                    }
                    sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                if (filter == null)
                    continue;

                int filterId = filter.GetInstanceID();
                if (scannedFilterIds.Contains(filterId))
                {
                    skippedAlreadyScanned++;
                    continue;
                }

                if (!filter.gameObject.scene.IsValid() || !filter.gameObject.scene.isLoaded)
                {
                    skippedWrongScene++;
                    continue;
                }

                if (filter.gameObject.scene != scene && filter.gameObject.scene.name != "DontDestroyOnLoad")
                {
                    skippedWrongScene++;
                    continue;
                }

                // Skip non-rendered layers (Invisible collision brushes, PlayerOnly, GroundCheck, Portal, UI, etc.)
                if (IsLayerNeverRendered(filter.gameObject.layer))
                {
                    skippedLayer++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer != null && Games.GameProfiles.IsHiddenRenderer(renderer))
                {
                    scannedFilterIds.Add(filterId);
                    continue;
                }
                if (renderer == null)
                {
                    skippedNoRenderer++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // A lower-detail level of an LOD group: the detailed level is drawn instead.
                if (lowerLodRenderers.Contains(renderer.GetInstanceID()))
                {
                    skippedLowerLod++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // Skip inactive renderers to avoid scanning ghost geometry from alternate scene variants.
                // Don't add to scannedFilterIds — rescans will retry when the object becomes active.
                if (scanActiveOnly && (!renderer.enabled || !renderer.gameObject.activeInHierarchy))
                {
                    skippedInactive++;
                    continue;
                }

                var mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    skippedNoMesh++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // Unity's static batching merges renderers into a single "Combined Mesh" with
                // pre-transformed world-space vertices. Each renderer owns a slice of submeshes
                // at [subMeshStartIndex .. subMeshStartIndex + sharedMaterials.Length).
                bool isCombinedMesh = mesh.name != null && mesh.name.StartsWith("Combined Mesh");

                // Skip dynamic entities (enemies, spawn effects, seasonal hats, player, projectiles)
                // SceneMeshScanner is strictly for static level geometry; dynamic objects are captured per-frame.
                if (IsDynamicOrIgnored(filter, renderer, isCombinedMesh))
                {
                    skippedDynamic++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // Mark scanned before extraction — even if geometry is empty we won't retry
                scannedFilterIds.Add(filterId);
                PrimitiveDiagnostics.Note(renderer, mesh, "Scanner");

                Vector3[] vertices = null;
                Vector3[] normals = null;
                Vector2[] uvs = null;
                Color32[] colors = null;
                int[][] subMeshIndices = null;

                // For combined meshes, reuse cached vertex/index data across renderers
                if (isCombinedMesh)
                {
                    int meshId = mesh.GetInstanceID();
                    if (combinedDataCache.TryGetValue(meshId, out var cached))
                    {
                        vertices = cached.verts;
                        normals = cached.norms;
                        uvs = cached.uvs;
                        colors = cached.cols;
                        subMeshIndices = cached.subIndices;
                    }
                }
                else if (sharedMeshData.TryGetValue(mesh.GetInstanceID(), out var shared))
                {
                    vertices = shared.verts;
                    normals = shared.norms;
                    uvs = shared.uvs;
                    colors = shared.cols;
                    subMeshIndices = shared.subIndices;
                    sharedMeshReuses++;
                }
                bool readThisMesh = vertices == null;

                if (vertices == null)
                {
                    phase = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (mesh.isReadable)
                    {
                        try
                        {
                            vertices = mesh.vertices;
                        }
                        catch { }
                    }

                    if (vertices != null && vertices.Length > 0)
                    {
                        // Fast path: CPU mesh data available
                        normals = mesh.normals;
                        uvs = mesh.uv;
                        colors = mesh.colors32;
                        if (colors != null && colors.Length == 0) colors = null;
                    }
                    else if (mesh.vertexCount > 0)
                    {
                        // GPU readback path: vertex data is GPU-only
                        readMs += RemixProfiler.ElapsedMs(phase);
                        phase = System.Diagnostics.Stopwatch.GetTimestamp();
                        try
                        {
                            if (ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out subMeshIndices))
                            {
                                gpuReadbackCount++;
                                if (LayeredMaterialCompositor.UsesVertexColorAsColor(renderer.sharedMaterials))
                                    colors = SkinnedMeshGpuReader.TryReadColors(mesh);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"GPU readback failed for mesh '{mesh.name}': {ex.Message}");
                            skippedReadError++;
                            continue;
                        }
                    }

                    if (isCombinedMesh && vertices != null && vertices.Length > 0)
                        combinedDataCache[mesh.GetInstanceID()] = (vertices, normals, uvs, colors, subMeshIndices);
                    if (gpuReadbackCount > 0 && !mesh.isReadable)
                        gpuMs += RemixProfiler.ElapsedMs(phase);
                    else
                        readMs += RemixProfiler.ElapsedMs(phase);
                }

                if (vertices == null || vertices.Length == 0)
                {
                    skippedNoVerts++;
                    continue;
                }

                // Build per-submesh surfaces with separate materials
                var materials = renderer.sharedMaterials;
                var surfaces = new List<SubMeshSurface>();
                var surfaceMaterials = new List<Material>();

                // For combined meshes, each renderer only owns a slice of submeshes
                int subStart = 0;
                int subEnd = mesh.subMeshCount;
                if (isCombinedMesh)
                {
                    subStart = renderer.subMeshStartIndex;
                    subEnd = Math.Min(subStart + (materials != null ? materials.Length : 0), mesh.subMeshCount);
                    if (subStart >= subEnd)
                        continue;
                }

                phase = System.Diagnostics.Stopwatch.GetTimestamp();
                if (subMeshIndices == null)
                {
                    // CPU path — extract per-submesh indices for this renderer's range
                    var subList = new List<int[]>();
                    for (int sub = subStart; sub < subEnd; sub++)
                    {
                        if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                        {
                            subList.Add(null);
                            continue;
                        }
                        var tris = mesh.GetTriangles(sub);
                        subList.Add(tris != null && tris.Length > 0 ? tris : null);
                    }
                    subMeshIndices = subList.ToArray();
                }
                else if (isCombinedMesh)
                {
                    // GPU readback / cache returned all submeshes — extract only this renderer's range
                    int rangeCount = Math.Min(subEnd, subMeshIndices.Length) - subStart;
                    if (rangeCount > 0)
                    {
                        var subset = new int[rangeCount][];
                        Array.Copy(subMeshIndices, subStart, subset, 0, rangeCount);
                        subMeshIndices = subset;
                    }
                }

                if (readThisMesh && !isCombinedMesh)
                    sharedMeshData[mesh.GetInstanceID()] = (vertices, normals, uvs, colors, subMeshIndices);
                trianglesMs += RemixProfiler.ElapsedMs(phase);
                phase = System.Diagnostics.Stopwatch.GetTimestamp();

                for (int sub = 0; sub < subMeshIndices.Length; sub++)
                {
                    var tris = subMeshIndices[sub];
                    if (tris == null || tris.Length == 0)
                        continue;

                    int matId = 0;
                    if (materials != null && sub < materials.Length && materials[sub] != null)
                    {
                        var mat = materials[sub];
                        if (IsMaterialOrShaderInvisible(mat))
                            continue;

                        matId = StaticGeometryDedupe.GetCanonicalMaterialId(mat);
                        materialManager.CaptureMaterialTextures(mat, matId);
                        DescribeVertexColorsOnce(mat, mesh, colors);
                    }
                    surfaces.Add(new SubMeshSurface { Indices = tris, MaterialId = matId });
                    surfaceMaterials.Add(materials != null && sub < materials.Length ? materials[sub] : null);
                }

                materialMs += RemixProfiler.ElapsedMs(phase);
                phase = System.Diagnostics.Stopwatch.GetTimestamp();
                // For combined meshes, compact vertex arrays so each Remix mesh only
                // includes the vertices referenced by this renderer's submeshes
                if (isCombinedMesh && surfaces.Count > 0)
                    CompactMeshData(ref vertices, ref normals, ref uvs, ref colors, surfaces);
                compactMs += RemixProfiler.ElapsedMs(phase);

                if (surfaces.Count == 0)
                {
                    skippedNoTris++;
                    continue;
                }

                // Validate indices across all surfaces (once per shared mesh: same arrays)
                bool valid = true;
                bool validated = !isCombinedMesh && !readThisMesh && validatedMeshes.Contains(mesh.GetInstanceID());
                if (!validated)
                foreach (var surf in surfaces)
                {
                    if (surf.Indices.Length % 3 != 0) { valid = false; break; }
                    for (int i = 0; i < surf.Indices.Length; i++)
                    {
                        if (surf.Indices[i] < 0 || surf.Indices[i] >= vertices.Length)
                        { valid = false; break; }
                    }
                    if (!valid) break;
                }
                if (!valid)
                    continue;
                if (!isCombinedMesh)
                    validatedMeshes.Add(mesh.GetInstanceID());

                // Panoramic skies take their UVs from their vertex directions, as Unity samples them
                // (RemixMeshConverter.MapPanoramicSkies), before the mesh is hashed.
                bool cameraOnly = RemixMeshConverter.IsCameraOnly(surfaceMaterials);
                if (surfaceMaterials.Exists(RemixMaterialManager.IsPanoramicSky))
                {
                    var indices = new List<uint[]>(surfaces.Count);
                    foreach (var surf in surfaces)
                        indices.Add(Array.ConvertAll(surf.Indices, i => (uint)i));
                    RemixMeshConverter.MapPanoramicSkies(ref vertices, ref normals, ref uvs, ref colors, indices, surfaceMaterials);
                    for (int s = 0; s < surfaces.Count; s++)
                        surfaces[s] = new SubMeshSurface { Indices = Array.ConvertAll(indices[s], i => (int)i), MaterialId = surfaces[s].MaterialId };
                }

                // Combined static-batch vertices are already in world space.
                Vector3 objectUp = isCombinedMesh ? Vector3.up : filter.transform.InverseTransformDirection(Vector3.up);
                bool projected = !isCombinedMesh && AnyProjection(surfaces);
                Vector3 objectScale = projected ? QuantizeScale(filter.transform.lossyScale) : Vector3.one;
                // Objects with the same mesh and materials share one Remix mesh (a level places the
                // same rock thousands of times); each keeps its own instance. Combined static-batch
                // meshes hold per-renderer world-space slices: one mesh per object.
                ulong meshHash = isCombinedMesh
                    ? GenerateInstanceMeshHash(mesh, filter.transform, renderer)
                    : GenerateSharedMeshHash(mesh, vertices, surfaces, surfaceMaterials, objectUp, AnyTopVariant(surfaces), objectScale,
                                             meshHashInputs, materialHashInputs);
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);

                if (colors != null && colors.Length > 0)
                    vertexColorCount++;

                var surfaceIds = new int[surfaces.Count];
                for (int s = 0; s < surfaces.Count; s++)
                    surfaceIds[s] = surfaces[s].MaterialId;
                scannedDescriptions[renderer.GetInstanceID()] = new ScanRecord
                {
                    meshName = mesh.name,
                    vertexCount = vertices.Length,
                    colors = colors != null && colors.Length > 0,
                    materials = surfaceMaterials.ToArray(),
                    materialIds = surfaceIds,
                };

                lock (streamLock)
                {
                    streamingQueue.Enqueue(new ScannedMeshData
                    {
                        MeshHash = meshHash,
                        DedupeKey = dedupeKey,
                        RendererInstanceId = renderer.GetInstanceID(),
                        Vertices = vertices,
                        Normals = normals,
                        UVs = uvs,
                        Colors = colors,
                        Surfaces = surfaces.ToArray(),
                        CameraOnly = cameraOnly,
                        LocalToWorld = isCombinedMesh ? Matrix4x4.identity : filter.transform.localToWorldMatrix,
                        Layer = filter.gameObject.layer,
                        SourceRenderer = renderer,
                        SourceGameObject = filter.gameObject,
                        SourceTransform = filter.transform,
                        Flags = DetermineInstanceFlags(filter, renderer, isCombinedMesh),
                        ObjectUp = objectUp,
                        ObjectScale = objectScale,
                    });
                }
                queued++;
            }

            if (logDiagnostics || queued > 0)
            {
                double totalMs = workMs + RemixProfiler.ElapsedMs(sliceStart);
                logger.LogInfo($"Scene scan '{scene.name}' took {totalMs:F0} ms over {RemixProfiler.ElapsedMs(scanStart):F0} ms (find {findMs:F0}, mesh reads {readMs:F0}, GPU readback {gpuMs:F0}, " +
                               $"triangles {trianglesMs:F0}, materials {materialMs:F0}, compaction {compactMs:F0}, " +
                               $"other {totalMs - findMs - readMs - gpuMs - trianglesMs - materialMs - compactMs:F0})");
                logger.LogInfo($"Scene scan '{scene.name}': {filters.Length} total MeshFilters, {queued} queued ({sharedMeshData.Count} distinct meshes read, {sharedMeshReuses} reused, {skippedLowerLod} lower LOD levels of {lodGroups} groups skipped; {gpuReadbackCount} via GPU readback, {vertexColorCount} with vertex colors)" +
                    $" | skipped: {skippedWrongScene} wrong scene, {skippedAlreadyScanned} already scanned," +
                    $" {skippedInactive} inactive, {skippedLayer} layer excluded, {skippedDynamic} dynamic/ignored, {skippedNoRenderer} no renderer, {skippedNoMesh} no mesh," +
                    $" {skippedReadError} read error, {skippedNoVerts} no verts, {skippedNoTris} no tris");
                materialManager.LogMaterialStats();
            }

            lastScanQueued = queued;
        }

        private void DrainStreamingBatch()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            materialManager.ProcessPendingTextureUploads();
            double textureMs = RemixProfiler.ElapsedMs(start);

            int batchGeneration;
            lock (streamLock)
            {
                batchGeneration = clearGeneration;
                if (streamingQueue.Count == 0)
                {
                    if (streamingActive && !scanInProgress)
                    {
                        streamingActive = false;
                        logger.LogInfo($"Streaming complete: {currentInstances.Count} scene mesh instances loaded; " +
                                       $"{streamMeshes} meshes in {streamMs:F0} ms over {streamFrames} frames " +
                                       $"(texture uploads {streamTextureMs:F0} ms)");
                        streamMs = streamTextureMs = 0;
                        streamFrames = streamMeshes = 0;
                    }
                    return;
                }
            }

            var newInstances = new List<InstanceData>(64);
            var newRenderers = new List<ScannedRendererRef>(64);

            // As many as fit the frame's budget: small meshes stream in many at a time.
            for (int taken = 0; taken < MaxMeshesPerFrame; taken++)
            {
                if (taken >= MinMeshesPerFrame && RemixProfiler.ElapsedMs(start) >= StreamBudgetMs)
                    break;
                ScannedMeshData entry;
                lock (streamLock)
                {
                    if (batchGeneration != clearGeneration || streamingQueue.Count == 0)
                        break;
                    entry = streamingQueue.Dequeue();
                }
                streamMeshes++;
                IntPtr meshHandle = CreateRemixMesh(entry);
                if (meshHandle == IntPtr.Zero)
                    continue;

                // Convert Unity Matrix4x4 to Remix transform (Y-up to Z-up)
                var m = entry.LocalToWorld;
                var transform = RemixAPI.remixapi_Transform.FromMatrix(
                    m.m00, m.m02, m.m01, m.m03,
                    m.m20, m.m22, m.m21, m.m23,
                    m.m10, m.m12, m.m11, m.m13
                );

                Vector3 localCenter = ComputeLocalBoundsCenter(entry.Vertices);
                newInstances.Add(new InstanceData
                {
                    MeshHandle = meshHandle,
                    DedupeKey = entry.DedupeKey,
                    RendererInstanceId = entry.RendererInstanceId,
                    Transform = transform,
                    LastLocalToWorld = m,
                    LocalBoundsCenter = localCenter,
                    Layer = entry.Layer,
                    BoundsCenter = m.MultiplyPoint3x4(localCenter),
                    Flags = entry.Flags,
                });
                newRenderers.Add(new ScannedRendererRef
                {
                    Renderer = entry.SourceRenderer,
                    GameObject = entry.SourceGameObject,
                    Transform = entry.SourceTransform,
                });
            }

            streamFrames++;
            streamMs += RemixProfiler.ElapsedMs(start);
            streamTextureMs += textureMs;
            if (newInstances.Count > 0)
            {
                lock (streamLock)
                {
                    if (batchGeneration != clearGeneration)
                        return;
                }
                lock (instanceLock)
                {
                    currentInstances.AddRange(newInstances);
                    instanceRenderers.AddRange(newRenderers);
                }
            }
        }

        private IntPtr CreateRemixMesh(ScannedMeshData data)
        {
            if (data.Vertices == null || data.Vertices.Length == 0 ||
                data.Surfaces == null || data.Surfaces.Length == 0)
                return IntPtr.Zero;

            if (meshHandles.TryGetValue(data.MeshHash, out IntPtr existing))
                return existing;

            var verts = data.Vertices;
            var norms = data.Normals;
            var uvs = data.UVs;
            var cols = data.Colors;
            bool hasColors = cols != null && cols.Length == verts.Length;
            // Vertex colors a layered material uses as masks are not color (weight 0); partly color
            // where the shader scales them (_VertexColorAmount).
            float vertexColorWeight = data.Surfaces.Length > 0 ? materialManager.GetVertexColorWeight(data.Surfaces[0].MaterialId) : 1f;
            if (vertexColorWeight == 0f)
                hasColors = false;

            if (norms == null || norms.Length != verts.Length)
            {
                norms = ComputeFaceNormals(verts, data.Surfaces.Select(s => s.Indices).ToArray());
                logger.LogDebug($"Mesh 0x{data.MeshHash:X16}: normals missing, computed from face geometry");
            }

            if (uvs == null || uvs.Length != verts.Length)
                uvs = new Vector2[verts.Length];

            // Top-colored surfaces: cut their triangles along the top contour before splitting
            // (into copies; the shared mesh's index arrays stay as read).
            var surfaceList = data.Surfaces;
            RemixMeshConverter.TopContourClipper clipper = null;
            for (int s = 0; s < surfaceList.Length; s++)
            {
                var surf = surfaceList[s];
                if (surf.Indices == null || surf.MaterialId == 0 || !materialManager.TryGetTopVariant(surf.MaterialId, out _, out float threshold))
                    continue;
                if (clipper == null)
                {
                    clipper = new RemixMeshConverter.TopContourClipper(verts, norms, uvs, cols);
                    surfaceList = (SubMeshSurface[])surfaceList.Clone();
                }
                uint[] clipped = clipper.Clip(Array.ConvertAll(surf.Indices, i => (uint)i), data.ObjectUp, threshold);
                surfaceList[s].Indices = Array.ConvertAll(clipped, i => (int)i);
            }
            if (clipper != null)
            {
                clipper.Apply(out verts, out norms, out uvs, out cols);
                hasColors = hasColors && cols != null && cols.Length == verts.Length;
            }

            // Check if any surface needs UV tiling/offset applied
            bool anyNonIdentityST = false;
            for (int s = 0; s < data.Surfaces.Length; s++)
            {
                var st = materialManager.GetMainTexST(data.Surfaces[s].MaterialId);
                if (st.x != 1f || st.y != 1f || st.z != 0f || st.w != 0f ||
                    materialManager.TryGetProjection(data.Surfaces[s].MaterialId, out _))
                { anyNonIdentityST = true; break; }
            }

            var vertexHandles = new List<GCHandle>();
            var indexHandles = new List<GCHandle>();
            var surfaceHandles = new List<GCHandle>();

            try
            {
                RemixAPI.remixapi_HardcodedVertex[] sharedRemixVerts = null;
                GCHandle sharedVertexHandle = default;

                // If no tiling needed, build shared vertex buffer once (common fast path)
                if (!anyNonIdentityST)
                {
                    sharedRemixVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(WeightVertexColor(cols[i], vertexColorWeight)) : 0xFFFFFFFF;
                        sharedRemixVerts[i] = RemixAPI.MakeVertex(
                            verts[i].x, verts[i].z, verts[i].y,
                            norms[i].x, norms[i].z, norms[i].y,
                            uvs[i].x, uvs[i].y,
                            col
                        );
                    }
                    sharedVertexHandle = GCHandle.Alloc(sharedRemixVerts, GCHandleType.Pinned);
                    vertexHandles.Add(sharedVertexHandle);
                }

                // Build one surface per submesh, each with its own material (skipping non-triangle or empty surfaces)
                var surfaces = new List<RemixAPI.remixapi_MeshInfoSurfaceTriangles>();
                bool bakeable = true;
                int blendedMaterialId = 0;
                for (int s = 0; s < data.Surfaces.Length; s++)
                {
                    var surf = surfaceList[s];
                    if (surf.Indices == null || surf.Indices.Length < 3 || (surf.Indices.Length % 3) != 0)
                        continue;

                    var validIndices = new List<uint>(surf.Indices.Length);
                    for (int i = 0; i + 2 < surf.Indices.Length; i += 3)
                    {
                        int i0 = surf.Indices[i];
                        int i1 = surf.Indices[i + 1];
                        int i2 = surf.Indices[i + 2];
                        if (i0 >= 0 && (ulong)i0 < (ulong)verts.Length &&
                            i1 >= 0 && (ulong)i1 < (ulong)verts.Length &&
                            i2 >= 0 && (ulong)i2 < (ulong)verts.Length)
                        {
                            validIndices.Add((uint)i0);
                            validIndices.Add((uint)i1);
                            validIndices.Add((uint)i2);
                        }
                    }
                    if (validIndices.Count < 3)
                        continue;

                    // Upward-facing triangles of a top-colored material get its top variant.
                    var parts = new List<(uint[] indices, int materialId)>(2);
                    if (surf.MaterialId != 0 && materialManager.TryGetTopVariant(surf.MaterialId, out int topMaterialId, out float topThreshold))
                    {
                        RemixMeshConverter.SplitByUp(validIndices, norms, data.ObjectUp, topThreshold, out var topIndices, out var otherIndices);
                        if (otherIndices.Length >= 3) parts.Add((otherIndices, surf.MaterialId));
                        if (topIndices.Length >= 3) parts.Add((topIndices, topMaterialId));
                    }
                    else
                    {
                        parts.Add((validIndices.ToArray(), surf.MaterialId));
                    }

                    foreach (var (surfIndices, partMaterialId) in parts)
                    {
                        int surfaceMaterialId = partMaterialId != 0 ? partMaterialId : (data.Surfaces.Length > 0 ? data.Surfaces[0].MaterialId : 0);
                        if (!materialManager.IsBakeableMaterial(surfaceMaterialId))
                            bakeable = false;
                        if (blendedMaterialId == 0 && materialManager.NeedsBlendedInstance(surfaceMaterialId))
                            blendedMaterialId = surfaceMaterialId;
                        GCHandle idxHandle = GCHandle.Alloc(surfIndices, GCHandleType.Pinned);
                        indexHandles.Add(idxHandle);

                        IntPtr materialHandle = IntPtr.Zero;
                        if (partMaterialId != 0)
                            materialHandle = materialManager.GetOrCreateMaterial(partMaterialId);
                        if (materialHandle == IntPtr.Zero && data.Surfaces.Length > 0 && data.Surfaces[0].MaterialId != 0)
                            materialHandle = materialManager.GetOrCreateMaterial(data.Surfaces[0].MaterialId);

                        IntPtr vertsPtr;
                        ulong vertsCount;

                        if (anyNonIdentityST)
                        {
                            // Per-surface vertex buffer with _MainTex_ST applied
                            var st = materialManager.GetMainTexST(partMaterialId);
                            bool projected = materialManager.TryGetProjection(partMaterialId, out float projection);
                            int[] source = null;
                            Vector2[] projectedUvs = null;
                            if (projected)
                                RemixMeshConverter.ProjectTriangles(surfIndices, verts, data.ObjectScale, projection, out source, out projectedUvs);

                            var surfVerts = new RemixAPI.remixapi_HardcodedVertex[projected ? source.Length : verts.Length];
                            for (int k = 0; k < surfVerts.Length; k++)
                            {
                                int i = projected ? source[k] : k;
                                Vector2 uv = projected ? projectedUvs[k] : uvs[i];
                                float u = uv.x * st.x + st.z;
                                float v = uv.y * st.y + st.w;
                                uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(WeightVertexColor(cols[i], vertexColorWeight)) : 0xFFFFFFFF;
                                surfVerts[k] = RemixAPI.MakeVertex(
                                    verts[i].x, verts[i].z, verts[i].y,
                                    norms[i].x, norms[i].z, norms[i].y,
                                    u, v,
                                    col
                                );
                            }
                            var vHandle = GCHandle.Alloc(surfVerts, GCHandleType.Pinned);
                            vertexHandles.Add(vHandle);
                            vertsPtr = vHandle.AddrOfPinnedObject();
                            vertsCount = (ulong)surfVerts.Length;
                        }
                        else
                        {
                            vertsPtr = sharedVertexHandle.AddrOfPinnedObject();
                            vertsCount = (ulong)sharedRemixVerts.Length;
                        }

                        surfaces.Add(new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                        {
                            vertices_values = vertsPtr,
                            vertices_count = vertsCount,
                            indices_values = idxHandle.AddrOfPinnedObject(),
                            indices_count = (ulong)surfIndices.Length,
                            skinning_hasvalue = 0,
                            skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                            material = materialHandle
                        });
                    }
                }

                if (surfaces.Count == 0)
                    return IntPtr.Zero;

                var surfacesArray = surfaces.ToArray();
                GCHandle surfaceArrayHandle = GCHandle.Alloc(surfacesArray, GCHandleType.Pinned);
                surfaceHandles.Add(surfaceArrayHandle);

                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = data.MeshHash,
                    surfaces_values = surfaceArrayHandle.AddrOfPinnedObject(),
                    surfaces_count = (uint)surfacesArray.Length
                };

                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    var createFunc = meshConverter.GetCreateMeshFunc();
                    if (createFunc == null)
                        return IntPtr.Zero;
                    result = createFunc(ref meshInfo, out handle);
                }

                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogError($"Failed to create scanned mesh 0x{data.MeshHash:X16}: {result}");
                    return IntPtr.Zero;
                }
                meshConverter.SetBakeable(handle, bakeable);
                // A particle-like (additive glow) surface: its instances blend.
                meshConverter.NoteBlended(handle, blendedMaterialId);
                if (data.CameraOnly)
                    meshConverter.RegisterCameraOnly(handle, $"scanned 0x{data.MeshHash:X16}");

                meshHandles[data.MeshHash] = handle;
                return handle;
            }
            finally
            {
                foreach (var h in vertexHandles) h.Free();
                foreach (var h in indexHandles) h.Free();
                foreach (var h in surfaceHandles) h.Free();
            }
        }

        // Persistent across sessions (no instance IDs): the mesh's name, size and a sample of its
        // vertices, and per surface its index count and its material's name, shader, main
        // texture and colors.
        private bool AnyProjection(List<SubMeshSurface> surfaces)
        {
            foreach (var surface in surfaces)
            {
                if (materialManager.TryGetProjection(surface.MaterialId, out _))
                    return true;
            }
            return false;
        }

        // Per axis to steps of about 10%, so copies at nearly the same scale share a mesh.
        private static Vector3 QuantizeScale(Vector3 scale)
        {
            float Step(float v)
            {
                float a = Mathf.Abs(v);
                if (a < 1e-4f) return 1f;
                return Mathf.Pow(1.1f, Mathf.Round(Mathf.Log(a, 1.1f)));
            }
            return new Vector3(Step(scale.x), Step(scale.y), Step(scale.z));
        }

        private bool AnyTopVariant(List<SubMeshSurface> surfaces)
        {
            foreach (var surface in surfaces)
            {
                if (materialManager.TryGetTopVariant(surface.MaterialId, out _, out _))
                    return true;
            }
            return false;
        }

        // orientationMatters: a top-colored material splits faces by world up, so differently
        // turned copies (up quantized) are different meshes.
        private static ulong GenerateSharedMeshHash(Mesh mesh, Vector3[] vertices, List<SubMeshSurface> surfaces, List<Material> surfaceMaterials,
                                                   Vector3 objectUp, bool orientationMatters, Vector3 objectScale,
                                                   Dictionary<int, ulong[]> meshInputs, Dictionary<int, ulong[]> materialInputs)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(ulong value) { hash = (hash ^ value) * 1099511628211UL; }
            void MixAll(ulong[] values) { for (int i = 0; i < values.Length; i++) Mix(values[i]); }

            Mix(0x5348415245444D53UL); // distinct from the per-object hashes
            if (objectScale != Vector3.one)
            {
                Mix(FloatBits(objectScale.x));
                Mix(FloatBits(objectScale.y));
                Mix(FloatBits(objectScale.z));
            }
            if (orientationMatters)
            {
                var up = objectUp.normalized;
                Mix((ulong)(long)Mathf.Round(up.x * 4f));
                Mix((ulong)(long)Mathf.Round(up.y * 4f));
                Mix((ulong)(long)Mathf.Round(up.z * 4f));
            }
            int meshId = mesh.GetInstanceID();
            if (!meshInputs.TryGetValue(meshId, out var meshValues))
            {
                var list = new List<ulong>(64);
                AddString(list, mesh.name);
                list.Add((ulong)vertices.Length);
                int step = Math.Max(1, vertices.Length / 16);
                for (int i = 0; i < vertices.Length; i += step)
                {
                    list.Add(FloatBits(vertices[i].x));
                    list.Add(FloatBits(vertices[i].y));
                    list.Add(FloatBits(vertices[i].z));
                }
                meshInputs[meshId] = meshValues = list.ToArray();
            }
            MixAll(meshValues);
            for (int s = 0; s < surfaces.Count; s++)
            {
                Mix((ulong)surfaces[s].Indices.Length);
                var mat = s < surfaceMaterials.Count ? surfaceMaterials[s] : null;
                if (mat == null) { Mix(0); continue; }
                int matId = mat.GetInstanceID();
                if (!materialInputs.TryGetValue(matId, out var matValues))
                {
                    var list = new List<ulong>(64);
                    AddString(list, mat.name.Replace(" (Instance)", "").Replace("(Clone)", "").Trim());
                    AddString(list, mat.shader != null ? mat.shader.name : null);
                    try
                    {
                        var tex = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                        AddString(list, tex != null ? tex.name : null);
                        foreach (var prop in SharedHashColorProps)
                        {
                            if (!mat.HasProperty(prop)) continue;
                            var c = mat.GetColor(prop);
                            list.Add(FloatBits(c.r)); list.Add(FloatBits(c.g)); list.Add(FloatBits(c.b)); list.Add(FloatBits(c.a));
                        }
                    }
                    catch { }
                    materialInputs[matId] = matValues = list.ToArray();
                }
                MixAll(matValues);
            }
            return hash == 0 ? 1 : hash;
        }

        private static ulong FloatBits(float value) => (ulong)BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

        // A string's hash inputs: its characters then 0xFF, or a lone 0 for none.
        private static void AddString(List<ulong> list, string text)
        {
            if (text == null) { list.Add(0); return; }
            foreach (char c in text) list.Add(c);
            list.Add(0xFF);
        }

        private static readonly string[] SharedHashColorProps = { "_Color", "_BaseColor", "_Tint" };

        private static ulong GenerateInstanceMeshHash(Mesh mesh, Transform transform, Renderer renderer)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis

            // Include transform hierarchy hash so each object gets a persistent unique Remix mesh
            hash ^= HashUtils.GetHierarchyHash(transform);
            hash *= 1099511628211UL;

            // and the renderer's own slice of the combined mesh, and where it is: two objects of
            // the same name at the same place in the hierarchy (two root "Cube"s in one static
            // batch) shared one Remix mesh, the second drawing the first's slice (ULTRAKILL 0-1's
            // missing end-room floor).
            hash ^= (ulong)(uint)(renderer is MeshRenderer meshRenderer ? meshRenderer.subMeshStartIndex : 0);
            hash *= 1099511628211UL;
            Vector3 centre = renderer.bounds.center;
            hash ^= (ulong)(uint)Mathf.RoundToInt(centre.x * 100f);
            hash *= 1099511628211UL;
            hash ^= (ulong)(uint)Mathf.RoundToInt(centre.y * 100f);
            hash *= 1099511628211UL;
            hash ^= (ulong)(uint)Mathf.RoundToInt(centre.z * 100f);
            hash *= 1099511628211UL;

            if (!string.IsNullOrEmpty(mesh.name))
            {
                foreach (char c in mesh.name)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }

            hash ^= (ulong)mesh.vertexCount;
            hash *= 1099511628211UL;

            return hash;
        }

        /// <summary>
        /// Compacts vertex/normal/UV arrays to only include vertices referenced by the
        /// given surfaces, and remaps all surface indices accordingly. Used for combined
        /// meshes where each renderer uses a small slice of a large shared vertex buffer.
        /// </summary>
        private static void CompactMeshData(
            ref Vector3[] vertices, ref Vector3[] normals, ref Vector2[] uvs,
            ref Color32[] colors, List<SubMeshSurface> surfaces)
        {
            var usedSet = new HashSet<int>();
            foreach (var surf in surfaces)
                foreach (int idx in surf.Indices)
                    usedSet.Add(idx);

            if (usedSet.Count == vertices.Length)
                return;

            var sorted = new int[usedSet.Count];
            usedSet.CopyTo(sorted);
            Array.Sort(sorted);

            var remap = new Dictionary<int, int>(sorted.Length);
            for (int i = 0; i < sorted.Length; i++)
                remap[sorted[i]] = i;

            var newVerts = new Vector3[sorted.Length];
            var newNorms = normals != null && normals.Length == vertices.Length
                ? new Vector3[sorted.Length] : null;
            var newUvs = uvs != null && uvs.Length == vertices.Length
                ? new Vector2[sorted.Length] : null;
            var newCols = colors != null && colors.Length == vertices.Length
                ? new Color32[sorted.Length] : null;

            for (int i = 0; i < sorted.Length; i++)
            {
                int old = sorted[i];
                newVerts[i] = vertices[old];
                if (newNorms != null) newNorms[i] = normals[old];
                if (newUvs != null) newUvs[i] = uvs[old];
                if (newCols != null) newCols[i] = colors[old];
            }

            foreach (var surf in surfaces)
                for (int i = 0; i < surf.Indices.Length; i++)
                    surf.Indices[i] = remap[surf.Indices[i]];

            vertices = newVerts;
            normals = newNorms;
            uvs = newUvs;
            colors = newCols;
        }

        private bool ReadMeshFromGPU(Mesh mesh, out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[][] subMeshIndices)
        {
            return NativeMeshReader.ReadMeshFromGPU(mesh, out positions, out normals, out uvs, out subMeshIndices);
        }

        private static Vector3[] ComputeFaceNormals(Vector3[] verts, int[][] subMeshIndices)
        {
            return NativeMeshReader.ComputeFaceNormals(verts, subMeshIndices);
        }
    }
}
