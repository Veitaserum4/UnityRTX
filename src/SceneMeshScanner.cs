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
            IsCheckPoint = 1 << 1,
            IsNoPass = 1 << 2,
            HasMoved = 1 << 3,
            IsScaleZero = 1 << 4,
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

        public bool IsLayerExcluded(int layer)
        {
            if (isLayerDisabled != null && isLayerDisabled(layer))
                return true;

            // Safety fallback exclusions for standard non-rendered geometry in ULTRAKILL / Unity:
            // Layer 5: UI
            // Layer 16: Invisible (triggers, blockers, collision brushes)
            // Layer 18: PlayerOnly (invisible player barriers)
            // Layer 19: Virtual Screen
            // Layer 20: GroundCheck
            // Layer 28: VirtualRender
            // Layer 30: Portal
            if (layer == 16 || layer == 18 || layer == 20 || layer == 30 || layer == 5 || layer == 19 || layer == 28)
                return true;

            return false;
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
                logger.LogInfo($"[VertexColors] shader '{shaderName}' mesh '{mesh.name}': none ({(mesh.isReadable ? "readable" : "GPU readback")}, mesh has colors: {mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)})");
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

        private static Color32 WeightVertexColor(Color32 c, float weight)
        {
            if (weight >= 1f)
                return c;
            return new Color32((byte)(255 + (c.r - 255) * weight), (byte)(255 + (c.g - 255) * weight),
                               (byte)(255 + (c.b - 255) * weight), c.a);
        }

        public static bool IsMaterialOrShaderInvisible(Material mat)
        {
            if (mat == null) return false;

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
                if (sName.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sName.IndexOf("Clear", StringComparison.OrdinalIgnoreCase) >= 0)
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

        private const int MeshesPerFrame = 32;

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

            int queued = ScanScene(scene, logDiagnostics: true);
            if (queued > 0)
            {
                streamingActive = true;
                logger.LogInfo($"Scene scan queued {queued} meshes for streaming (rescanning for {RescanDuration}s to catch async-loaded objects)");
            }
            else
            {
                logger.LogInfo($"Scene scan found no meshes yet in '{scene.name}' (will rescan for {RescanDuration}s)");
            }
        }

        /// <summary>
        /// Must be called on the main thread each frame (e.g. from plugin Update).
        /// Periodically rescans to pick up objects that loaded asynchronously.
        /// </summary>
        public void Update(float deltaTime)
        {
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

            int queued = ScanScene(activeScene, logDiagnostics: false);
            if (queued > 0)
            {
                streamingActive = true;
                logger.LogInfo($"Rescan found {queued} new meshes ({timeSinceSceneLoad:F0}s after scene load)");
            }
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
        public void UpdateVisibility(Vector3 cameraPosition, bool useDistanceCulling, float maxRenderDistance, bool useVisibilityCulling)
        {
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
                        if (!renderer.enabled)
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

                        // 4. Special toggleable objects that must follow activeInHierarchy (CheckPoint graphic, Door noPass skull lock)
                        if ((instance.Flags & (InstanceFlags.IsCheckPoint | InstanceFlags.IsNoPass)) != 0 && !activeInHierarchy)
                        {
                            culledInactive++;
                            continue;
                        }

                        // 5. Hierarchical active state check (only when scanActiveOnly is false)
                        if (!activeInHierarchy)
                        {
                            if (!go.activeSelf || IsIntermediateAncestorDisabled(refObj.Transform))
                            {
                                culledInactive++;
                                continue;
                            }
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

                    // Distance culling using up-to-date bounds center
                    if (useDistanceCulling)
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
                    logger.LogInfo($"[VisDiag] total={currentInstances.Count} visible={reusableVisibleInstances.Count} null={culledNull} disabled={culledDisabled} inactive={culledInactive} layer={culledLayer} scale={culledScale} vis={culledVis} dist={culledDist}");
                }

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
            activeScene = default;
        }

        private static Type _checkPointType;
        private static bool _checkPointTypeSearched;

        private static Type GetCheckPointType()
        {
            if (!_checkPointTypeSearched)
            {
                _checkPointTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("CheckPoint");
                    if (t != null)
                    {
                        _checkPointType = t;
                        break;
                    }
                }
            }
            return _checkPointType;
        }

        private static InstanceFlags DetermineInstanceFlags(MeshFilter filter, Renderer renderer, bool isCombinedMesh)
        {
            if (isCombinedMesh)
                return InstanceFlags.IsCombinedMesh;

            InstanceFlags flags = InstanceFlags.None;

            try
            {
                var cpType = GetCheckPointType();
                if (cpType != null && renderer.GetComponentInParent(cpType) != null)
                {
                    flags |= InstanceFlags.IsCheckPoint;
                }
                else
                {
                    string objName = filter.gameObject.name;
                    if (!string.IsNullOrEmpty(objName) && objName.IndexOf("CheckPoint", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        flags |= InstanceFlags.IsCheckPoint;
                    }
                    else if (filter.transform.parent != null && !string.IsNullOrEmpty(filter.transform.parent.name) &&
                             filter.transform.parent.name.IndexOf("CheckPoint", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        flags |= InstanceFlags.IsCheckPoint;
                    }
                }
            }
            catch { }

            try
            {
                string objName = filter.gameObject.name;
                if (!string.IsNullOrEmpty(objName) && objName.IndexOf("NoPass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    flags |= InstanceFlags.IsNoPass;
                }
                else if (filter.transform.parent != null && !string.IsNullOrEmpty(filter.transform.parent.name) &&
                         filter.transform.parent.name.IndexOf("NoPass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    flags |= InstanceFlags.IsNoPass;
                }
                else if (renderer.sharedMaterial != null && !string.IsNullOrEmpty(renderer.sharedMaterial.name) &&
                         renderer.sharedMaterial.name.IndexOf("NoPass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    flags |= InstanceFlags.IsNoPass;
                }
            }
            catch { }

            return flags;
        }

        private static Type[] _ignoredDynamicTypes;
        private static bool _ignoredDynamicTypesSearched;

        private static Type[] GetIgnoredDynamicTypes()
        {
            if (!_ignoredDynamicTypesSearched)
            {
                _ignoredDynamicTypesSearched = true;
                var list = new List<Type>();
                var targetNames = new HashSet<string>
                {
                    "EnemyIdentifier",
                    "SpawnEffect",
                    "SeasonalHats",
                    "NewMovement",
                    "PlayerTracker",
                    "Projectile",
                    "Coin",
                    "Nail",
                    "Grenade",
                    "ItemIdentifier",
                    "Skull",
                    "BloodAbsorber"
                };

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var name in targetNames)
                    {
                        var t = asm.GetType(name);
                        if (t != null && !list.Contains(t))
                            list.Add(t);
                    }
                }
                _ignoredDynamicTypes = list.ToArray();
            }
            return _ignoredDynamicTypes;
        }

        private static bool IsDynamicOrIgnored(MeshFilter filter, Renderer renderer, bool isCombinedMesh)
        {
            if (isCombinedMesh)
                return false;

            // Skip scrolling / animated UV renderers — they are captured dynamically per-frame with live UV offsets
            if (RemixScrollingTextureDetector.IsScrollingRenderer(renderer))
                return true;

            try
            {
                var types = GetIgnoredDynamicTypes();
                if (types != null)
                {
                    for (int i = 0; i < types.Length; i++)
                    {
                        if (renderer.GetComponentInParent(types[i]) != null)
                            return true;
                    }
                }
            }
            catch { }

            try
            {
                string objName = filter.gameObject.name;
                if (!string.IsNullOrEmpty(objName))
                {
                    if (objName.IndexOf("SpawnEffect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        objName.IndexOf("SeasonalHats", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        objName.IndexOf("Pumpkin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        objName.IndexOf("SantaHat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        objName.IndexOf("EasterBunny", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                if (filter.transform.parent != null)
                {
                    string parentName = filter.transform.parent.name;
                    if (!string.IsNullOrEmpty(parentName))
                    {
                        if (parentName.IndexOf("SpawnEffect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            parentName.IndexOf("SeasonalHats", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            parentName.IndexOf("Halloween", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            parentName.IndexOf("Christmas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            parentName.IndexOf("Easter", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private int ScanScene(Scene scene, bool logDiagnostics)
        {
            var filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            var combinedDataCache = new Dictionary<int, (Vector3[] verts, Vector3[] norms, Vector2[] uvs, Color32[] cols, int[][] subIndices)>();
            // Every other mesh, read once per scan and shared by all its renderers (a level places
            // the same rock or plant thousands of times; nothing downstream writes to these arrays).
            var sharedMeshData = new Dictionary<int, (Vector3[] verts, Vector3[] norms, Vector2[] uvs, Color32[] cols, int[][] subIndices)>();
            int sharedMeshReuses = 0;
int queued = 0;
            int skippedAlreadyScanned = 0, skippedWrongScene = 0, skippedNoRenderer = 0;
            int skippedInactive = 0, skippedDynamic = 0, skippedLayer = 0;
            int skippedNoMesh = 0, skippedNoVerts = 0, skippedNoTris = 0, skippedReadError = 0;
            int gpuReadbackCount = 0;
            int vertexColorCount = 0;

            foreach (var filter in filters)
            {
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
                if (IsLayerExcluded(filter.gameObject.layer))
                {
                    skippedLayer++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    skippedNoRenderer++;
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
                        try
                        {
                            if (ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out subMeshIndices))
                                gpuReadbackCount++;
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

                // For combined meshes, compact vertex arrays so each Remix mesh only
                // includes the vertices referenced by this renderer's submeshes
                if (isCombinedMesh && surfaces.Count > 0)
                    CompactMeshData(ref vertices, ref normals, ref uvs, ref colors, surfaces);

                if (surfaces.Count == 0)
                {
                    skippedNoTris++;
                    continue;
                }

                // Validate indices across all surfaces
                bool valid = true;
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

                // Objects with the same mesh and materials share one Remix mesh (a level places the
                // same rock thousands of times); each keeps its own instance. Combined static-batch
                // meshes hold per-renderer world-space slices: one mesh per object.
                ulong meshHash = isCombinedMesh
                    ? GenerateInstanceMeshHash(mesh, filter.transform)
                    : GenerateSharedMeshHash(mesh, vertices, surfaces, surfaceMaterials);
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);

                if (colors != null && colors.Length > 0)
                    vertexColorCount++;

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
                        LocalToWorld = isCombinedMesh ? Matrix4x4.identity : filter.transform.localToWorldMatrix,
                        Layer = filter.gameObject.layer,
                        SourceRenderer = renderer,
                        SourceGameObject = filter.gameObject,
                        SourceTransform = filter.transform,
                        Flags = DetermineInstanceFlags(filter, renderer, isCombinedMesh),
                    });
                }
                queued++;
            }

            if (logDiagnostics || queued > 0)
            {
                logger.LogInfo($"Scene scan '{scene.name}': {filters.Length} total MeshFilters, {queued} queued ({sharedMeshData.Count} distinct meshes read, {sharedMeshReuses} reused; {gpuReadbackCount} via GPU readback, {vertexColorCount} with vertex colors)" +
                    $" | skipped: {skippedWrongScene} wrong scene, {skippedAlreadyScanned} already scanned," +
                    $" {skippedInactive} inactive, {skippedLayer} layer excluded, {skippedDynamic} dynamic/ignored, {skippedNoRenderer} no renderer, {skippedNoMesh} no mesh," +
                    $" {skippedReadError} read error, {skippedNoVerts} no verts, {skippedNoTris} no tris");
                materialManager.LogMaterialStats();
            }

            return queued;
        }

        private void DrainStreamingBatch()
        {
            materialManager.ProcessPendingTextureUploads();

            ScannedMeshData[] batch;
            int batchGeneration;
            lock (streamLock)
            {
                batchGeneration = clearGeneration;
                int count = Math.Min(MeshesPerFrame, streamingQueue.Count);
                if (count == 0)
                {
                    if (streamingActive)
                    {
                        streamingActive = false;
                        logger.LogInfo($"Streaming complete: {currentInstances.Count} scene mesh instances loaded");
                    }
                    return;
                }
                batch = new ScannedMeshData[count];
                for (int i = 0; i < count; i++)
                    batch[i] = streamingQueue.Dequeue();
            }

            var newInstances = new List<InstanceData>(batch.Length);
            var newRenderers = new List<ScannedRendererRef>(batch.Length);

            foreach (var entry in batch)
            {
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
            if (vertexColorWeight <= 0f)
                hasColors = false;

            if (norms == null || norms.Length != verts.Length)
            {
                norms = ComputeFaceNormals(verts, data.Surfaces.Select(s => s.Indices).ToArray());
                logger.LogDebug($"Mesh 0x{data.MeshHash:X16}: normals missing, computed from face geometry");
            }

            if (uvs == null || uvs.Length != verts.Length)
                uvs = new Vector2[verts.Length];

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
                for (int s = 0; s < data.Surfaces.Length; s++)
                {
                    var surf = data.Surfaces[s];
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
                        RemixMeshConverter.SplitByUp(validIndices, norms, topThreshold, out var topIndices, out var otherIndices);
                        if (otherIndices.Length >= 3) parts.Add((otherIndices, surf.MaterialId));
                        if (topIndices.Length >= 3) parts.Add((topIndices, topMaterialId));
                    }
                    else
                    {
                        parts.Add((validIndices.ToArray(), surf.MaterialId));
                    }

                    foreach (var (surfIndices, partMaterialId) in parts)
                    {
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
                            var surfVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
                            for (int i = 0; i < verts.Length; i++)
                            {
                                Vector2 uv = projected ? RemixMeshConverter.ProjectUV(verts[i], norms[i], projection) : uvs[i];
                                float u = uv.x * st.x + st.z;
                                float v = uv.y * st.y + st.w;
                                uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(WeightVertexColor(cols[i], vertexColorWeight)) : 0xFFFFFFFF;
                                surfVerts[i] = RemixAPI.MakeVertex(
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
        private static ulong GenerateSharedMeshHash(Mesh mesh, Vector3[] vertices, List<SubMeshSurface> surfaces, List<Material> surfaceMaterials)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(ulong value) { hash = (hash ^ value) * 1099511628211UL; }
            void MixString(string text)
            {
                if (text == null) { Mix(0); return; }
                foreach (char c in text) Mix(c);
                Mix(0xFF);
            }
            void MixFloat(float value) { Mix((ulong)BitConverter.ToInt32(BitConverter.GetBytes(value), 0)); }

            Mix(0x5348415245444D53UL); // distinct from the per-object hashes
            MixString(mesh.name);
            Mix((ulong)vertices.Length);
            int step = Math.Max(1, vertices.Length / 16);
            for (int i = 0; i < vertices.Length; i += step)
            {
                MixFloat(vertices[i].x);
                MixFloat(vertices[i].y);
                MixFloat(vertices[i].z);
            }
            for (int s = 0; s < surfaces.Count; s++)
            {
                Mix((ulong)surfaces[s].Indices.Length);
                var mat = s < surfaceMaterials.Count ? surfaceMaterials[s] : null;
                if (mat == null) { Mix(0); continue; }
                MixString(mat.name.Replace(" (Instance)", "").Replace("(Clone)", "").Trim());
                MixString(mat.shader != null ? mat.shader.name : null);
                try
                {
                    var tex = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                    MixString(tex != null ? tex.name : null);
                    foreach (var prop in SharedHashColorProps)
                    {
                        if (!mat.HasProperty(prop)) continue;
                        var c = mat.GetColor(prop);
                        MixFloat(c.r); MixFloat(c.g); MixFloat(c.b); MixFloat(c.a);
                    }
                }
                catch { }
            }
            return hash == 0 ? 1 : hash;
        }

        private static readonly string[] SharedHashColorProps = { "_Color", "_BaseColor", "_Tint" };

        private static ulong GenerateInstanceMeshHash(Mesh mesh, Transform transform)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis

            // Include transform hierarchy hash so each object gets a persistent unique Remix mesh
            hash ^= HashUtils.GetHierarchyHash(transform);
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
