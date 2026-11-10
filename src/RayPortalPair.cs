using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// A game's view portal (GameProfile.GetRayPortalView) as a Remix ray portal pair. OpenRemix
    /// maps a camera ray meeting the front of portal 0 (any surface of a material with
    /// remixapi_MaterialInfoPortalEXT index 0) by T1 diag(-1, -1, 1) T0^-1 and goes on from
    /// there, just in front of portal 1's plane; T0, T1 are the two portals' instance transforms
    /// (scene/portals.h). Here both are hidden quads, drawn each frame: portal 0 on the plane
    /// through the player's camera, facing it, so every camera ray going forward meets its front
    /// whatever the surface's own facing; portal 1 = M T0 diag(-1, -1, 1) for the view's map M
    /// (x -> exitCamera + scale (x - camera)). So the map is M: a ray meeting a portal surface
    /// goes on from the exit camera's matching ray, just in front of the exit camera.
    /// In Remix's mesh space a portal's opening is the plane x = 0, front +X, up +Z: in Unity's
    /// (y and z swapped) front +X, up +Y, and diag(-1, -1, 1) is diag(-1, 1, -1).
    /// </summary>
    internal sealed class RayPortalPair
    {
        public const int EntranceIndex = 0, ExitIndex = 1;

        private const ulong EntranceMeshKey = 0x5250_4F52_0000_0000UL, ExitMeshKey = 0x5250_4F52_0000_0001UL;
        private const int EntranceMaterialId = -3100000, ExitMaterialId = -3100001;
        private readonly ManualLogSource logger;
        private bool queued;
        private bool logged;

        public RayPortalPair(ManualLogSource logger) => this.logger = logger;

        // Main thread: the two quads and their portal materials, once.
        public void Ensure(RemixFrameCapture capture, RemixMaterialManager materials)
        {
            if (queued || capture == null || materials == null)
                return;
            queued = true;
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("UI/Default") ?? Shader.Find("Standard");
            Queue(capture, materials, shader, EntranceMeshKey, EntranceMaterialId, EntranceIndex, "RemixRayPortalEntrance");
            Queue(capture, materials, shader, ExitMeshKey, ExitMaterialId, ExitIndex, "RemixRayPortalExit");
        }

        private static void Queue(RemixFrameCapture capture, RemixMaterialManager materials, Shader shader, ulong meshKey, int materialId, int index, string name)
        {
            var material = new Material(shader) { name = name, color = Color.black };
            materials.SetRayPortalIndex(materialId, index);
            materials.CaptureMaterialTextures(material, materialId);
            // A tiny quad on the plane x = 0, far along it: the portal's frame is its transform,
            // the quad itself is never seen.
            const float far = 10000f, size = 0.001f;
            var vertices = new[]
            {
                new Vector3(0f, far, far), new Vector3(0f, far + size, far),
                new Vector3(0f, far + size, far + size), new Vector3(0f, far, far + size)
            };
            var normals = new[] { Vector3.right, Vector3.right, Vector3.right, Vector3.right };
            capture.QueuePriorityMesh(new PreparedMeshData
            {
                MeshKey = meshKey,
                MeshId = (int)(meshKey & 0x7FFFFFFF),
                MeshName = name,
                MeshHash = RemixMeshConverter.GenerateMeshHash(name, 4, 6, materialId),
                Vertices = vertices,
                Normals = normals,
                UVs = new Vector2[4],
                SubmeshIndices = new List<uint[]> { new uint[] { 0, 1, 2, 0, 2, 3 } },
                SubmeshMaterials = new List<Material> { material },
                SubmeshMaterialIds = new List<int> { materialId }
            });
        }

        // Render thread: the pair for this frame's camera and view.
        public void Draw(RemixMeshConverter meshes, RemixFrameCapture.CameraData camera, ref uint objectPickingValue)
        {
            if (!meshes.TryGetMeshHandle(EntranceMeshKey, out var entrance) || !meshes.TryGetMeshHandle(ExitMeshKey, out var exit))
                return;
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
            var t1 = map * t0 * Matrix4x4.Scale(new Vector3(-1f, 1f, -1f));
            meshes.DrawMeshInstance(entrance, t0, objectPickingValue++, 0, EntranceMeshKey, isDynamic: true);
            meshes.DrawMeshInstance(exit, t1, objectPickingValue++, 0, ExitMeshKey, isDynamic: true);
            if (!logged)
            {
                logged = true;
                logger.LogInfo($"[Portal] view portal pair drawn: camera {camera.position} -> exit {view.exitCamera} at scale {view.scale}");
            }
        }
    }
}
