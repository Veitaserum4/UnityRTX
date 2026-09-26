using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Autodetects standard Unity skyboxes (RenderSettings.skybox and solid camera backgrounds),
    /// bakes or extracts 6-face cubemap textures, constructs an inward-facing skybox cube mesh,
    /// and submits it to RTX Remix tagged with REMIXAPI_INSTANCE_CATEGORY_BIT_SKY.
    /// </summary>
    public class RemixSkyboxManager
    {
        private readonly ManualLogSource logger;
        private readonly RemixMaterialManager materialManager;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixCameraHandler cameraHandler;
        private readonly ConfigEntry<bool> configEnableSkybox;

        private RemixFrameCapture frameCapture;

        private Mesh skyboxMesh;
        private Material[] skyMaterials = new Material[6];
        private Texture2D[] skyFaceTextures = new Texture2D[6];

        private int skyboxVersion = 0;
        private ulong skyboxMeshKey = 0;
        private int skyboxMeshId = 0;
        private ulong skyboxMeshHash = 0;

        private Material lastCapturedSkyMat = null;
        private CameraClearFlags lastCapturedClearFlags = (CameraClearFlags)(-1);
        private Color lastCapturedBgColor = Color.clear;
        private bool needsCapture = true;

        private string statusText = "Initializing...";
        public string StatusText => statusText;

        public RemixSkyboxManager(
            ManualLogSource logger,
            RemixMaterialManager materialManager,
            RemixMeshConverter meshConverter,
            RemixCameraHandler cameraHandler,
            ConfigEntry<bool> enableSkybox)
        {
            this.logger = logger;
            this.materialManager = materialManager;
            this.meshConverter = meshConverter;
            this.cameraHandler = cameraHandler;
            this.configEnableSkybox = enableSkybox;
        }

        public void SetFrameCapture(RemixFrameCapture capture)
        {
            this.frameCapture = capture;
        }

        public void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene)
        {
            needsCapture = true;
            logger?.LogInfo($"[RemixSkyboxManager] Scene '{scene.name}' loaded, skybox capture scheduled.");
        }

        /// <summary>
        /// Updates the skybox state on the Unity main thread.
        /// Bakes or captures the skybox if the material, clearFlags, or scene changed.
        /// </summary>
        public void UpdateSkybox(Camera mainCam, int frameCount)
        {
            if (configEnableSkybox != null && !configEnableSkybox.Value)
            {
                statusText = "Disabled";
                return;
            }

            if (mainCam == null)
            {
                mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
                if (mainCam == null) return;
            }

            Material currentSkyMat = RenderSettings.skybox;

            // Check if camera has a Skybox component that overrides RenderSettings.skybox
            var camSkyboxComp = mainCam.GetComponent<Skybox>();
            if (camSkyboxComp != null && camSkyboxComp.material != null)
            {
                currentSkyMat = camSkyboxComp.material;
            }

            CameraClearFlags curFlags = mainCam.clearFlags;
            Color curBg = mainCam.backgroundColor;

            bool matChanged = (currentSkyMat != lastCapturedSkyMat);
            bool flagsChanged = (curFlags != lastCapturedClearFlags);
            bool bgChanged = (currentSkyMat == null && curBg != lastCapturedBgColor);

            if (needsCapture || matChanged || flagsChanged || bgChanged)
            {
                CaptureAndPrepareSkybox(mainCam, currentSkyMat, curFlags, curBg);
            }
        }

        /// <summary>
        /// Emits the skybox mesh instance into the frame state, centered on the camera and tagged as SKY.
        /// </summary>
        public void EmitSkyboxInstance(RemixFrameCapture.FrameState state, Camera mainCam)
        {
            if (configEnableSkybox != null && !configEnableSkybox.Value)
                return;

            if (state == null || skyboxMeshKey == 0 || mainCam == null)
                return;

            // Only emit once the mesh has been created in Remix
            if (!meshConverter.IsMeshCached(skyboxMeshKey))
                return;

            float farPlane = mainCam.farClipPlane > 10f ? mainCam.farClipPlane : 1000f;
            float targetDist = Mathf.Max(farPlane * 0.85f, 50f);
            float scale = targetDist * 2.0f; // Unit cube radius is 0.5 * scale = targetDist

            Quaternion skyRot = Quaternion.identity;
            if (lastCapturedSkyMat != null && lastCapturedSkyMat.HasProperty("_Rotation"))
            {
                try
                {
                    float rot = lastCapturedSkyMat.GetFloat("_Rotation");
                    skyRot = Quaternion.Euler(0, -rot, 0);
                }
                catch { }
            }

            Matrix4x4 l2w = Matrix4x4.TRS(
                mainCam.transform.position,
                skyRot,
                Vector3.one * scale
            );

            state.instances.Add(new RemixFrameCapture.MeshInstanceData
            {
                meshKey = skyboxMeshKey,
                meshId = skyboxMeshId,
                localToWorld = l2w,
                rendererInstanceId = 0,
                dedupeKey = default,
                categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_SKY
            });
        }

        private void CaptureAndPrepareSkybox(Camera mainCam, Material skyMat, CameraClearFlags clearFlags, Color bgColor)
        {
            try
            {
                skyboxVersion++;
                EnsureSkyboxCubeMesh();

                bool capturedCubemap = false;
                if (skyMat != null && clearFlags == CameraClearFlags.Skybox)
                {
                    capturedCubemap = CaptureCubemapTextures(mainCam, skyMat);
                }

                if (!capturedCubemap)
                {
                    CaptureSolidColorTextures(bgColor);
                }

                if (skyFaceTextures[0] != null)
                {
                    Color sample = skyFaceTextures[0].GetPixel(skyFaceTextures[0].width / 2, skyFaceTextures[0].height / 2);
                    logger?.LogInfo($"[RemixSkyboxManager] Captured skybox face 0 center pixel: RGBA({sample.r:F3}, {sample.g:F3}, {sample.b:F3}, {sample.a:F3})");
                }

                // Ensure materials are created for the 6 faces
                Shader unlitShader = Shader.Find("Unlit/Texture")
                                  ?? Shader.Find("UI/Default")
                                  ?? Shader.Find("Standard")
                                  ?? (skyMat != null ? skyMat.shader : null);

                List<Material> submeshMaterials = new List<Material>(6);
                List<int> submeshMaterialIds = new List<int>(6);

                for (int i = 0; i < 6; i++)
                {
                    if (skyMaterials[i] != null)
                    {
                        UnityEngine.Object.Destroy(skyMaterials[i]);
                    }

                    skyMaterials[i] = new Material(unlitShader);
                    skyMaterials[i].name = $"RemixSkyMaterial_v{skyboxVersion}_Face_{i}";
                    skyMaterials[i].mainTexture = skyFaceTextures[i];

                    // Unique synthetic material ID per version and face to ensure fresh material registration in Remix
                    int matId = -2000000 - (skyboxVersion * 10 + i);
                    submeshMaterials.Add(skyMaterials[i]);
                    submeshMaterialIds.Add(matId);

                    // Capture material textures on main thread and mark emissive so sky surface self-illuminates in path tracing
                    materialManager.CaptureMaterialTextures(
                        skyMaterials[i],
                        matId,
                        mpbEmissiveColor: Color.white,
                        mpbEmissiveIntensity: 1.0f,
                        mpbMainTex: skyFaceTextures[i],
                        mpbColor: Color.white
                    );
                }

                // Unique mesh key per skybox version so render thread always creates the updated mesh
                skyboxMeshKey = 0x534B594200000000UL | (ulong)(skyboxVersion & 0xFFFFFFFF);
                skyboxMeshHash = RemixMeshConverter.GenerateMeshHash("RemixSkyboxCube", skyboxMesh.vertexCount, 36, (int)skyboxMeshKey);

                var submeshIndices = new List<uint[]>(6);
                for (int s = 0; s < 6; s++)
                {
                    int baseIdx = s * 4;
                    submeshIndices.Add(new uint[]
                    {
                        (uint)(baseIdx + 0), (uint)(baseIdx + 2), (uint)(baseIdx + 1),
                        (uint)(baseIdx + 0), (uint)(baseIdx + 3), (uint)(baseIdx + 2)
                    });
                }

                var preparedData = new PreparedMeshData
                {
                    MeshKey = skyboxMeshKey,
                    MeshId = skyboxMeshId,
                    MeshName = $"RemixSkyboxCube_v{skyboxVersion}",
                    MeshHash = skyboxMeshHash,
                    Vertices = skyboxMesh.vertices,
                    Normals = skyboxMesh.normals,
                    UVs = skyboxMesh.uv,
                    Colors = null,
                    SubmeshIndices = submeshIndices,
                    SubmeshMaterials = submeshMaterials,
                    SubmeshMaterialIds = submeshMaterialIds
                };

                // Queue high-priority creation for render thread
                frameCapture?.QueuePriorityMesh(preparedData);
                logger?.LogInfo($"[RemixSkyboxManager] Queued priority skybox mesh 0x{skyboxMeshKey:X16} (v{skyboxVersion})");

                lastCapturedSkyMat = skyMat;
                lastCapturedClearFlags = clearFlags;
                lastCapturedBgColor = bgColor;
                needsCapture = false;

                if (capturedCubemap && skyMat != null)
                {
                    statusText = $"{skyMat.shader.name} (Cubemap Baked, v{skyboxVersion})";
                }
                else
                {
                    statusText = $"Solid Color ({bgColor.r:F2}, {bgColor.g:F2}, {bgColor.b:F2}, v{skyboxVersion})";
                }

                logger?.LogInfo($"[RemixSkyboxManager] Skybox captured successfully: {statusText}");
            }
            catch (Exception ex)
            {
                statusText = $"Capture Error: {ex.Message}";
                logger?.LogWarning($"[RemixSkyboxManager] Failed to capture skybox: {ex.Message}");
            }
        }

        private bool CaptureCubemapTextures(Camera mainCam, Material skyMat)
        {
            RenderTexture rt = null;
            GameObject skyCamGo = null;

            try
            {
                const int resolution = 512;
                rt = new RenderTexture(resolution, resolution, 16, RenderTextureFormat.ARGB32);
                rt.dimension = UnityEngine.Rendering.TextureDimension.Cube;
                rt.filterMode = FilterMode.Bilinear;
                rt.wrapMode = TextureWrapMode.Clamp;
                rt.Create();

                skyCamGo = new GameObject("RemixSkyboxCaptureCamera");
                skyCamGo.hideFlags = HideFlags.HideAndDontSave;
                Camera skyCam = skyCamGo.AddComponent<Camera>();
                skyCam.enabled = false;
                skyCam.cullingMask = 0; // Draw only the skybox background
                skyCam.clearFlags = CameraClearFlags.Skybox;
                skyCam.nearClipPlane = 0.1f;
                skyCam.farClipPlane = 100f;
                skyCam.transform.position = mainCam != null ? mainCam.transform.position : Vector3.zero;
                skyCam.transform.rotation = Quaternion.identity;

                if (skyMat != null)
                {
                    var skyComp = skyCamGo.AddComponent<Skybox>();
                    skyComp.material = skyMat;
                }

                bool renderOk = skyCam.RenderToCubemap(rt);
                if (!renderOk)
                {
                    logger?.LogWarning("[RemixSkyboxManager] Camera.RenderToCubemap returned false");
                    return false;
                }

                // Map cubemap faces to 6 cube submeshes:
                // 0: +Z (PositiveZ)
                // 1: -Z (NegativeZ)
                // 2: -X (NegativeX)
                // 3: +X (PositiveX)
                // 4: +Y (PositiveY)
                // 5: -Y (NegativeY)
                CubemapFace[] faceOrder = new CubemapFace[]
                {
                    CubemapFace.PositiveZ,
                    CubemapFace.NegativeZ,
                    CubemapFace.NegativeX,
                    CubemapFace.PositiveX,
                    CubemapFace.PositiveY,
                    CubemapFace.NegativeY
                };

                for (int i = 0; i < 6; i++)
                {
                    Graphics.SetRenderTarget(rt, 0, faceOrder[i]);
                    if (skyFaceTextures[i] == null || skyFaceTextures[i].width != resolution)
                    {
                        if (skyFaceTextures[i] != null) UnityEngine.Object.Destroy(skyFaceTextures[i]);
                        skyFaceTextures[i] = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                        skyFaceTextures[i].name = $"RemixSkyboxFace_{faceOrder[i]}";
                        skyFaceTextures[i].filterMode = FilterMode.Bilinear;
                        skyFaceTextures[i].wrapMode = TextureWrapMode.Clamp;
                    }

                    skyFaceTextures[i].ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);

                    // Ensure alpha is 255 for all pixels so Remix does not treat it as cutout transparency
                    Color32[] pixels = skyFaceTextures[i].GetPixels32();
                    for (int p = 0; p < pixels.Length; p++)
                    {
                        pixels[p].a = 255;
                    }
                    skyFaceTextures[i].SetPixels32(pixels);
                    skyFaceTextures[i].Apply(false, false);
                }

                Graphics.SetRenderTarget(null);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[RemixSkyboxManager] Exception baking cubemap: {ex.Message}");
                return false;
            }
            finally
            {
                Graphics.SetRenderTarget(null);
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                if (skyCamGo != null)
                {
                    UnityEngine.Object.Destroy(skyCamGo);
                }
            }
        }

        private void CaptureSolidColorTextures(Color bgColor)
        {
            Color32 c32 = new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(bgColor.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(bgColor.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(bgColor.b * 255f), 0, 255),
                255
            );

            Color32[] pixels = new Color32[16];
            for (int p = 0; p < 16; p++) pixels[p] = c32;

            for (int i = 0; i < 6; i++)
            {
                if (skyFaceTextures[i] == null || skyFaceTextures[i].width != 4)
                {
                    if (skyFaceTextures[i] != null) UnityEngine.Object.Destroy(skyFaceTextures[i]);
                    skyFaceTextures[i] = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    skyFaceTextures[i].name = $"RemixSkyboxFace_Solid_{i}";
                    skyFaceTextures[i].filterMode = FilterMode.Point;
                    skyFaceTextures[i].wrapMode = TextureWrapMode.Clamp;
                }

                skyFaceTextures[i].SetPixels32(pixels);
                skyFaceTextures[i].Apply(false, false);
            }
        }

        private void EnsureSkyboxCubeMesh()
        {
            if (skyboxMesh != null) return;

            skyboxMesh = new Mesh();
            skyboxMesh.name = "RemixSkyboxCube";

            Vector3[] vertices = new Vector3[24];
            Vector3[] normals = new Vector3[24];
            Vector2[] uvs = new Vector2[24];

            // Submesh 0: Front (+Z)
            vertices[0] = new Vector3(-0.5f, -0.5f, 0.5f);
            vertices[1] = new Vector3(0.5f, -0.5f, 0.5f);
            vertices[2] = new Vector3(0.5f, 0.5f, 0.5f);
            vertices[3] = new Vector3(-0.5f, 0.5f, 0.5f);
            normals[0] = normals[1] = normals[2] = normals[3] = new Vector3(0, 0, -1);
            uvs[0] = new Vector2(0, 0); uvs[1] = new Vector2(1, 0); uvs[2] = new Vector2(1, 1); uvs[3] = new Vector2(0, 1);

            // Submesh 1: Back (-Z)
            vertices[4] = new Vector3(0.5f, -0.5f, -0.5f);
            vertices[5] = new Vector3(-0.5f, -0.5f, -0.5f);
            vertices[6] = new Vector3(-0.5f, 0.5f, -0.5f);
            vertices[7] = new Vector3(0.5f, 0.5f, -0.5f);
            normals[4] = normals[5] = normals[6] = normals[7] = new Vector3(0, 0, 1);
            uvs[4] = new Vector2(0, 0); uvs[5] = new Vector2(1, 0); uvs[6] = new Vector2(1, 1); uvs[7] = new Vector2(0, 1);

            // Submesh 2: Left (-X)
            vertices[8] = new Vector3(-0.5f, -0.5f, -0.5f);
            vertices[9] = new Vector3(-0.5f, -0.5f, 0.5f);
            vertices[10] = new Vector3(-0.5f, 0.5f, 0.5f);
            vertices[11] = new Vector3(-0.5f, 0.5f, -0.5f);
            normals[8] = normals[9] = normals[10] = normals[11] = new Vector3(1, 0, 0);
            uvs[8] = new Vector2(0, 0); uvs[9] = new Vector2(1, 0); uvs[10] = new Vector2(1, 1); uvs[11] = new Vector2(0, 1);

            // Submesh 3: Right (+X)
            vertices[12] = new Vector3(0.5f, -0.5f, 0.5f);
            vertices[13] = new Vector3(0.5f, -0.5f, -0.5f);
            vertices[14] = new Vector3(0.5f, 0.5f, -0.5f);
            vertices[15] = new Vector3(0.5f, 0.5f, 0.5f);
            normals[12] = normals[13] = normals[14] = normals[15] = new Vector3(-1, 0, 0);
            uvs[12] = new Vector2(0, 0); uvs[13] = new Vector2(1, 0); uvs[14] = new Vector2(1, 1); uvs[15] = new Vector2(0, 1);

            // Submesh 4: Up (+Y)
            vertices[16] = new Vector3(-0.5f, 0.5f, 0.5f);
            vertices[17] = new Vector3(0.5f, 0.5f, 0.5f);
            vertices[18] = new Vector3(0.5f, 0.5f, -0.5f);
            vertices[19] = new Vector3(-0.5f, 0.5f, -0.5f);
            normals[16] = normals[17] = normals[18] = normals[19] = new Vector3(0, -1, 0);
            uvs[16] = new Vector2(0, 0); uvs[17] = new Vector2(1, 0); uvs[18] = new Vector2(1, 1); uvs[19] = new Vector2(0, 1);

            // Submesh 5: Down (-Y)
            vertices[20] = new Vector3(-0.5f, -0.5f, -0.5f);
            vertices[21] = new Vector3(0.5f, -0.5f, -0.5f);
            vertices[22] = new Vector3(0.5f, -0.5f, 0.5f);
            vertices[23] = new Vector3(-0.5f, -0.5f, 0.5f);
            normals[20] = normals[21] = normals[22] = normals[23] = new Vector3(0, 1, 0);
            uvs[20] = new Vector2(0, 0); uvs[21] = new Vector2(1, 0); uvs[22] = new Vector2(1, 1); uvs[23] = new Vector2(0, 1);

            skyboxMesh.vertices = vertices;
            skyboxMesh.normals = normals;
            skyboxMesh.uv = uvs;
            skyboxMesh.subMeshCount = 6;

            for (int f = 0; f < 6; f++)
            {
                int baseIdx = f * 4;
                int[] tris = new int[]
                {
                    baseIdx + 0, baseIdx + 2, baseIdx + 1,
                    baseIdx + 0, baseIdx + 3, baseIdx + 2
                };
                skyboxMesh.SetTriangles(tris, f);
            }

            skyboxMesh.UploadMeshData(false);
            skyboxMeshId = skyboxMesh.GetInstanceID();
        }

        public void Cleanup()
        {
            for (int i = 0; i < 6; i++)
            {
                if (skyFaceTextures[i] != null)
                {
                    UnityEngine.Object.Destroy(skyFaceTextures[i]);
                    skyFaceTextures[i] = null;
                }
                if (skyMaterials[i] != null)
                {
                    UnityEngine.Object.Destroy(skyMaterials[i]);
                    skyMaterials[i] = null;
                }
            }

            if (skyboxMesh != null)
            {
                UnityEngine.Object.Destroy(skyboxMesh);
                skyboxMesh = null;
            }
        }
    }
}
