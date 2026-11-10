using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Logs what the camera's center is looking at (object, mesh, materials, shaders, vertex
    /// colors) whenever it changes, checked every 120 frames: ties a screenshot to the materials
    /// on screen. Two answers: the solid collider the view ray hits (UnityEngine.Physics through
    /// reflection, no PhysicsModule reference; trigger volumes ignored), and the nearest renderer
    /// UnityRTX draws along the ray (RemixFrameCapture.PickDrawnRenderer: objects without
    /// colliders too). Main thread only.
    /// </summary>
    internal sealed class LookAtDiagnostics
    {
        private readonly ManualLogSource logger;
        private MethodInfo raycast;
        private object ignoreTriggers;
        private PropertyInfo hitCollider, hitPoint;
        private bool unavailable;
        private int lastTarget, lastDrawn;

        public LookAtDiagnostics(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void Update(Camera camera, RemixFrameCapture capture, int frameCount)
        {
            if (camera == null || frameCount % 120 != 0)
                return;
            var ray = new Ray(camera.transform.position, camera.transform.forward);

            if (capture != null)
            {
                var drawn = capture.PickDrawnRenderer(ray, camera.transform, out float distance);
                int id = drawn != null ? drawn.GetInstanceID() : 0;
                if (drawn != null && id != lastDrawn)
                    logger.LogInfo($"[LookAt] drawn '{drawn.name}' at {distance:F1} m (bounds): {Describe(drawn)}");
                lastDrawn = id;
            }

            if (unavailable)
                return;
            try
            {
                if (raycast == null && !Resolve())
                    return;
                var args = new object[] { ray, null, 1000f, ~0, ignoreTriggers };
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
            // Raycast(Ray, out RaycastHit, float maxDistance, int layerMask, QueryTriggerInteraction)
            foreach (var m in physics.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var p = m.GetParameters();
                if (m.Name == "Raycast" && p.Length == 5 && p[0].ParameterType == typeof(Ray) &&
                    p[1].ParameterType == hitType.MakeByRefType() && p[2].ParameterType == typeof(float) &&
                    p[3].ParameterType == typeof(int) && p[4].ParameterType.IsEnum)
                {
                    raycast = m;
                    ignoreTriggers = Enum.ToObject(p[4].ParameterType, 1); // QueryTriggerInteraction.Ignore
                    break;
                }
            }
            hitCollider = hitType.GetProperty("collider");
            hitPoint = hitType.GetProperty("point");
            if (raycast == null || hitCollider == null || hitPoint == null)
            {
                unavailable = true;
                logger.LogInfo("[LookAt] unavailable: Physics.Raycast(Ray, out RaycastHit, float, int, QueryTriggerInteraction) not found");
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
            var go = renderer.gameObject;
            var text = $"renderer '{renderer.name}' ({renderer.GetType().Name}) path '{Path(renderer.transform)}' layer {LayerMask.LayerToName(go.layer)}({go.layer}), shadows {renderer.shadowCastingMode}, mesh '{(mesh != null ? mesh.name : "-")}'";
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

        private static string Path(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }
    }
}
