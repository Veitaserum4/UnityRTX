using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Always-visible debug overlay drawn via the Remix ImGui overlay callback.
    /// Displays mesh/texture/material diagnostics, origins, errors, and 3D wireframe
    /// bounding boxes around each rendered mesh with floating labels.
    /// Toggle with F3.
    /// </summary>
    public class RemixDebugHUD
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_F3 = 0x72;

        private readonly ManualLogSource _log;
        private readonly UnityRemixPlugin _plugin;
        private bool _visible = false;
        private bool _f3WasDown;

        private HUDSnapshot _snapshot;

        private const int MaxLabelCount = 80;
        private float _boxMaxDistance = 50f;

        #region Snapshot structures

        private class HUDSnapshot
        {
            public int StaticMeshCache;
            public int SkinnedMeshHandles;
            public int TextureCache;
            public int MaterialData;
            public int FailedMeshes;
            public int PendingMeshQueue;
            public int PersistentStatic;
            public int CachedStaticRenderers;
            public int CachedSkinnedRenderers;
            public int ScannedInstances;
            public int ScannerStreamQueue;
            public int TerrainChunks;
            public StaticGeometryStats StaticGeometryStats;
            public int LightCount;
            public string CameraName;
            public string SceneName;
            public string[] PlaceholderMaterials;

            // Camera matrices for 3D→2D projection (captured on main thread)
            public Matrix4x4 ViewMatrix;
            public Matrix4x4 ProjMatrix;
            public Vector3 CameraPosition;
            public bool HasCameraMatrices;

            // Per-mesh debug entries
            public DebugBoxEntry[] MeshEntries;
        }

        private struct DebugBoxEntry
        {
            public string Name;
            public string MeshName;
            public int LayerIndex;
            public int RendererInstanceId;
            public string LayerName;
            public string Origin;
            public string MaterialName;
            public ulong TextureHash;
            public bool Failed;
            public bool NoTexture;
            // World-space AABB
            public Vector3 BoundsCenter;
            public Vector3 BoundsExtents;
            // Pre-computed distance to camera for sorting
            public float SqrDistToCamera;
        }

        #endregion

        public RemixDebugHUD(ManualLogSource log, UnityRemixPlugin plugin)
        {
            _log = log;
            _plugin = plugin;
            _snapshot = new HUDSnapshot
            {
                PlaceholderMaterials = Array.Empty<string>(),
                MeshEntries = Array.Empty<DebugBoxEntry>()
            };
        }

        /// <summary>
        /// Called from the Unity main thread each frame to snapshot diagnostic data.
        /// </summary>
        public void UpdateSnapshot()
        {
            var snap = new HUDSnapshot();

            var mc = _plugin.MeshConverter;
            var mm = _plugin.MaterialManager;
            var fc = _plugin.FrameCapture;
            var sc = _plugin.SceneMeshScanner;
            var ch = _plugin.CameraHandler;
            var lc = _plugin.LightConverter;

            if (mc != null)
            {
                snap.StaticMeshCache = mc.MeshCacheCount;
                snap.SkinnedMeshHandles = mc.SkinnedMeshHandleCount;
            }

            if (mm != null)
            {
                snap.TextureCache = mm.TextureCacheCount;
                snap.MaterialData = mm.MaterialDataCount;
                snap.PlaceholderMaterials = mm.GetPlaceholderMaterialNames();
            }
            else
            {
                snap.PlaceholderMaterials = Array.Empty<string>();
            }

            if (fc != null)
            {
                snap.FailedMeshes = fc.FailedMeshCount;
                snap.PendingMeshQueue = fc.PendingMeshQueueCount;
                snap.PersistentStatic = fc.PersistentStaticCount;
                snap.CachedStaticRenderers = fc.CachedStaticRendererCount;
                snap.CachedSkinnedRenderers = fc.CachedSkinnedRendererCount;
                snap.StaticGeometryStats = fc.GetStaticGeometryStats(sc);
            }

            if (sc != null)
            {
                snap.ScannedInstances = sc.TotalInstanceCount;
                snap.ScannerStreamQueue = sc.StreamingQueueCount;
            }

            var tm = _plugin.TerrainManager;
            if (tm != null)
            {
                snap.TerrainChunks = tm.ChunkCount;
            }

            if (ch != null)
                snap.CameraName = ch.ActiveCameraName ?? "none";

            if (lc != null)
                snap.LightCount = lc.CachedLightCount;

            try
            {
                snap.SceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }
            catch
            {
                snap.SceneName = "unknown";
            }

            // Camera matrices for 3D projection (only when visible)
            if (_visible && ch != null && ch.CurrentCamera != null)
            {
                var cam = ch.CurrentCamera;
                snap.ViewMatrix = cam.worldToCameraMatrix;
                snap.ProjMatrix = cam.projectionMatrix;
                snap.CameraPosition = cam.transform.position;
                snap.HasCameraMatrices = true;
            }

            // Per-mesh debug entries (only when visible + DrawList available)
            if (_visible && RemixImGui.HasDrawListSupport)
            {
                var allEntries = new List<DebugBoxEntry>();

                // Runtime + skinned + persistent from frame capture
                if (fc != null)
                {
                    foreach (var src in fc.CollectDebugMeshEntries())
                        allEntries.Add(ConvertEntry(src, snap.CameraPosition));
                }

                // Scanner entries
                if (sc != null)
                {
                    foreach (var src in sc.CollectDebugEntries())
                        allEntries.Add(ConvertEntry(src, snap.CameraPosition));
                }

                snap.MeshEntries = allEntries.ToArray();
            }
            else
            {
                snap.MeshEntries = Array.Empty<DebugBoxEntry>();
            }

            Volatile.Write(ref _snapshot, snap);
        }

        private static DebugBoxEntry ConvertEntry(RemixFrameCapture.DebugMeshEntry src, Vector3 camPos)
        {
            return new DebugBoxEntry
            {
                Name = src.Name,
                MeshName = src.MeshName,
                LayerIndex = src.LayerIndex,
                RendererInstanceId = src.RendererInstanceId,
                LayerName = src.LayerName,
                Origin = src.Origin,
                MaterialName = src.MaterialName,
                TextureHash = src.TextureHash,
                Failed = src.Failed,
                NoTexture = src.NoTexture,
                BoundsCenter = src.BoundsCenter,
                BoundsExtents = src.BoundsExtents,
                SqrDistToCamera = (src.BoundsCenter - camPos).sqrMagnitude,
            };
        }

        #region Draw (render thread)

        /// <summary>
        /// ImGui draw callback — invoked on the render thread every frame.
        /// </summary>
        public void Draw(IntPtr userData)
        {
            bool f3Down = (GetAsyncKeyState(VK_F3) & 0x8000) != 0;
            if (f3Down && !_f3WasDown)
                _visible = !_visible;
            _f3WasDown = f3Down;

            if (!_visible)
                return;

            var snap = Volatile.Read(ref _snapshot);

            // 2D HUD panel
            DrawHUDPanel(snap);

            // 3D wireframe boxes + labels
            if (RemixImGui.HasDrawListSupport && snap.HasCameraMatrices && snap.MeshEntries != null)
                Draw3DBoxes(snap);
        }

        private void DrawHUDPanel(HUDSnapshot snap)
        {
            int flags = RemixImGui.WindowFlags_NoFocusOnAppearing
                      | RemixImGui.WindowFlags_NoBringToFrontOnFocus
                      | RemixImGui.WindowFlags_NoSavedSettings
                      | RemixImGui.WindowFlags_NoMove
                      | RemixImGui.WindowFlags_NoResize;

            RemixImGui.SetNextWindowPos(10, 10, RemixImGui.Cond_Always);
            RemixImGui.SetNextWindowSize(420, 0, RemixImGui.Cond_Always);
            RemixImGui.PushStyleColor(RemixImGui.Col_WindowBg, 0.05f, 0.05f, 0.05f, 0.75f);

            if (RemixImGui.Begin("Debug", flags))
            {
                DrawSummary(snap);
                RemixImGui.Separator();
                DrawMeshDetails(snap);
                RemixImGui.Separator();
                DrawErrors(snap);
            }
            RemixImGui.End();
            RemixImGui.PopStyleColor(1);
        }

        private void DrawSummary(HUDSnapshot snap)
        {
            RemixImGui.Text($"Scene: {snap.SceneName}");
            RemixImGui.Text($"Camera: {snap.CameraName}");
            RemixImGui.Spacing();
            RemixImGui.Text($"Static Meshes (cached):  {snap.StaticMeshCache}");
            RemixImGui.Text($"Skinned Meshes (active): {snap.SkinnedMeshHandles}");
            RemixImGui.Text($"Textures:  {snap.TextureCache}");
            RemixImGui.Text($"Materials: {snap.MaterialData}");
            RemixImGui.Text($"Lights:    {snap.LightCount}");
            RemixImGui.Text($"Unique Static Objects: {snap.StaticGeometryStats.DedupedVisibleStaticTotal}");

            if (snap.MeshEntries != null && snap.MeshEntries.Length > 0)
            {
                RemixImGui.Spacing();
                RemixImGui.SliderFloat("Box Distance", ref _boxMaxDistance, 1f, 500f, "%.0f m");
                RemixImGui.TextColored(0.5f, 0.8f, 0.5f, 1.0f,
                    $"3D boxes: {snap.MeshEntries.Length} meshes (max {MaxLabelCount} labels)");
            }
        }

        private void DrawMeshDetails(HUDSnapshot snap)
        {
            if (!RemixImGui.CollapsingHeader("Mesh Pipeline"))
                return;

            RemixImGui.Indent();

            RemixImGui.TextColored(0.6f, 1.0f, 0.6f, 1.0f, "Runtime Capture");
            RemixImGui.Text($"  Static renderers:  {snap.CachedStaticRenderers}");
            RemixImGui.Text($"  Skinned renderers: {snap.CachedSkinnedRenderers}");
            RemixImGui.Text($"  Persistent static: {snap.PersistentStatic}");
            RemixImGui.Text($"  Pending creation:  {snap.PendingMeshQueue}");
            RemixImGui.Text($"  Raw renderers:     {snap.StaticGeometryStats.RawStaticRenderers}");
            RemixImGui.Text($"  Unique renderers:  {snap.StaticGeometryStats.DedupedStaticRenderers} (-{snap.StaticGeometryStats.SuppressedStaticRenderers})");
            RemixImGui.Text($"  Raw static meshes: {snap.StaticGeometryStats.RawStaticMeshes}");
            RemixImGui.Text($"  Visible meshes:    {snap.StaticGeometryStats.DedupedStaticMeshes} (-{snap.StaticGeometryStats.SuppressedStaticMeshes})");

            RemixImGui.Spacing();
            RemixImGui.TextColored(0.6f, 1.0f, 0.6f, 1.0f, "Scene Scanner");
            RemixImGui.Text($"  Scanned instances: {snap.ScannedInstances}");
            RemixImGui.Text($"  Streaming queue:   {snap.ScannerStreamQueue}");
            RemixImGui.Text($"  Raw scene scan:    {snap.StaticGeometryStats.RawSceneScanInstances}");
            RemixImGui.Text($"  Visible scan:      {snap.StaticGeometryStats.DedupedSceneScanInstances} (-{snap.StaticGeometryStats.SuppressedSceneScanInstances})");

            RemixImGui.Spacing();
            RemixImGui.TextColored(0.6f, 1.0f, 0.6f, 1.0f, "Terrain Manager");
            RemixImGui.Text($"  Terrain chunks:    {snap.TerrainChunks}");

            RemixImGui.Unindent();
        }

        private void DrawErrors(HUDSnapshot snap)
        {
            int errorCount = snap.FailedMeshes + (snap.PlaceholderMaterials?.Length ?? 0);

            if (errorCount == 0)
            {
                RemixImGui.TextColored(0.4f, 1.0f, 0.4f, 1.0f, "No errors");
                return;
            }

            if (!RemixImGui.CollapsingHeader($"Errors ({errorCount})"))
                return;

            RemixImGui.Indent();

            if (snap.FailedMeshes > 0)
            {
                RemixImGui.TextColored(1.0f, 0.4f, 0.4f, 1.0f, $"Failed meshes: {snap.FailedMeshes}");
                RemixImGui.Text("  (non-readable meshes that could not be uploaded)");
            }

            var placeholders = snap.PlaceholderMaterials;
            if (placeholders != null && placeholders.Length > 0)
            {
                RemixImGui.Spacing();
                RemixImGui.TextColored(1.0f, 0.7f, 0.3f, 1.0f,
                    $"No-albedo materials: {placeholders.Length}");

                int tableFlags = RemixImGui.TableFlags_Borders
                               | RemixImGui.TableFlags_RowBg
                               | RemixImGui.TableFlags_SizingStretchProp;

                int maxRows = Math.Min(placeholders.Length, 20);
                if (RemixImGui.BeginTable("##placeholder_mats", 1, tableFlags, 0, 200))
                {
                    RemixImGui.TableSetupColumn("Material Name", 0, 1.0f);
                    RemixImGui.TableHeadersRow();

                    for (int i = 0; i < maxRows; i++)
                    {
                        RemixImGui.TableNextRow();
                        RemixImGui.TableSetColumnIndex(0);
                        RemixImGui.Text(placeholders[i]);
                    }

                    if (placeholders.Length > maxRows)
                    {
                        RemixImGui.TableNextRow();
                        RemixImGui.TableSetColumnIndex(0);
                        RemixImGui.Text($"... and {placeholders.Length - maxRows} more");
                    }

                    RemixImGui.EndTable();
                }
            }

            RemixImGui.Unindent();
        }

        #endregion

        #region 3D Wireframe Boxes

        private void Draw3DBoxes(HUDSnapshot snap)
        {
            RemixImGui.GetDisplaySize(out float screenW, out float screenH);
            if (screenW <= 0 || screenH <= 0) return;

            // clipPos = proj * view * worldPos — Unity's operator* is algebraic
            var viewProj = snap.ProjMatrix * snap.ViewMatrix;
            var entries = snap.MeshEntries;

            // Sort by distance (nearest first) so labels of close objects render on top
            Array.Sort(entries, (a, b) => a.SqrDistToCamera.CompareTo(b.SqrDistToCamera));

            int labelsDrawn = 0;
            var corners = new Vector3[8];
            var screen = new Vector3[8];
            float maxDistSqr = _boxMaxDistance * _boxMaxDistance;

            for (int i = 0; i < entries.Length; i++)
            {
                ref var e = ref entries[i];

                if (e.SqrDistToCamera > maxDistSqr) continue;

                // Skip entries whose layer or renderer is disabled by the user
                var fc = _plugin.FrameCapture;
                if (fc != null && (fc.IsLayerDisabled(e.LayerIndex) || fc.IsRendererDisabled(e.RendererInstanceId)))
                    continue;

                // Choose color based on status
                uint boxColor;
                if (e.Failed)
                    boxColor = RemixImGui.ImColor(1.0f, 0.3f, 0.3f, 0.9f); // red
                else if (e.NoTexture)
                    boxColor = RemixImGui.ImColor(1.0f, 0.8f, 0.2f, 0.9f); // yellow
                else
                    boxColor = RemixImGui.ImColor(0.3f, 1.0f, 0.3f, 0.7f); // green

                // Compute 8 AABB corners in world space
                var c = e.BoundsCenter;
                var ex = e.BoundsExtents;
                // Skip degenerate bounds
                if (ex.x < 0.001f && ex.y < 0.001f && ex.z < 0.001f) continue;

                corners[0] = new Vector3(c.x - ex.x, c.y - ex.y, c.z - ex.z);
                corners[1] = new Vector3(c.x + ex.x, c.y - ex.y, c.z - ex.z);
                corners[2] = new Vector3(c.x + ex.x, c.y + ex.y, c.z - ex.z);
                corners[3] = new Vector3(c.x - ex.x, c.y + ex.y, c.z - ex.z);
                corners[4] = new Vector3(c.x - ex.x, c.y - ex.y, c.z + ex.z);
                corners[5] = new Vector3(c.x + ex.x, c.y - ex.y, c.z + ex.z);
                corners[6] = new Vector3(c.x + ex.x, c.y + ex.y, c.z + ex.z);
                corners[7] = new Vector3(c.x - ex.x, c.y + ex.y, c.z + ex.z);

                // Project all 8 corners to screen space
                bool anyVisible = false;
                for (int j = 0; j < 8; j++)
                {
                    screen[j] = WorldToScreen(corners[j], viewProj, screenW, screenH);
                    if (screen[j].z > 0) anyVisible = true;
                }
                if (!anyVisible) continue;

                // Draw 12 edges of the AABB
                DrawEdge(screen[0], screen[1], boxColor, screenW, screenH);
                DrawEdge(screen[1], screen[2], boxColor, screenW, screenH);
                DrawEdge(screen[2], screen[3], boxColor, screenW, screenH);
                DrawEdge(screen[3], screen[0], boxColor, screenW, screenH);
                DrawEdge(screen[4], screen[5], boxColor, screenW, screenH);
                DrawEdge(screen[5], screen[6], boxColor, screenW, screenH);
                DrawEdge(screen[6], screen[7], boxColor, screenW, screenH);
                DrawEdge(screen[7], screen[4], boxColor, screenW, screenH);
                DrawEdge(screen[0], screen[4], boxColor, screenW, screenH);
                DrawEdge(screen[1], screen[5], boxColor, screenW, screenH);
                DrawEdge(screen[2], screen[6], boxColor, screenW, screenH);
                DrawEdge(screen[3], screen[7], boxColor, screenW, screenH);

                // Floating label above the box (capped count)
                if (labelsDrawn < MaxLabelCount)
                {
                    // Project top-center of bounds
                    var labelWorld = new Vector3(c.x, c.y + ex.y, c.z);
                    var labelScreen = WorldToScreen(labelWorld, viewProj, screenW, screenH);
                    if (labelScreen.z > 0 && labelScreen.x > -200 && labelScreen.x < screenW + 200
                                          && labelScreen.y > -200 && labelScreen.y < screenH + 200)
                    {
                        DrawLabel(labelScreen.x, labelScreen.y, ref e, boxColor, screenW, screenH);
                        labelsDrawn++;
                    }
                }
            }
        }

        /// <summary>
        /// Project a world-space point to screen pixel coordinates.
        /// Returns (screenX, screenY, clipW). clipW > 0 means the point is in front of the camera.
        /// </summary>
        private static Vector3 WorldToScreen(Vector3 world, Matrix4x4 viewProj, float screenW, float screenH)
        {
            // Multiply world point by combined view-projection matrix
            float x = viewProj.m00 * world.x + viewProj.m01 * world.y + viewProj.m02 * world.z + viewProj.m03;
            float y = viewProj.m10 * world.x + viewProj.m11 * world.y + viewProj.m12 * world.z + viewProj.m13;
            float w = viewProj.m30 * world.x + viewProj.m31 * world.y + viewProj.m32 * world.z + viewProj.m33;

            if (w < 0.001f)
                return new Vector3(0, 0, -1); // behind camera

            float invW = 1.0f / w;
            // NDC: [-1, 1] → Screen: [0, width/height]
            float sx = (x * invW * 0.5f + 0.5f) * screenW;
            float sy = (1.0f - (y * invW * 0.5f + 0.5f)) * screenH; // flip Y (screen Y goes down)

            return new Vector3(sx, sy, w);
        }

        private static void DrawEdge(Vector3 a, Vector3 b, uint color, float screenW, float screenH)
        {
            // Only draw if both endpoints are in front of the camera
            if (a.z <= 0 || b.z <= 0) return;

            // Basic screen bounds check to avoid giant off-screen lines
            if (a.x < -500 && b.x < -500) return;
            if (a.x > screenW + 500 && b.x > screenW + 500) return;
            if (a.y < -500 && b.y < -500) return;
            if (a.y > screenH + 500 && b.y > screenH + 500) return;

            RemixImGui.DrawList_AddLine(a.x, a.y, b.x, b.y, color, 1.5f);
        }

        private static void DrawLabel(float x, float y, ref DebugBoxEntry e, uint boxColor, float screenW, float screenH)
        {
            // Build multi-line label text
            string line1 = e.Name;
            string line2 = $"{e.Origin} | {e.LayerName}";
            string line3 = $"Mesh: {(string.IsNullOrEmpty(e.MeshName) ? "(unnamed)" : e.MeshName)}";
            string line4;
            if (e.Failed)
                line4 = "FAILED (non-readable)";
            else if (e.NoTexture)
                line4 = $"Mat: {e.MaterialName} (NO TEXTURE)";
            else if (e.TextureHash != 0)
                line4 = $"Mat: {e.MaterialName} Tex: 0x{e.TextureHash:X8}";
            else
                line4 = $"Mat: {e.MaterialName}";

            // Estimate text block size (approximate: 7px per char width, 14px per line height)
            const float charW = 7f;
            const float lineH = 14f;
            const float pad = 4f;
            float maxLen = Math.Max(Math.Max(line1.Length, line2.Length), Math.Max(line3.Length, line4.Length));
            float blockW = maxLen * charW + pad * 2;
            float blockH = lineH * 4 + pad * 2;

            // Center the label horizontally above the point, offset upward
            float lx = x - blockW * 0.5f;
            float ly = y - blockH - 4f;

            // Clamp to screen
            if (lx < 2) lx = 2;
            if (ly < 2) ly = 2;
            if (lx + blockW > screenW - 2) lx = screenW - blockW - 2;

            // Background
            uint bgColor = RemixImGui.ImColor(0.0f, 0.0f, 0.0f, 0.7f);
            RemixImGui.DrawList_AddRectFilled(lx, ly, lx + blockW, ly + blockH, bgColor, 3f);
            RemixImGui.DrawList_AddRect(lx, ly, lx + blockW, ly + blockH, boxColor, 3f, 1f);

            // Text lines
            uint textWhite = RemixImGui.ImColor(1f, 1f, 1f, 1f);
            uint textGray = RemixImGui.ImColor(0.7f, 0.7f, 0.7f, 1f);
            uint statusColor = e.Failed ? RemixImGui.ImColor(1f, 0.3f, 0.3f, 1f)
                             : e.NoTexture ? RemixImGui.ImColor(1f, 0.8f, 0.2f, 1f)
                             : textGray;

            float tx = lx + pad;
            float ty = ly + pad;
            RemixImGui.DrawList_AddText(tx, ty, textWhite, line1); ty += lineH;
            RemixImGui.DrawList_AddText(tx, ty, textGray, line2);  ty += lineH;
            RemixImGui.DrawList_AddText(tx, ty, textGray, line3);  ty += lineH;
            RemixImGui.DrawList_AddText(tx, ty, statusColor, line4);
        }

        #endregion
    }
}
