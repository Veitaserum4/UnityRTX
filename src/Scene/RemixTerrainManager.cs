using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityRemix
{
    /// <summary>
    /// Manages discovery, heightmap geometry extraction, splatmap texture baking,
    /// and streaming of UnityEngine.Terrain objects into RTX Remix / OpenRemix.
    /// </summary>
    public class RemixTerrainManager
    {
        private readonly ManualLogSource logger;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        private readonly object apiLock;
        private readonly Func<int, bool> isLayerDisabled;
        private readonly BepInEx.Configuration.ConfigEntry<int> configChunkSize;
        private readonly BepInEx.Configuration.ConfigEntry<int> configBakeResolution;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configEnableTerrain;

        public struct TerrainChunkData
        {
            public int TerrainId;
            public ulong ChunkMeshHash;
            public RemixAPI.remixapi_HardcodedVertex[] Vertices;
            public uint[] Indices;
            public int MaterialId;
            public Vector3 BoundsCenter;
            public float BoundsRadius;
            public Matrix4x4 LocalToWorld;
            public int Layer;
        }

        public struct TerrainInstanceData
        {
            public IntPtr MeshHandle;
            public RemixAPI.remixapi_Transform Transform;
            public int Layer;
            public Vector3 BoundsCenter;
            public float BoundsRadius;
            public int RendererInstanceId;
            public int TerrainId;
        }

        // Streaming queue: Main thread pushes extracted chunk data, Render thread drains batches
        private readonly Queue<TerrainChunkData> streamingQueue = new Queue<TerrainChunkData>();
        private readonly object streamLock = new object();
        private volatile bool streamingActive;

        // Completed instances drawn on render thread
        private readonly List<TerrainInstanceData> currentInstances = new List<TerrainInstanceData>();
        private readonly object instanceLock = new object();

        // Visibility-filtered snapshot built on main thread
        private TerrainInstanceData[] visibleInstances;

        // Track processed terrain instance IDs to avoid redundant processing
        private readonly HashSet<int> processedTerrainIds = new HashSet<int>();
        private readonly List<IntPtr> createdMeshHandles = new List<IntPtr>();
        private readonly List<Texture2D> createdTextures = new List<Texture2D>();

        // Periodic rescan timer for dynamically/async loaded terrains
        private float rescanTimer;
        private const float RescanInterval = 1.0f;
        private const float RescanDuration = 20.0f;
        private float timeSinceSceneLoad;
        private Scene activeScene;

        public bool HasData => currentInstances.Count > 0;
        public bool IsStreaming => streamingActive;
        public int ChunkCount => currentInstances.Count;

        public RemixTerrainManager(
            ManualLogSource logger,
            RemixMeshConverter meshConverter,
            RemixMaterialManager materialManager,
            object apiLock,
            Func<int, bool> isLayerDisabled,
            BepInEx.Configuration.ConfigEntry<int> configChunkSize,
            BepInEx.Configuration.ConfigEntry<int> configBakeResolution,
            BepInEx.Configuration.ConfigEntry<bool> configEnableTerrain)
        {
            this.logger = logger;
            this.meshConverter = meshConverter;
            this.materialManager = materialManager;
            this.apiLock = apiLock;
            this.isLayerDisabled = isLayerDisabled;
            this.configChunkSize = configChunkSize;
            this.configBakeResolution = configBakeResolution;
            this.configEnableTerrain = configEnableTerrain;
        }

        /// <summary>
        /// Called on the main thread when a scene loads.
        /// </summary>
        public void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (configEnableTerrain != null && !configEnableTerrain.Value)
                return;

            if (mode == LoadSceneMode.Single)
            {
                ClearData();
            }

            activeScene = scene;
            timeSinceSceneLoad = 0f;
            rescanTimer = 0f;

            ScanAndProcessTerrains();
        }

        /// <summary>
        /// Periodic check on main thread for newly loaded terrains (e.g. async/Addressables).
        /// </summary>
        public void Update(float deltaTime)
        {
            if (configEnableTerrain != null && !configEnableTerrain.Value)
                return;

            if (!activeScene.IsValid())
                return;

            timeSinceSceneLoad += deltaTime;
            if (timeSinceSceneLoad > RescanDuration)
                return;

            rescanTimer += deltaTime;
            if (rescanTimer < RescanInterval)
                return;
            rescanTimer = 0f;

            ScanAndProcessTerrains();
        }

        /// <summary>
        /// Find all active terrains in the scene and process any that haven't been queued yet.
        /// </summary>
        private void ScanAndProcessTerrains()
        {
            try
            {
                var terrains = Terrain.activeTerrains;
                if (terrains == null || terrains.Length == 0)
                {
                    terrains = Resources.FindObjectsOfTypeAll<Terrain>();
                }

                if (terrains == null || terrains.Length == 0)
                    return;

                foreach (var terrain in terrains)
                {
                    if (terrain == null || !terrain.enabled || !terrain.gameObject.activeInHierarchy)
                        continue;

                    if (!terrain.drawHeightmap)
                        continue;

                    int id = terrain.GetInstanceID();
                    if (processedTerrainIds.Contains(id))
                        continue;

                    if (isLayerDisabled != null && isLayerDisabled(terrain.gameObject.layer))
                        continue;

                    ProcessTerrain(terrain);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[RemixTerrain] Error scanning terrains: {ex}");
            }
        }

        /// <summary>
        /// Extract terrain heightmap, bake splatmap textures, slice into chunks, and queue for Remix.
        /// </summary>
        private void ProcessTerrain(Terrain terrain)
        {
            var tData = terrain.terrainData;
            if (tData == null) return;

            int terrainId = terrain.GetInstanceID();
            processedTerrainIds.Add(terrainId);

            int hRes = tData.heightmapResolution;
            Vector3 size = tData.size;
            int quadRes = hRes - 1;

            logger.LogInfo($"[RemixTerrain] Processing terrain '{terrain.name}' (id={terrainId}): size={size}, heightmapRes={hRes}, layers={(tData.terrainLayers != null ? tData.terrainLayers.Length : 0)}, alphamaps={(tData.alphamapTextures != null ? tData.alphamapTextures.Length : 0)}");

            // 1. Bake composite albedo and normal textures
            int bakeRes = configBakeResolution != null ? Mathf.Clamp(configBakeResolution.Value, 512, 4096) : 2048;
            var (bakedAlbedo, bakedNormal) = BakeTerrainTextures(terrain, tData, bakeRes);
            if (bakedAlbedo != null) createdTextures.Add(bakedAlbedo);
            if (bakedNormal != null) createdTextures.Add(bakedNormal);

            // 2. Register material with RemixMaterialManager
            string matName = $"Terrain_{terrain.name}_{terrainId}";
            int materialId = materialManager.RegisterCustomMaterial(matName, bakedAlbedo, bakedNormal, 0.85f, 0.0f);

            // 3. Extract heightmap grid
            float[,] heights = tData.GetHeights(0, 0, hRes, hRes);

            // Check for terrain holes
            bool[,] holes = null;
            try
            {
                if (tData.holesResolution > 0)
                {
                    holes = tData.GetHoles(0, 0, tData.holesResolution, tData.holesResolution);
                }
            }
            catch { }

            // 4. Divide into chunks (default 64x64 quads)
            int chunkSize = configChunkSize != null ? Mathf.Clamp(configChunkSize.Value, 16, 128) : 64;
            int chunksX = Mathf.CeilToInt((float)quadRes / chunkSize);
            int chunksZ = Mathf.CeilToInt((float)quadRes / chunkSize);

            float stepX = size.x / quadRes;
            float stepZ = size.z / quadRes;

            int totalChunks = 0;
            var chunksToQueue = new List<TerrainChunkData>(chunksX * chunksZ);

            Matrix4x4 localToWorld = terrain.transform.localToWorldMatrix;
            int layer = terrain.gameObject.layer;

            for (int cz = 0; cz < chunksZ; cz++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    int x0 = cx * chunkSize;
                    int x1 = Mathf.Min(x0 + chunkSize, quadRes);
                    int z0 = cz * chunkSize;
                    int z1 = Mathf.Min(z0 + chunkSize, quadRes);

                    int quadsW = x1 - x0;
                    int quadsH = z1 - z0;
                    if (quadsW <= 0 || quadsH <= 0) continue;

                    int vertCountX = quadsW + 1;
                    int vertCountZ = quadsH + 1;
                    int totalVerts = vertCountX * vertCountZ;

                    var remixVerts = new RemixAPI.remixapi_HardcodedVertex[totalVerts];
                    Vector3 chunkMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 chunkMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                    // Build vertices
                    for (int iz = 0; iz < vertCountZ; iz++)
                    {
                        int gz = z0 + iz;
                        float normV = gz / (float)quadRes;
                        float posZ = normV * size.z;

                        for (int ix = 0; ix < vertCountX; ix++)
                        {
                            int gx = x0 + ix;
                            float normU = gx / (float)quadRes;
                            float posX = normU * size.x;
                            float posY = heights[gz, gx] * size.y;

                            Vector3 pos = new Vector3(posX, posY, posZ);
                            chunkMin = Vector3.Min(chunkMin, pos);
                            chunkMax = Vector3.Max(chunkMax, pos);

                            // Central difference normal calculation from heightmap
                            int gxPrev = Mathf.Max(gx - 1, 0);
                            int gxNext = Mathf.Min(gx + 1, quadRes);
                            int gzPrev = Mathf.Max(gz - 1, 0);
                            int gzNext = Mathf.Min(gz + 1, quadRes);

                            float dhx = (heights[gz, gxNext] - heights[gz, gxPrev]) * size.y;
                            float spanX = (gxNext - gxPrev) * stepX;
                            float dhz = (heights[gzNext, gx] - heights[gzPrev, gx]) * size.y;
                            float spanZ = (gzNext - gzPrev) * stepZ;

                            Vector3 normal = Vector3.Normalize(new Vector3(-dhx / spanX, 1.0f, -dhz / spanZ));

                            // Unity (X, Y up, Z) -> Remix (X, Z depth, Y up)
                            int vIdx = iz * vertCountX + ix;
                            remixVerts[vIdx] = RemixAPI.MakeVertex(
                                pos.x, pos.z, pos.y,
                                normal.x, normal.z, normal.y,
                                normU, normV,
                                0xFFFFFFFF
                            );
                        }
                    }

                    // Build triangle indices (clockwise from top-down)
                    var indicesList = new List<uint>(quadsW * quadsH * 6);
                    for (int qz = 0; qz < quadsH; qz++)
                    {
                        for (int qx = 0; qx < quadsW; qx++)
                        {
                            // Check hole
                            if (holes != null)
                            {
                                int hx = Mathf.Clamp(Mathf.RoundToInt(((x0 + qx) / (float)quadRes) * (tData.holesResolution - 1)), 0, tData.holesResolution - 1);
                                int hz = Mathf.Clamp(Mathf.RoundToInt(((z0 + qz) / (float)quadRes) * (tData.holesResolution - 1)), 0, tData.holesResolution - 1);
                                if (!holes[hz, hx])
                                    continue; // Skip hole quad
                            }

                            uint v00 = (uint)(qz * vertCountX + qx);
                            uint v01 = (uint)((qz + 1) * vertCountX + qx);
                            uint v10 = (uint)(qz * vertCountX + (qx + 1));
                            uint v11 = (uint)((qz + 1) * vertCountX + (qx + 1));

                            // Triangle 1: (v00, v01, v11)
                            indicesList.Add(v00);
                            indicesList.Add(v01);
                            indicesList.Add(v11);

                            // Triangle 2: (v00, v11, v10)
                            indicesList.Add(v00);
                            indicesList.Add(v11);
                            indicesList.Add(v10);
                        }
                    }

                    if (indicesList.Count == 0)
                        continue;

                    Vector3 localCenter = (chunkMin + chunkMax) * 0.5f;
                    Vector3 worldCenter = localToWorld.MultiplyPoint3x4(localCenter);
                    float radius = Vector3.Distance(chunkMax, localCenter);

                    ulong chunkHash = HashUtils.HashStringFNV($"Terrain_{terrainId}_Chunk_{cx}_{cz}");

                    chunksToQueue.Add(new TerrainChunkData
                    {
                        TerrainId = terrainId,
                        ChunkMeshHash = chunkHash,
                        Vertices = remixVerts,
                        Indices = indicesList.ToArray(),
                        MaterialId = materialId,
                        BoundsCenter = worldCenter,
                        BoundsRadius = radius,
                        LocalToWorld = localToWorld,
                        Layer = layer
                    });

                    totalChunks++;
                }
            }

            lock (streamLock)
            {
                foreach (var chunk in chunksToQueue)
                {
                    streamingQueue.Enqueue(chunk);
                }
                streamingActive = true;
            }

            logger.LogInfo($"[RemixTerrain] Queued {totalChunks} chunks ({chunkSize}x{chunkSize} quads) for terrain '{terrain.name}' to render thread");
        }

        /// <summary>
        /// Bake composite albedo and normal textures from terrain layers and alphamaps.
        /// </summary>
        private (Texture2D albedo, Texture2D normal) BakeTerrainTextures(Terrain terrain, TerrainData tData, int resolution)
        {
            try
            {
                var alphamaps = tData.alphamapTextures;
                var layers = tData.terrainLayers;

                // If no alphamaps or no layers, fallback to terrain material template or white texture
                if (alphamaps == null || alphamaps.Length == 0 || layers == null || layers.Length == 0)
                {
                    logger.LogWarning($"[RemixTerrain] Terrain '{terrain.name}' has no alphamaps or layers; using fallback texture");
                    var fb = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                    Color fbColor = Color.gray;
                    if (terrain.materialTemplate != null && terrain.materialTemplate.HasProperty("_Color"))
                        fbColor = terrain.materialTemplate.color;
                    Color32[] fbPixels = new Color32[64 * 64];
                    for (int i = 0; i < fbPixels.Length; i++) fbPixels[i] = fbColor;
                    fb.SetPixels32(fbPixels);
                    fb.Apply();
                    return (fb, null);
                }

                // Prepare readable copies of alphamaps
                var readableAlphas = new Texture2D[alphamaps.Length];
                var alphaPixelBuffers = new Color32[alphamaps.Length][];
                for (int a = 0; a < alphamaps.Length; a++)
                {
                    readableAlphas[a] = EnsureReadable(alphamaps[a], linear: true);
                    alphaPixelBuffers[a] = readableAlphas[a] != null ? readableAlphas[a].GetPixels32() : null;
                }

                // Prepare readable copies of layer textures
                int layerCount = layers.Length;
                var layerDiffusePixels = new Color32[layerCount][];
                var layerNormalPixels = new Color32[layerCount][];
                int[] layerDiffW = new int[layerCount];
                int[] layerDiffH = new int[layerCount];
                int[] layerNormW = new int[layerCount];
                int[] layerNormH = new int[layerCount];
                Vector2[] layerTileSizes = new Vector2[layerCount];
                Vector2[] layerTileOffsets = new Vector2[layerCount];
                bool anyNormals = false;

                for (int l = 0; l < layerCount; l++)
                {
                    var layer = layers[l];
                    if (layer == null) continue;

                    layerTileSizes[l] = layer.tileSize.x > 0 && layer.tileSize.y > 0 ? layer.tileSize : new Vector2(15f, 15f);
                    layerTileOffsets[l] = layer.tileOffset;

                    if (layer.diffuseTexture != null)
                    {
                        var readDiff = EnsureReadable(layer.diffuseTexture, linear: false);
                        if (readDiff != null)
                        {
                            layerDiffW[l] = readDiff.width;
                            layerDiffH[l] = readDiff.height;
                            layerDiffusePixels[l] = readDiff.GetPixels32();
                            if (readDiff != layer.diffuseTexture) UnityEngine.Object.Destroy(readDiff);
                        }
                    }

                    if (layer.normalMapTexture != null)
                    {
                        var readNorm = EnsureReadable(layer.normalMapTexture, linear: true);
                        if (readNorm != null)
                        {
                            layerNormW[l] = readNorm.width;
                            layerNormH[l] = readNorm.height;
                            layerNormalPixels[l] = readNorm.GetPixels32();
                            anyNormals = true;
                            if (readNorm != layer.normalMapTexture) UnityEngine.Object.Destroy(readNorm);
                        }
                    }
                }

                // Bake output buffers
                int bakeW = resolution;
                int bakeH = resolution;
                var outAlbedo = new Color32[bakeW * bakeH];
                var outNormal = anyNormals ? new Color32[bakeW * bakeH] : null;

                Vector3 terrainSize = tData.size;
                int alpha0W = readableAlphas[0] != null ? readableAlphas[0].width : 1;
                int alpha0H = readableAlphas[0] != null ? readableAlphas[0].height : 1;

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Multi-threaded CPU bake
                Parallel.For(0, bakeH, py =>
                {
                    float v = (py + 0.5f) / bakeH;
                    float worldZ = v * terrainSize.z;

                    for (int px = 0; px < bakeW; px++)
                    {
                        float u = (px + 0.5f) / bakeW;
                        float worldX = u * terrainSize.x;
                        int outIdx = py * bakeW + px;

                        // Sample layer weights from alphamaps
                        float sumWeight = 0f;
                        float[] weights = new float[layerCount];

                        for (int l = 0; l < layerCount; l++)
                        {
                            int mapIdx = l / 4;
                            int chanIdx = l % 4;

                            if (mapIdx < alphaPixelBuffers.Length && alphaPixelBuffers[mapIdx] != null)
                            {
                                int aW = readableAlphas[mapIdx].width;
                                int aH = readableAlphas[mapIdx].height;
                                int ax = Mathf.Clamp((int)(u * aW), 0, aW - 1);
                                int ay = Mathf.Clamp((int)(v * aH), 0, aH - 1);
                                var ap = alphaPixelBuffers[mapIdx][ay * aW + ax];

                                byte bVal = chanIdx == 0 ? ap.r : (chanIdx == 1 ? ap.g : (chanIdx == 2 ? ap.b : ap.a));
                                float w = bVal / 255.0f;
                                weights[l] = w;
                                sumWeight += w;
                            }
                        }

                        if (sumWeight > 0.0001f)
                        {
                            for (int l = 0; l < layerCount; l++) weights[l] /= sumWeight;
                        }
                        else
                        {
                            weights[0] = 1.0f;
                        }

                        // Accumulate albedo and normals
                        float accR = 0f, accG = 0f, accB = 0f;
                        float accNx = 0f, accNy = 0f, accNz = 0f;

                        for (int l = 0; l < layerCount; l++)
                        {
                            float w = weights[l];
                            if (w <= 0.001f) continue;

                            // Tile UV
                            Vector2 tSize = layerTileSizes[l];
                            Vector2 tOff = layerTileOffsets[l];
                            float lu = (worldX / tSize.x) + tOff.x;
                            float lv = (worldZ / tSize.y) + tOff.y;

                            // Wrap UV
                            lu = (lu % 1.0f + 1.0f) % 1.0f;
                            lv = (lv % 1.0f + 1.0f) % 1.0f;

                            // Sample diffuse
                            if (layerDiffusePixels[l] != null)
                            {
                                int lw = layerDiffW[l];
                                int lh = layerDiffH[l];
                                int lx = Mathf.Clamp((int)(lu * lw), 0, lw - 1);
                                int ly = Mathf.Clamp((int)(lv * lh), 0, lh - 1);
                                var dp = layerDiffusePixels[l][ly * lw + lx];

                                accR += (dp.r / 255.0f) * w;
                                accG += (dp.g / 255.0f) * w;
                                accB += (dp.b / 255.0f) * w;
                            }

                            // Sample normal
                            if (outNormal != null && layerNormalPixels[l] != null)
                            {
                                int nw = layerNormW[l];
                                int nh = layerNormH[l];
                                int nx = Mathf.Clamp((int)(lu * nw), 0, nw - 1);
                                int ny = Mathf.Clamp((int)(lv * nh), 0, nh - 1);
                                var np = layerNormalPixels[l][ny * nw + nx];

                                float nnx = (np.r / 127.5f) - 1.0f;
                                float nny = (np.g / 127.5f) - 1.0f;
                                float nnz = (np.b / 127.5f) - 1.0f;

                                accNx += nnx * w;
                                accNy += nny * w;
                                accNz += nnz * w;
                            }
                        }

                        outAlbedo[outIdx] = new Color32(
                            (byte)Mathf.Clamp(accR * 255.0f, 0f, 255f),
                            (byte)Mathf.Clamp(accG * 255.0f, 0f, 255f),
                            (byte)Mathf.Clamp(accB * 255.0f, 0f, 255f),
                            255
                        );

                        if (outNormal != null)
                        {
                            Vector3 n = new Vector3(accNx, accNy, accNz);
                            if (n.sqrMagnitude > 0.0001f) n.Normalize();
                            else n = Vector3.forward;

                            outNormal[outIdx] = new Color32(
                                (byte)Mathf.Clamp((n.x * 0.5f + 0.5f) * 255.0f, 0f, 255f),
                                (byte)Mathf.Clamp((n.y * 0.5f + 0.5f) * 255.0f, 0f, 255f),
                                (byte)Mathf.Clamp((n.z * 0.5f + 0.5f) * 255.0f, 0f, 255f),
                                255
                            );
                        }
                    }
                });

                sw.Stop();
                logger.LogInfo($"[RemixTerrain] Baked {bakeW}x{bakeH} composite textures for '{terrain.name}' in {sw.ElapsedMilliseconds}ms");

                // Clean up readable alpha copies
                for (int a = 0; a < readableAlphas.Length; a++)
                {
                    if (readableAlphas[a] != null && readableAlphas[a] != alphamaps[a])
                        UnityEngine.Object.Destroy(readableAlphas[a]);
                }

                // Create Texture2D objects with mipmaps
                var albedoTex = new Texture2D(bakeW, bakeH, TextureFormat.RGBA32, true);
                albedoTex.name = $"Terrain_{terrain.name}_{terrain.GetInstanceID()}_Albedo";
                albedoTex.SetPixels32(outAlbedo);
                albedoTex.Apply(true);

                Texture2D normalTex = null;
                if (outNormal != null)
                {
                    normalTex = new Texture2D(bakeW, bakeH, TextureFormat.RGBA32, true, true);
                    normalTex.name = $"Terrain_{terrain.name}_{terrain.GetInstanceID()}_Normal";
                    normalTex.SetPixels32(outNormal);
                    normalTex.Apply(true);
                }

                return (albedoTex, normalTex);
            }
            catch (Exception ex)
            {
                logger.LogError($"[RemixTerrain] Exception baking terrain textures: {ex}");
                return (null, null);
            }
        }

        private static Texture2D EnsureReadable(Texture2D tex, bool linear)
        {
            if (tex == null) return null;
            if (tex.isReadable) return tex;

            try
            {
                var tmp = RenderTexture.GetTemporary(
                    tex.width, tex.height, 0,
                    RenderTextureFormat.ARGB32,
                    linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);

                var prev = RenderTexture.active;
                Graphics.Blit(tex, tmp);
                RenderTexture.active = tmp;

                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, linear);
                readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                readable.Apply();

                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                return readable;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Called on the main thread each frame to update distance culling snapshots.
        /// </summary>
        public void UpdateVisibility(Vector3 cameraPosition, bool useDistanceCulling, float maxRenderDistance)
        {
            lock (instanceLock)
            {
                if (currentInstances.Count == 0)
                {
                    Volatile.Write(ref visibleInstances, Array.Empty<TerrainInstanceData>());
                    return;
                }

                var visible = new List<TerrainInstanceData>(currentInstances.Count);
                float maxDistSqr = maxRenderDistance * maxRenderDistance;

                for (int i = 0; i < currentInstances.Count; i++)
                {
                    var instance = currentInstances[i];

                    if (isLayerDisabled != null && isLayerDisabled(instance.Layer))
                        continue;

                    if (useDistanceCulling)
                    {
                        // Check distance from camera to chunk bounds
                        float sqrDist = (instance.BoundsCenter - cameraPosition).sqrMagnitude;
                        float effectiveMax = maxRenderDistance + instance.BoundsRadius;
                        if (sqrDist > (effectiveMax * effectiveMax))
                            continue;
                    }

                    visible.Add(instance);
                }

                Volatile.Write(ref visibleInstances, visible.Count > 0 ? visible.ToArray() : Array.Empty<TerrainInstanceData>());
            }
        }

        /// <summary>
        /// Called on the render thread each frame. Drains mesh creation queue and returns visible instances.
        /// </summary>
        public TerrainInstanceData[] GetInstances()
        {
            DrainStreamingBatch();

            var snapshot = Volatile.Read(ref visibleInstances);
            if (snapshot != null)
                return snapshot;

            return Array.Empty<TerrainInstanceData>();
        }

        /// <summary>
        /// Drains pending terrain chunks on the render thread and creates Remix mesh handles.
        /// </summary>
        private void DrainStreamingBatch()
        {
            if (!streamingActive) return;

            var batch = new List<TerrainChunkData>();
            lock (streamLock)
            {
                const int MaxBatch = 8; // Process up to 8 chunks per frame to avoid render thread hitches
                while (streamingQueue.Count > 0 && batch.Count < MaxBatch)
                {
                    batch.Add(streamingQueue.Dequeue());
                }
                if (streamingQueue.Count == 0)
                    streamingActive = false;
            }

            if (batch.Count == 0) return;

            var createMeshFunc = meshConverter.GetCreateMeshFunc();
            if (createMeshFunc == null) return;

            var newInstances = new List<TerrainInstanceData>(batch.Count);

            foreach (var data in batch)
            {
                IntPtr meshHandle = CreateRemixMesh(data, createMeshFunc);
                if (meshHandle == IntPtr.Zero)
                    continue;

                var m = data.LocalToWorld;
                var transform = RemixAPI.remixapi_Transform.FromMatrix(
                    m.m00, m.m02, m.m01, m.m03,
                    m.m20, m.m22, m.m21, m.m23,
                    m.m10, m.m12, m.m11, m.m13
                );

                newInstances.Add(new TerrainInstanceData
                {
                    MeshHandle = meshHandle,
                    Transform = transform,
                    Layer = data.Layer,
                    BoundsCenter = data.BoundsCenter,
                    BoundsRadius = data.BoundsRadius,
                    RendererInstanceId = (int)(data.ChunkMeshHash & 0x7FFFFFFF),
                    TerrainId = data.TerrainId
                });
            }

            if (newInstances.Count > 0)
            {
                lock (instanceLock)
                {
                    currentInstances.AddRange(newInstances);
                }
            }
        }

        private IntPtr CreateRemixMesh(TerrainChunkData data, RemixAPI.PFN_remixapi_CreateMesh createMeshFunc)
        {
            if (data.Vertices == null || data.Vertices.Length == 0 ||
                data.Indices == null || data.Indices.Length == 0)
                return IntPtr.Zero;

            var vertexHandles = new List<GCHandle>();
            var indexHandles = new List<GCHandle>();
            var surfaceHandles = new List<GCHandle>();

            try
            {
                var vertHandle = GCHandle.Alloc(data.Vertices, GCHandleType.Pinned);
                vertexHandles.Add(vertHandle);

                var idxHandle = GCHandle.Alloc(data.Indices, GCHandleType.Pinned);
                indexHandles.Add(idxHandle);

                IntPtr materialHandle = materialManager.GetOrCreateMaterial(data.MaterialId);

                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)data.Vertices.Length,
                    indices_values = idxHandle.AddrOfPinnedObject(),
                    indices_count = (ulong)data.Indices.Length,
                    skinning_hasvalue = 0,
                    skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                    material = materialHandle
                };

                var surfacesArray = new RemixAPI.remixapi_MeshInfoSurfaceTriangles[] { surface };
                var surfaceArrayHandle = GCHandle.Alloc(surfacesArray, GCHandleType.Pinned);
                surfaceHandles.Add(surfaceArrayHandle);

                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = data.ChunkMeshHash,
                    surfaces_values = surfaceArrayHandle.AddrOfPinnedObject(),
                    surfaces_count = 1
                };

                IntPtr handle = IntPtr.Zero;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createMeshFunc(ref meshInfo, out handle);
                }

                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS && handle != IntPtr.Zero)
                {
                    lock (createdMeshHandles)
                        createdMeshHandles.Add(handle);
                    return handle;
                }

                logger.LogError($"[RemixTerrain] Failed to create terrain chunk mesh (hash 0x{data.ChunkMeshHash:X16}): {result}");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                logger.LogError($"[RemixTerrain] Exception creating mesh 0x{data.ChunkMeshHash:X16}: {ex}");
                return IntPtr.Zero;
            }
            finally
            {
                foreach (var h in vertexHandles) if (h.IsAllocated) h.Free();
                foreach (var h in indexHandles) if (h.IsAllocated) h.Free();
                foreach (var h in surfaceHandles) if (h.IsAllocated) h.Free();
            }
        }

        /// <summary>
        /// Clears all cached terrain data, queues, and instances on scene unload.
        /// </summary>
        public void ClearData()
        {
            lock (instanceLock)
            {
                currentInstances.Clear();
            }

            lock (streamLock)
            {
                streamingQueue.Clear();
                streamingActive = false;
            }

            processedTerrainIds.Clear();
            Volatile.Write(ref visibleInstances, null);
            activeScene = default;

            var destroyMeshFunc = meshConverter.GetDestroyMeshFunc();
            if (destroyMeshFunc != null)
            {
                lock (createdMeshHandles)
                {
                    foreach (var h in createdMeshHandles)
                    {
                        if (h != IntPtr.Zero)
                        {
                            try
                            {
                                lock (apiLock)
                                {
                                    destroyMeshFunc(h);
                                }
                            }
                            catch { }
                        }
                    }
                    createdMeshHandles.Clear();
                }
            }

            foreach (var tex in createdTextures)
            {
                if (tex != null)
                    UnityEngine.Object.Destroy(tex);
            }
            createdTextures.Clear();
        }
    }
}
