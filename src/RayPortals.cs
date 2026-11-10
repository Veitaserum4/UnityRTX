using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// A game's portals as Remix ray portals (remixapi_MaterialInfoPortalEXT): portal i's partner
    /// is i ^ 1, and a camera ray meeting the front of portal A goes on from its partner B,
    /// mapped by T_B diag(-1, -1, 1) T_A^-1 (OpenRemix scene/portals.h; T: the instance
    /// transforms). In Remix's mesh space a portal's opening is the plane x = 0, front +X, up
    /// +Z; in Unity's (y and z swapped) front +X, up +Y, and the flip is diag(-1, 1, -1). Up to
    /// SlotPairs pairs, each drawn as two quads with standalone portal materials, every frame:
    ///
    /// - The view portal (GameProfile.GetRayPortalView), pair 0 when there is one: surfaces of
    ///   materials the game names open it (index 0 over their own look). Its two quads are
    ///   hidden (tiny, far along their planes); portal 0 lies on the plane through the player's
    ///   camera, facing it, so every forward camera ray meets its front, and portal 1 is mapped
    ///   by x -> exit + scale (x - camera): a ray meeting such a surface goes on from the exit
    ///   camera's matching ray, just in front of the exit camera.
    /// - The game's portals (GameProfile.GetRayPortals), nearest first: openings of their own
    ///   (unit quads scaled to the opening), framed like the game's (opening in the frame's local
    ///   XY plane, normal +Z, travel exit diag(-1, 1, -1) entry^-1): a quarter turn about y puts
    ///   Remix's +X on the game's +Z (it commutes with the flip, as does the opening's scale).
    ///   Remix opens a portal from its front only: both ends face the side of the nearer end
    ///   the camera is on.
    /// </summary>
    internal sealed class RayPortals
    {
        public const int EntranceIndex = 0, ExitIndex = 1;
        public const int SlotPairs = 4;

        private const ulong MeshKeyBase = 0x5250_4F52_0000_0000UL;
        private const int MaterialIdBase = -3100000;
        private readonly ManualLogSource logger;
        private bool queued;
        private bool loggedView;
        private readonly HashSet<int> loggedPortals = new HashSet<int>();

        public RayPortals(ManualLogSource logger) => this.logger = logger;

        // Hidden view-portal quads (indices 0, 1) and opening quads (every index).
        private static ulong HiddenMeshKey(int index) => MeshKeyBase | 0x100UL | (uint)index;
        private static ulong OpeningMeshKey(int index) => MeshKeyBase | (uint)index;

        // Main thread: the quads and their portal materials, once.
        public void Ensure(RemixFrameCapture capture, RemixMaterialManager materials)
        {
            if (queued || capture == null || materials == null)
                return;
            queued = true;
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("UI/Default") ?? Shader.Find("Standard");
            for (int index = 0; index < SlotPairs * 2; index++)
            {
                var material = new Material(shader) { name = $"RemixRayPortal{index}", color = Color.black };
                int materialId = MaterialIdBase - index;
                materials.SetRayPortalIndex(materialId, index);
                materials.CaptureMaterialTextures(material, materialId);
                // An opening: the unit square on the plane x = 0.
                Queue(capture, OpeningMeshKey(index), material, materialId, $"RemixRayPortalOpening{index}", 0f, 1f);
                // Hidden: a tiny square far along the plane (the portal's frame is its transform).
                if (index < 2)
                    Queue(capture, HiddenMeshKey(index), material, materialId, $"RemixRayPortalHidden{index}", 10000f, 0.001f);
            }
        }

        private static void Queue(RemixFrameCapture capture, ulong meshKey, Material material, int materialId, string name, float offset, float size)
        {
            float lo = offset - size * 0.5f, hi = offset + size * 0.5f;
            var vertices = new[] { new Vector3(0f, lo, lo), new Vector3(0f, hi, lo), new Vector3(0f, hi, hi), new Vector3(0f, lo, hi) };
            var normals = new[] { Vector3.right, Vector3.right, Vector3.right, Vector3.right };
            var uvs = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            capture.QueuePriorityMesh(new PreparedMeshData
            {
                MeshKey = meshKey,
                MeshId = (int)(meshKey & 0x7FFFFFFF),
                MeshName = name,
                MeshHash = RemixMeshConverter.GenerateMeshHash(name, 4, 6, materialId),
                Vertices = vertices,
                Normals = normals,
                UVs = uvs,
                SubmeshIndices = new List<uint[]> { new uint[] { 0, 1, 2, 0, 2, 3 } },
                SubmeshMaterials = new List<Material> { material },
                SubmeshMaterialIds = new List<int> { materialId }
            });
        }

        // Render thread: this frame's portals.
        public void Draw(RemixMeshConverter meshes, RemixFrameCapture.CameraData camera, ref uint objectPickingValue)
        {
            int pair = 0;
            if (camera.portalActive && DrawView(meshes, camera, ref objectPickingValue))
                pair = 1;
            var portals = camera.portals;
            for (int i = 0; portals != null && i < portals.Length && pair < SlotPairs; i++)
            {
                if (DrawPortal(meshes, camera, portals[i], pair, ref objectPickingValue))
                    pair++;
            }
        }

        private bool DrawView(RemixMeshConverter meshes, RemixFrameCapture.CameraData camera, ref uint objectPickingValue)
        {
            if (!meshes.TryGetMeshHandle(HiddenMeshKey(EntranceIndex), out var entrance) || !meshes.TryGetMeshHandle(HiddenMeshKey(ExitIndex), out var exit))
                return false;
            var view = camera.portal;
            Vector3 front = -camera.forward.normalized;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, front);
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.ProjectOnPlane(camera.up, front);
            up.Normalize();
            var t0 = Matrix4x4.identity;
            t0.SetColumn(0, front);
            t0.SetColumn(1, up);
            t0.SetColumn(2, Vector3.Cross(front, up));
            t0.SetColumn(3, new Vector4(camera.position.x, camera.position.y, camera.position.z, 1f));
            var map = Matrix4x4.Translate(view.exitCamera) * Matrix4x4.Scale(Vector3.one * view.scale) * Matrix4x4.Translate(-camera.position);
            var t1 = map * t0 * Flip;
            meshes.DrawMeshInstance(entrance, t0, objectPickingValue++, 0, HiddenMeshKey(EntranceIndex), isDynamic: true);
            meshes.DrawMeshInstance(exit, t1, objectPickingValue++, 0, HiddenMeshKey(ExitIndex), isDynamic: true);
            if (!loggedView)
            {
                loggedView = true;
                logger.LogInfo($"[Portal] view portal drawn: camera {camera.position} -> exit {view.exitCamera} at scale {view.scale}");
            }
            return true;
        }

        private static readonly Matrix4x4 Flip = Matrix4x4.Scale(new Vector3(-1f, 1f, -1f));
        // Remix's opening frame onto the game's: x -> +z or -z (a quarter turn about y).
        private static readonly Matrix4x4 FacePlusZ = Matrix4x4.Rotate(Quaternion.AngleAxis(-90f, Vector3.up));
        private static readonly Matrix4x4 FaceMinusZ = Matrix4x4.Rotate(Quaternion.AngleAxis(90f, Vector3.up));

        private bool DrawPortal(RemixMeshConverter meshes, RemixFrameCapture.CameraData camera, Games.PortalPair portal, int pair, ref uint objectPickingValue)
        {
            int entranceIndex = pair * 2, exitIndex = entranceIndex + 1;
            if (!meshes.TryGetMeshHandle(OpeningMeshKey(entranceIndex), out var entrance) || !meshes.TryGetMeshHandle(OpeningMeshKey(exitIndex), out var exit))
                return false;
            Vector3 entryPos = portal.entry.GetColumn(3), exitPos = portal.exit.GetColumn(3);
            bool nearEntry = (camera.position - entryPos).sqrMagnitude <= (camera.position - exitPos).sqrMagnitude;
            Vector3 nearPos = nearEntry ? entryPos : exitPos;
            Vector3 nearNormal = nearEntry ? (Vector3)portal.entry.GetColumn(2) : (Vector3)portal.exit.GetColumn(2);
            var face = Vector3.Dot(camera.position - nearPos, nearNormal) >= 0f ? FacePlusZ : FaceMinusZ;
            // The opening's size along Remix's y (the game's y) and z (the game's x).
            var size = Matrix4x4.Scale(new Vector3(1f, portal.height, portal.width));
            var t0 = portal.entry * face * size;
            var t1 = portal.exit * face * size;
            meshes.DrawMeshInstance(entrance, t0, objectPickingValue++, 0, OpeningMeshKey(entranceIndex), isDynamic: true);
            meshes.DrawMeshInstance(exit, t1, objectPickingValue++, 0, OpeningMeshKey(exitIndex), isDynamic: true);
            if (loggedPortals.Add(portal.id))
                logger.LogInfo($"[Portal] portal {portal.id} as pair {pair}: entry {entryPos}, exit {exitPos}, {portal.width:F2} x {portal.height:F2}");
            return true;
        }
    }
}
