using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Logs what the camera's center is looking at (object, mesh, materials, shaders, vertex
    /// colors) whenever it changes, checked every 120 frames: ties a screenshot to the materials
    /// on screen. Uses UnityEngine.Physics through reflection (no PhysicsModule reference); hits
    /// only objects with colliders. Main thread only.
    /// </summary>
    internal sealed class LookAtDiagnostics
    {
        private readonly ManualLogSource logger;
        private MethodInfo raycast;
        private PropertyInfo hitCollider, hitPoint;
        private bool unavailable;
        private int lastTarget;

        public LookAtDiagnostics(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void Update(Camera camera, int frameCount)
        {
            if (unavailable || camera == null || frameCount % 120 != 0)
                return;
            try
            {
                if (raycast == null && !Resolve())
                    return;
                var ray = new Ray(camera.transform.position, camera.transform.forward);
                var args = new object[] { ray, null, 1000f };
                if (!(bool)raycast.Invoke(null, args))
                    return;
                var collider = hitCollider.GetValue(args[1]) as Component;
                if (collider == null)
                    return;
                var renderer = collider.GetComponent<Renderer>() ?? collider.GetComponentInParent<Renderer>() ?? collider.GetComponentInChildren<Renderer>();
                int target = renderer != null ? renderer.GetInstanceID() : collider.GetInstanceID();
                if (target == lastTarget)
                    return;
                lastTarget = target;
                var point = (Vector3)hitPoint.GetValue(args[1]);
                logger.LogInfo($"[LookAt] '{collider.name}' at {Vector3.Distance(camera.transform.position, point):F1} m: {Describe(renderer)}");
            }
            catch (Exception ex)
            {
                unavailable = true;
                logger.LogInfo($"[LookAt] unavailable: {ex.Message}");
            }
        }

        private bool Resolve()
        {
            var physics = Type.GetType("UnityEngine.Physics, UnityEngine.PhysicsModule") ?? Type.GetType("UnityEngine.Physics, UnityEngine");
            var hitType = Type.GetType("UnityEngine.RaycastHit, UnityEngine.PhysicsModule") ?? Type.GetType("UnityEngine.RaycastHit, UnityEngine");
            if (physics == null || hitType == null)
            {
                unavailable = true;
                logger.LogInfo("[LookAt] unavailable: no UnityEngine.Physics");
                return false;
            }
            raycast = physics.GetMethod("Raycast", new[] { typeof(Ray), hitType.MakeByRefType(), typeof(float) });
            hitCollider = hitType.GetProperty("collider");
            hitPoint = hitType.GetProperty("point");
            if (raycast == null || hitCollider == null || hitPoint == null)
            {
                unavailable = true;
                logger.LogInfo("[LookAt] unavailable: Physics.Raycast(Ray, out RaycastHit, float) not found");
                return false;
            }
            return true;
        }

        private static string Describe(Renderer renderer)
        {
            if (renderer == null)
                return "no renderer";
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
            else { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) mesh = filter.sharedMesh; }
            var text = $"renderer '{renderer.name}' ({renderer.GetType().Name}), mesh '{(mesh != null ? mesh.name : "-")}'";
            if (mesh != null)
            {
                text += $" {mesh.vertexCount} verts, readable {mesh.isReadable}, colors {mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)}";
                var t = renderer.transform;
                text += $", rotation {t.rotation.eulerAngles}, scale {t.lossyScale}";
            }
            var mats = renderer.sharedMaterials;
            for (int i = 0; mats != null && i < mats.Length; i++)
            {
                var m = mats[i];
                text += m != null ? $" | material {i} '{m.name}' shader '{(m.shader != null ? m.shader.name : "-")}' id {StaticGeometryDedupe.GetCanonicalMaterialId(m)}" : $" | material {i} none";
            }
            return text;
        }
    }
}
