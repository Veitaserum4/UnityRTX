using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    public enum SkyboxFiltering
    {
        Linear = 0,    // Bilinear smooth (Default)
        Closest = 1    // Point / Nearest-neighbor pixelated (Authentic PSX/ULTRAKILL retro aesthetic)
    }

    /// <summary>
    /// Autodetects standard Unity skyboxes (RenderSettings.skybox and solid camera backgrounds)
    /// and renders them via native RTX Remix Dome Light.
    /// </summary>
    public class RemixSkyboxManager
    {
        private readonly ManualLogSource logger;
        private readonly RemixMaterialManager materialManager;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixCameraHandler cameraHandler;
        private readonly ConfigEntry<bool> configEnableSkybox;
        private readonly ConfigEntry<SkyboxFiltering> configSkyboxFiltering;

        private RemixAPI.PFN_remixapi_CreateLight createLightFunc;
        private RemixAPI.PFN_remixapi_DestroyLight destroyLightFunc;
        private RemixAPI.PFN_remixapi_DrawLightInstance drawLightInstanceFunc;
        private object apiLock;
        private IntPtr currentDomeLightHandle = IntPtr.Zero;

        private RemixFrameCapture frameCapture;
        private RemixFramebufferPresenter framebufferPresenter;

        private Texture2D[] skyFaceTextures = new Texture2D[6];

        private int skyboxVersion = 0;

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
            ConfigEntry<bool> enableSkybox,
            ConfigEntry<SkyboxFiltering> skyboxFiltering = null)
        {
            this.logger = logger;
            this.materialManager = materialManager;
            this.meshConverter = meshConverter;
            this.cameraHandler = cameraHandler;
            this.configEnableSkybox = enableSkybox;
            this.configSkyboxFiltering = skyboxFiltering;
        }

        public void InitializeRemix(RemixAPI.remixapi_Interface remixInterface, object apiLock)
        {
            this.apiLock = apiLock;
            if (remixInterface.CreateLight != IntPtr.Zero)
                createLightFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateLight>(remixInterface.CreateLight);
            if (remixInterface.DestroyLight != IntPtr.Zero)
                destroyLightFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyLight>(remixInterface.DestroyLight);
            if (remixInterface.DrawLightInstance != IntPtr.Zero)
                drawLightInstanceFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawLightInstance>(remixInterface.DrawLightInstance);
        }

        public void ForceRecapture()
        {
            needsCapture = true;
        }

        public void SetFrameCapture(RemixFrameCapture capture)
        {
            this.frameCapture = capture;
        }

        public void SetFramebufferPresenter(RemixFramebufferPresenter presenter)
        {
            this.framebufferPresenter = presenter;
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

            CameraClearFlags curFlags = framebufferPresenter != null
                ? framebufferPresenter.GetOriginalClearFlags(mainCam)
                : mainCam.clearFlags;

            // If the camera is suppressed (Nothing) but a sky material exists, treat as Skybox
            if (curFlags == CameraClearFlags.Nothing && currentSkyMat != null)
            {
                curFlags = CameraClearFlags.Skybox;
            }

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
        /// Only active in legacy CubeMesh mode. In DomeLight and Atmosphere modes, no TLAS mesh is emitted,
        /// ensuring distant directional lights and sun shadow rays are never occluded.
        /// </summary>
        public void EmitSkyboxInstance(RemixFrameCapture.FrameState state, Camera mainCam)
        {
            // Dome Light is rendered natively via DrawSkyLight at optical infinity; no TLAS cube geometry needed.
        }

        /// <summary>
        /// Called per-frame on the render thread to draw the native Remix Dome Light.
        /// </summary>
        public void DrawSkyLight(int frameCount)
        {
            if (configEnableSkybox != null && !configEnableSkybox.Value)
                return;

            IntPtr handle = currentDomeLightHandle;
            if (handle != IntPtr.Zero && drawLightInstanceFunc != null)
            {
                drawLightInstanceFunc(handle);
                if (frameCount % 300 == 1)
                {
                    logger?.LogInfo($"[RemixSkyboxManager] Drawn DomeLight handle 0x{handle.ToInt64():X}");
                }
            }
        }

        private void CaptureAndPrepareSkybox(Camera mainCam, Material skyMat, CameraClearFlags clearFlags, Color bgColor)
        {
            try
            {
                skyboxVersion++;
                lastCapturedSkyMat = skyMat;
                lastCapturedClearFlags = clearFlags;
                lastCapturedBgColor = bgColor;
                needsCapture = false;

                bool captured = false;
                if (skyMat != null)
                {
                    // 1. Direct Panoramic capture (e.g. ULTRAKILL LustSky, Greed, etc. using Skybox/Panoramic)
                    if (IsPanoramicMaterial(skyMat))
                    {
                        captured = CapturePanoramicTexture(mainCam, skyMat);
                        if (captured)
                            return;
                    }

                    // 2. Direct 6-Sided capture
                    if (!captured && Is6SidedMaterial(skyMat))
                    {
                        captured = Capture6SidedTextures(skyMat);
                    }

                    // 3. Direct Cubemap texture extraction
                    if (!captured && skyMat.HasProperty("_Tex") && skyMat.GetTexture("_Tex") is Cubemap cubemap)
                    {
                        captured = ExtractFromDirectCubemap(cubemap);
                    }

                    // 4. Fallback to Camera.RenderToCubemap
                    if (!captured)
                    {
                        captured = CaptureCubemapTextures(mainCam, skyMat);
                    }
                }

                PrepareDomeLight(captured, bgColor);
            }
            catch (Exception ex)
            {
                statusText = $"Capture Error: {ex.Message}";
                logger?.LogWarning($"[RemixSkyboxManager] Failed to capture skybox: {ex.Message}");
            }
        }

        private static bool IsPanoramicMaterial(Material skyMat)
        {
            if (skyMat == null) return false;
            if (skyMat.shader != null && skyMat.shader.name.IndexOf("Panoramic", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (skyMat.HasProperty("_MainTex") && skyMat.mainTexture != null && !skyMat.HasProperty("_FrontTex") && !skyMat.HasProperty("_Tex"))
                return true;
            return false;
        }

        private static bool Is6SidedMaterial(Material skyMat)
        {
            if (skyMat == null) return false;
            return skyMat.HasProperty("_FrontTex") && skyMat.HasProperty("_BackTex");
        }

        private bool CapturePanoramicTexture(Camera mainCam, Material skyMat)
        {
            try
            {
                Texture srcTex = skyMat.mainTexture ?? (skyMat.HasProperty("_MainTex") ? skyMat.GetTexture("_MainTex") : null);
                if (srcTex == null) return false;

                int srcW = srcTex.width;
                int srcH = srcTex.height;
                if (srcW <= 0 || srcH <= 0) return false;

                logger?.LogInfo($"[RemixSkyboxManager] Capturing direct Panoramic skybox: '{skyMat.name}', texture='{srcTex.name}' ({srcW}x{srcH})");

                RenderTexture tempRt = RenderTexture.GetTemporary(srcW, srcH, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(srcTex, tempRt);

                RenderTexture prevActive = RenderTexture.active;
                RenderTexture.active = tempRt;

                Texture2D readTex = new Texture2D(srcW, srcH, TextureFormat.RGBA32, false);
                readTex.ReadPixels(new Rect(0, 0, srcW, srcH), 0, 0);
                readTex.Apply(false, false);

                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tempRt);

                Color32[] srcPixels = readTex.GetPixels32();
                UnityEngine.Object.Destroy(readTex);

                Color tint = skyMat.HasProperty("_Tint") ? skyMat.GetColor("_Tint") : Color.white;
                float exposure = skyMat.HasProperty("_Exposure") ? skyMat.GetFloat("_Exposure") : 1.0f;

                PrepareDomeLightFromEquirectangular(srcPixels, srcW, srcH, tint, exposure);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[RemixSkyboxManager] Direct panoramic capture failed: {ex.Message}");
                return false;
            }
        }

        private void PrepareDomeLightFromEquirectangular(Color32[] srcPixels, int srcW, int srcH, Color tint, float exposure)
        {
            string modsDir = Path.Combine(Environment.CurrentDirectory, "rtx-remix", "mods");
            if (!Directory.Exists(modsDir))
            {
                Directory.CreateDirectory(modsDir);
            }

            string ddsFileName = $"RemixSky_v{skyboxVersion}.dds";
            string ddsFullPath = Path.GetFullPath(Path.Combine(modsDir, ddsFileName));

            bool isClosest = (configSkyboxFiltering != null && configSkyboxFiltering.Value == SkyboxFiltering.Closest);

            // In Closest (pixelated) mode, we use a 2048x1024 high-res DDS buffer with nearest-neighbor block replication.
            // Because each retro pixel becomes a solid block of identical pixels in the DDS, Remix's LinearWrapSampler
            // evaluates identical values across the block, producing crisp, razor-sharp retro pixel edges!
            int dstW = isClosest ? Math.Max(srcW * 2, 2048) : srcW;
            int dstH = isClosest ? Math.Max(srcH * 2, 1024) : srcH;
            Color32[] ddsPixels = new Color32[dstW * dstH];

            System.Threading.Tasks.Parallel.For(0, dstH, yd =>
            {
                float vDds = (yd + 0.5f) / dstH;
                // yd = 0 is Zenith (top of DDS), corresponding to top of Unity texture (ys = srcH - 1)
                // yd = dstH - 1 is Nadir (bottom of DDS), corresponding to bottom of Unity texture (ys = 0)
                int rowOffset = yd * dstW;

                for (int xd = 0; xd < dstW; xd++)
                {
                    float uDds = (xd + 0.5f) / dstW;

                    Color32 c;
                    if (isClosest)
                    {
                        int sx = Mathf.Clamp((int)(uDds * srcW), 0, srcW - 1);
                        int sy = Mathf.Clamp((int)((1.0f - vDds) * srcH), 0, srcH - 1);
                        c = srcPixels[sy * srcW + sx];
                    }
                    else
                    {
                        float srcXf = uDds * srcW - 0.5f;
                        float srcYf = (1.0f - vDds) * srcH - 0.5f;
                        c = SampleBilinear(srcPixels, srcW, srcH, srcXf, srcYf);
                    }

                    byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * tint.r * exposure), 0, 255);
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * tint.g * exposure), 0, 255);
                    byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * tint.b * exposure), 0, 255);
                    ddsPixels[rowOffset + xd] = new Color32(r, g, b, 255);
                }
            });

            WriteDdsFile(ddsFullPath, dstW, dstH, ddsPixels);
            CleanupOldDdsFiles(modsDir, ddsFileName);

            string filterDesc = isClosest ? "Closest (Pixelated)" : "Linear (Smooth)";
            statusText = $"Dome Light (Panoramic v{skyboxVersion}, {filterDesc})";
            logger?.LogInfo($"[RemixSkyboxManager] Generated direct panoramic DDS: {ddsFullPath} ({filterDesc})");

            CreateRemixDomeLight(ddsFullPath);
        }

        private static Color32 SampleBilinear(Color32[] pixels, int w, int h, float x, float y)
        {
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, h - 1);
            int x1 = (x0 + 1) % w;
            if (x1 < 0) x1 += w;
            int x0_clamped = (x0 % w + w) % w;
            int y1 = Mathf.Clamp(y0 + 1, 0, h - 1);

            float fx = x - Mathf.Floor(x);
            float fy = Mathf.Clamp01(y - y0);

            Color32 c00 = pixels[y0 * w + x0_clamped];
            Color32 c10 = pixels[y0 * w + x1];
            Color32 c01 = pixels[y1 * w + x0_clamped];
            Color32 c11 = pixels[y1 * w + x1];

            float r = Mathf.Lerp(Mathf.Lerp(c00.r, c10.r, fx), Mathf.Lerp(c01.r, c11.r, fx), fy);
            float g = Mathf.Lerp(Mathf.Lerp(c00.g, c10.g, fx), Mathf.Lerp(c01.g, c11.g, fx), fy);
            float b = Mathf.Lerp(Mathf.Lerp(c00.b, c10.b, fx), Mathf.Lerp(c01.b, c11.b, fx), fy);

            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b), 0, 255),
                255
            );
        }

        private bool Capture6SidedTextures(Material skyMat)
        {
            try
            {
                string[] props = new string[] { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };
                CubemapFace[] faceOrder = new CubemapFace[]
                {
                    CubemapFace.PositiveZ,
                    CubemapFace.NegativeZ,
                    CubemapFace.NegativeX,
                    CubemapFace.PositiveX,
                    CubemapFace.PositiveY,
                    CubemapFace.NegativeY
                };

                Texture frontTex = skyMat.GetTexture("_FrontTex");
                if (frontTex == null) return false;

                int res = frontTex.width;
                if (res <= 0) return false;

                Color tint = skyMat.HasProperty("_Tint") ? skyMat.GetColor("_Tint") : Color.white;
                float exposure = skyMat.HasProperty("_Exposure") ? skyMat.GetFloat("_Exposure") : 1.0f;

                bool isClosest = (configSkyboxFiltering != null && configSkyboxFiltering.Value == SkyboxFiltering.Closest);

                for (int i = 0; i < 6; i++)
                {
                    Texture faceTex = skyMat.GetTexture(props[i]);
                    if (faceTex == null) faceTex = frontTex;

                    int fw = faceTex.width;
                    int fh = faceTex.height;

                    RenderTexture tempRt = RenderTexture.GetTemporary(fw, fh, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(faceTex, tempRt);

                    RenderTexture prevActive = RenderTexture.active;
                    RenderTexture.active = tempRt;

                    if (skyFaceTextures[i] == null || skyFaceTextures[i].width != fw || skyFaceTextures[i].height != fh)
                    {
                        if (skyFaceTextures[i] != null) UnityEngine.Object.Destroy(skyFaceTextures[i]);
                        skyFaceTextures[i] = new Texture2D(fw, fh, TextureFormat.RGBA32, false);
                        skyFaceTextures[i].name = $"RemixSkyboxFace_{faceOrder[i]}";
                    }
                    skyFaceTextures[i].filterMode = isClosest ? FilterMode.Point : FilterMode.Bilinear;
                    skyFaceTextures[i].wrapMode = TextureWrapMode.Clamp;

                    skyFaceTextures[i].ReadPixels(new Rect(0, 0, fw, fh), 0, 0);
                    skyFaceTextures[i].Apply(false, false);

                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(tempRt);

                    if (tint != Color.white || exposure != 1.0f)
                    {
                        Color32[] pixels = skyFaceTextures[i].GetPixels32();
                        for (int p = 0; p < pixels.Length; p++)
                        {
                            pixels[p].r = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[p].r * tint.r * exposure), 0, 255);
                            pixels[p].g = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[p].g * tint.g * exposure), 0, 255);
                            pixels[p].b = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[p].b * tint.b * exposure), 0, 255);
                            pixels[p].a = 255;
                        }
                        skyFaceTextures[i].SetPixels32(pixels);
                        skyFaceTextures[i].Apply(false, false);
                    }
                }

                logger?.LogInfo($"[RemixSkyboxManager] Captured 6-sided skybox: '{skyMat.name}' ({res}x{res})");
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[RemixSkyboxManager] 6-sided capture failed: {ex.Message}");
                return false;
            }
        }

        private void PrepareDomeLight(bool hasCubemap, Color bgColor)
        {
            string modsDir = Path.Combine(Environment.CurrentDirectory, "rtx-remix", "mods");
            if (!Directory.Exists(modsDir))
            {
                Directory.CreateDirectory(modsDir);
            }

            string ddsFileName = $"RemixSky_v{skyboxVersion}.dds";
            string ddsFullPath = Path.GetFullPath(Path.Combine(modsDir, ddsFileName));

            bool isClosest = (configSkyboxFiltering != null && configSkyboxFiltering.Value == SkyboxFiltering.Closest);
            string filterDesc = isClosest ? "Closest (Pixelated)" : "Linear (Smooth)";

            if (hasCubemap)
            {
                GenerateEquirectangularDds(ddsFullPath, 2048, 1024);
                statusText = $"Dome Light (Cubemap v{skyboxVersion}, {filterDesc})";
                logger?.LogInfo($"[RemixSkyboxManager] Generated equirectangular panorama DDS: {ddsFullPath} ({filterDesc})");
            }
            else
            {
                GenerateSolidColorDds(ddsFullPath, 16, 16, bgColor);
                statusText = $"Dome Light (Solid v{skyboxVersion})";
                logger?.LogInfo($"[RemixSkyboxManager] Generated solid color DDS ({bgColor}): {ddsFullPath}");
            }

            CleanupOldDdsFiles(modsDir, ddsFileName);
            CreateRemixDomeLight(ddsFullPath);
        }

        private void CreateRemixDomeLight(string ddsFullPath)
        {
            if (createLightFunc == null)
            {
                logger?.LogWarning("[RemixSkyboxManager] createLightFunc is null, cannot create Dome Light");
                return;
            }

            DestroyCurrentDomeLight();

            float rot = 0f;
            if (lastCapturedSkyMat != null && lastCapturedSkyMat.HasProperty("_Rotation"))
            {
                try { rot = lastCapturedSkyMat.GetFloat("_Rotation"); } catch { }
            }

            float rad = -rot * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rad);
            float sinR = Mathf.Sin(rad);

            var xform = RemixAPI.remixapi_Transform.FromMatrix(
                cosR, -sinR, 0, 0,
                sinR, cosR, 0, 0,
                0, 0, 1, 0
            );

            IntPtr pathPtr = Marshal.StringToHGlobalUni(ddsFullPath);

            var domeExt = new RemixAPI.remixapi_LightInfoDomeEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_DOME_EXT,
                pNext = IntPtr.Zero,
                transform = xform,
                colorTexture = pathPtr
            };

            GCHandle domeHandle = GCHandle.Alloc(domeExt, GCHandleType.Pinned);

            try
            {
                var lightInfo = new RemixAPI.remixapi_LightInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO,
                    pNext = domeHandle.AddrOfPinnedObject(),
                    hash = 0x534B59444F4D4500UL | (ulong)(uint)(skyboxVersion & 0xFF),
                    radiance = new RemixAPI.remixapi_Float3D(1.0f, 1.0f, 1.0f),
                    isDynamic = 1,
                    ignoreViewModel = 1,
                    ignoreFirstPersonPlayerShadow = 1
                };

                IntPtr newHandle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock ?? this)
                {
                    result = createLightFunc(ref lightInfo, out newHandle);
                }

                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    currentDomeLightHandle = newHandle;
                    logger?.LogInfo($"[RemixSkyboxManager] Successfully created Remix Dome Light handle 0x{newHandle.ToInt64():X}");
                }
                else
                {
                    logger?.LogWarning($"[RemixSkyboxManager] Failed to create Dome Light: {result}");
                }
            }
            finally
            {
                domeHandle.Free();
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        private void DestroyCurrentDomeLight()
        {
            if (currentDomeLightHandle != IntPtr.Zero && destroyLightFunc != null)
            {
                lock (apiLock ?? this)
                {
                    destroyLightFunc(currentDomeLightHandle);
                }
                currentDomeLightHandle = IntPtr.Zero;
            }
        }

        private void GenerateEquirectangularDds(string ddsPath, int width, int height)
        {
            Color32[][] facePixels = new Color32[6][];
            int faceRes = 0;
            for (int i = 0; i < 6; i++)
            {
                if (skyFaceTextures[i] != null)
                {
                    facePixels[i] = skyFaceTextures[i].GetPixels32();
                    faceRes = skyFaceTextures[i].width;
                }
            }

            if (faceRes == 0 || facePixels[0] == null)
            {
                logger?.LogWarning("[RemixSkyboxManager] Cannot generate equirectangular: face textures missing");
                return;
            }

            bool isClosest = (configSkyboxFiltering != null && configSkyboxFiltering.Value == SkyboxFiltering.Closest);
            Color32[] panoPixels = new Color32[width * height];

            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                float vLatLong = (y + 0.5f) / height;
                float theta = vLatLong * Mathf.PI; // 0 (zenith) to PI (nadir)
                float sinTheta = Mathf.Sin(theta);
                float rz = Mathf.Cos(theta); // Remix Z (Up)

                int rowOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    float uLatLong = (x + 0.5f) / width;
                    float phi = (uLatLong - 0.5f) * 2f * Mathf.PI; // -PI to +PI

                    float rx = sinTheta * Mathf.Sin(phi); // Remix X (Right)
                    float ry = sinTheta * Mathf.Cos(phi); // Remix Y (Forward)

                    // Convert Remix Z-up to Unity Y-up:
                    // Unity X = rx, Unity Y = rz, Unity Z = ry
                    float dx = rx;
                    float dy = rz;
                    float dz = ry;

                    float absX = Mathf.Abs(dx);
                    float absY = Mathf.Abs(dy);
                    float absZ = Mathf.Abs(dz);

                    int faceIndex;
                    float sc, tc, ma;

                    if (absX >= absY && absX >= absZ)
                    {
                        if (dx > 0) { faceIndex = 3; sc = -dz; tc = dy; ma = absX; } // PositiveX
                        else        { faceIndex = 2; sc = dz;  tc = dy; ma = absX; } // NegativeX
                    }
                    else if (absY >= absX && absY >= absZ)
                    {
                        if (dy > 0) { faceIndex = 4; sc = dx; tc = -dz; ma = absY; } // PositiveY
                        else        { faceIndex = 5; sc = dx; tc = dz;  ma = absY; } // NegativeY
                    }
                    else
                    {
                        if (dz > 0) { faceIndex = 0; sc = dx;  tc = dy; ma = absZ; } // PositiveZ
                        else        { faceIndex = 1; sc = -dx; tc = dy; ma = absZ; } // NegativeZ
                    }

                    float uFace = (sc / ma + 1f) * 0.5f;
                    float vFace = (tc / ma + 1f) * 0.5f;

                    Color32[] face = facePixels[faceIndex] ?? facePixels[0];
                    if (isClosest)
                    {
                        int px = Mathf.Clamp((int)(uFace * faceRes), 0, faceRes - 1);
                        int py = Mathf.Clamp((int)(vFace * faceRes), 0, faceRes - 1);
                        panoPixels[rowOffset + x] = face[py * faceRes + px];
                    }
                    else
                    {
                        float fx = uFace * faceRes - 0.5f;
                        float fy = vFace * faceRes - 0.5f;
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, faceRes - 1);
                        int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, faceRes - 1);
                        int x1 = Mathf.Clamp(x0 + 1, 0, faceRes - 1);
                        int y1 = Mathf.Clamp(y0 + 1, 0, faceRes - 1);
                        float s = Mathf.Clamp01(fx - x0);
                        float t = Mathf.Clamp01(fy - y0);

                        Color32 c00 = face[y0 * faceRes + x0];
                        Color32 c10 = face[y0 * faceRes + x1];
                        Color32 c01 = face[y1 * faceRes + x0];
                        Color32 c11 = face[y1 * faceRes + x1];

                        byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(c00.r, c10.r, s), Mathf.Lerp(c01.r, c11.r, s), t)), 0, 255);
                        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(c00.g, c10.g, s), Mathf.Lerp(c01.g, c10.g, s), t)), 0, 255);
                        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(c00.b, c10.b, s), Mathf.Lerp(c01.b, c11.b, s), t)), 0, 255);
                        panoPixels[rowOffset + x] = new Color32(r, g, b, 255);
                    }
                }
            });

            WriteDdsFile(ddsPath, width, height, panoPixels);
        }

        private void GenerateSolidColorDds(string ddsPath, int width, int height, Color color)
        {
            Color32 c32 = color;
            c32.a = 255;
            Color32[] solidPixels = new Color32[width * height];
            for (int i = 0; i < solidPixels.Length; i++)
            {
                solidPixels[i] = c32;
            }
            WriteDdsFile(ddsPath, width, height, solidPixels);
        }

        private static void WriteDdsFile(string filePath, int width, int height, Color32[] pixels)
        {
            byte[] dds = new byte[128 + width * height * 4];

            // Magic "DDS "
            dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';

            // DDS_HEADER (124 bytes)
            WriteInt32(dds, 4, 124);
            WriteInt32(dds, 8, 0x00021007); // DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PITCH | DDSD_PIXELFORMAT
            WriteInt32(dds, 12, height);
            WriteInt32(dds, 16, width);
            WriteInt32(dds, 20, width * 4);
            WriteInt32(dds, 24, 0); // depth
            WriteInt32(dds, 28, 1); // mipMapCount

            // DDS_PIXELFORMAT at offset 76 (32 bytes)
            WriteInt32(dds, 76, 32);       // dwSize
            WriteInt32(dds, 80, 0x41);     // DDPF_RGB | DDPF_ALPHAPIXELS
            WriteInt32(dds, 84, 0);        // dwFourCC
            WriteInt32(dds, 88, 32);       // dwRGBBitCount
            WriteUInt32(dds, 92, 0x000000FF);  // dwRBitMask
            WriteUInt32(dds, 96, 0x0000FF00);  // dwGBitMask
            WriteUInt32(dds, 100, 0x00FF0000); // dwBBitMask
            WriteUInt32(dds, 104, 0xFF000000); // dwABitMask

            // dwCaps at offset 108
            WriteInt32(dds, 108, 0x1000); // DDSCAPS_TEXTURE

            // Pixel data starting at offset 128
            int pixelOffset = 128;
            for (int i = 0; i < pixels.Length; i++)
            {
                dds[pixelOffset++] = pixels[i].r;
                dds[pixelOffset++] = pixels[i].g;
                dds[pixelOffset++] = pixels[i].b;
                dds[pixelOffset++] = pixels[i].a;
            }

            File.WriteAllBytes(filePath, dds);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private void CleanupOldDdsFiles(string modsDir, string currentFile)
        {
            try
            {
                var files = Directory.GetFiles(modsDir, "RemixSky_v*.dds");
                foreach (var file in files)
                {
                    if (!file.EndsWith(currentFile, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }



        private bool CaptureCubemapTextures(Camera mainCam, Material skyMat)
        {
            RenderTexture rt = null;
            RenderTexture temp2D = null;
            GameObject skyCamGo = null;

            try
            {
                string mainTexInfo = "none";
                if (skyMat.mainTexture != null)
                {
                    var t = skyMat.mainTexture;
                    mainTexInfo = $"{t.name} ({t.width}x{t.height}, {t.GetType().Name})";
                }
                logger?.LogInfo($"[RemixSkyboxManager] Capturing skyMat: '{skyMat.name}', shader='{skyMat.shader?.name}', mainTexture={mainTexInfo}");

                // If material directly has a readable Cubemap texture, extract faces directly
                if (skyMat.HasProperty("_Tex"))
                {
                    var cubemapTex = skyMat.GetTexture("_Tex") as Cubemap;
                    if (cubemapTex != null)
                    {
                        logger?.LogInfo($"[RemixSkyboxManager] Found direct Cubemap '{cubemapTex.name}' ({cubemapTex.width}x{cubemapTex.height}) - extracting faces directly.");
                        if (ExtractFromDirectCubemap(cubemapTex))
                            return true;
                    }
                }

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
                // Elevate the capture camera high into the sky (y=50000) away from all level geometry,
                // ProBuilder meshes, and room colliders. Use layer 31 (SpecialLighting) so Unity's
                // camera pipeline triggers without drawing any world objects.
                skyCam.cullingMask = (1 << 31);
                skyCam.clearFlags = CameraClearFlags.Skybox;
                skyCam.nearClipPlane = 0.1f;
                skyCam.farClipPlane = 1000f;
                skyCam.backgroundColor = mainCam != null ? mainCam.backgroundColor : Color.black;
                skyCam.transform.position = new Vector3(0f, 50000f, 0f);
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

                temp2D = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32);

                for (int i = 0; i < 6; i++)
                {
                    if (skyFaceTextures[i] == null || skyFaceTextures[i].width != resolution)
                    {
                        if (skyFaceTextures[i] != null) UnityEngine.Object.Destroy(skyFaceTextures[i]);
                        skyFaceTextures[i] = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                        skyFaceTextures[i].name = $"RemixSkyboxFace_{faceOrder[i]}";
                        skyFaceTextures[i].filterMode = FilterMode.Bilinear;
                        skyFaceTextures[i].wrapMode = TextureWrapMode.Clamp;
                    }

                    bool copiedViaGpu = false;
                    try
                    {
                        Graphics.CopyTexture(rt, (int)faceOrder[i], 0, temp2D, 0, 0);
                        Graphics.SetRenderTarget(temp2D);
                        skyFaceTextures[i].ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                        copiedViaGpu = true;
                    }
                    catch
                    {
                        Graphics.SetRenderTarget(rt, 0, faceOrder[i]);
                        skyFaceTextures[i].ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                    }

                    // Ensure alpha is 255 and gather stats
                    Color32[] pixels = skyFaceTextures[i].GetPixels32();
                    byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
                    long sumR = 0, sumG = 0, sumB = 0;
                    for (int p = 0; p < pixels.Length; p++)
                    {
                        pixels[p].a = 255;
                        byte r = pixels[p].r, g = pixels[p].g, b = pixels[p].b;
                        if (r < minR) minR = r; if (r > maxR) maxR = r;
                        if (g < minG) minG = g; if (g > maxG) maxG = g;
                        if (b < minB) minB = b; if (b > maxB) maxB = b;
                        sumR += r; sumG += g; sumB += b;
                    }
                    skyFaceTextures[i].SetPixels32(pixels);
                    skyFaceTextures[i].Apply(false, false);

                    float avgR = (float)sumR / pixels.Length;
                    float avgG = (float)sumG / pixels.Length;
                    float avgB = (float)sumB / pixels.Length;
                    logger?.LogInfo($"[RemixSkyboxManager] Face {i} ({faceOrder[i]}): minRGB=({minR},{minG},{minB}), maxRGB=({maxR},{maxG},{maxB}), avgRGB=({avgR:F1},{avgG:F1},{avgB:F1}), gpuCopy={copiedViaGpu}");
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
                if (temp2D != null)
                {
                    RenderTexture.ReleaseTemporary(temp2D);
                }
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

        private bool ExtractFromDirectCubemap(Cubemap cubemap)
        {
            try
            {
                int res = cubemap.width;
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
                    if (skyFaceTextures[i] == null || skyFaceTextures[i].width != res)
                    {
                        if (skyFaceTextures[i] != null) UnityEngine.Object.Destroy(skyFaceTextures[i]);
                        skyFaceTextures[i] = new Texture2D(res, res, TextureFormat.RGBA32, false);
                        skyFaceTextures[i].name = $"RemixSkyboxFace_{faceOrder[i]}";
                        skyFaceTextures[i].filterMode = FilterMode.Bilinear;
                        skyFaceTextures[i].wrapMode = TextureWrapMode.Clamp;
                    }

                    Color[] facePixels = cubemap.GetPixels(faceOrder[i]);
                    Color32[] c32 = new Color32[facePixels.Length];
                    for (int p = 0; p < facePixels.Length; p++)
                    {
                        c32[p] = (Color32)facePixels[p];
                        c32[p].a = 255;
                    }
                    skyFaceTextures[i].SetPixels32(c32);
                    skyFaceTextures[i].Apply(false, false);
                }
                logger?.LogInfo($"[RemixSkyboxManager] Extracted 6 faces directly from Cubemap '{cubemap.name}' ({res}x{res})");
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[RemixSkyboxManager] Direct cubemap extraction failed (unreadable?): {ex.Message}");
                return false;
            }
        }

        public void Cleanup()
        {
            DestroyCurrentDomeLight();

            for (int i = 0; i < 6; i++)
            {
                if (skyFaceTextures[i] != null)
                {
                    UnityEngine.Object.Destroy(skyFaceTextures[i]);
                    skyFaceTextures[i] = null;
                }
            }
        }
    }
}
