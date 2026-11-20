using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Peak
{
    internal sealed class PeakProfile : GameProfile
    {
        public override string Name => "PEAK";
        public override string[] ExecutableNames => new[] { "PEAK" };
        public override string DefaultsFolder => "Peak";

        // PEAK's mirrors (Mirror: the airport's): a camera renders the reflection into a
        // RenderTexture shown on a quad (mirrorTransform). Path-traced, the quad's plane is a
        // mirror (a ray portal whose exit is its entry) and the quad, which only shows the
        // game's picture of the reflection, is hidden.
        private Type mirrorType;
        private FieldInfo mirrorTransformField;
        private bool lookedUp;
        private readonly List<Behaviour> mirrors = new List<Behaviour>();
        private int mirrorListFrame = -1000;

        private bool LookUp()
        {
            if (!lookedUp)
            {
                lookedUp = true;
                mirrorType = AccessTools.TypeByName("Mirror");
                mirrorTransformField = mirrorType != null ? AccessTools.Field(mirrorType, "mirrorTransform") : null;
                if (mirrorTransformField == null)
                    Logger.LogInfo("[PEAK] Mirror.mirrorTransform not found: mirrors stay as the game draws them");
            }
            return mirrorTransformField != null;
        }

        public override bool IsHiddenRenderer(Renderer renderer)
        {
            if (!LookUp())
                return false;
            var mirror = renderer.GetComponentInParent(mirrorType);
            return mirror != null && ReferenceEquals(mirrorTransformField.GetValue(mirror) as Transform, renderer.transform);
        }

        public override void GetPortals(Camera worldCamera, List<PortalPair> portals)
        {
            if (worldCamera == null || !LookUp())
                return;
            // The mirrors in the scene, looked up again once a second.
            int frame = Time.frameCount;
            if (frame - mirrorListFrame >= 60)
            {
                mirrorListFrame = frame;
                mirrors.Clear();
                foreach (var found in UnityEngine.Object.FindObjectsOfType(mirrorType))
                    if (found is Behaviour behaviour)
                        mirrors.Add(behaviour);
            }
            Vector3 eye = worldCamera.transform.position;
            foreach (var mirror in mirrors)
            {
                if (mirror == null || !mirror.isActiveAndEnabled)
                    continue;
                var quad = mirrorTransformField.GetValue(mirror) as Transform;
                var filter = quad != null ? quad.GetComponent<MeshFilter>() : null;
                if (quad == null || !quad.gameObject.activeInHierarchy || filter == null || filter.sharedMesh == null)
                    continue;
                // The quad's opening: its mesh's extent in its XY plane, scaled.
                var bounds = filter.sharedMesh.bounds;
                var scale = quad.lossyScale;
                float width = Mathf.Abs(bounds.size.x * scale.x), height = Mathf.Abs(bounds.size.y * scale.y);
                if (width <= 0f || height <= 0f)
                    continue;
                // A portal's opening faces +Z: the side the camera is on.
                var rotation = quad.rotation;
                if (Vector3.Dot(eye - quad.position, quad.forward) < 0f)
                    rotation *= Quaternion.Euler(0f, 180f, 0f);
                var frame0 = Matrix4x4.TRS(quad.TransformPoint(bounds.center), rotation, Vector3.one);
                portals.Add(new PortalPair
                {
                    id = mirror.GetInstanceID(),
                    entry = frame0,
                    exit = frame0,
                    width = width,
                    height = height,
                    mirror = true,
                    lift = 0.01f
                });
            }
        }
    }
}
