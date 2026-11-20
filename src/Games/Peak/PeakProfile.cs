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

        // The local player's head in first person: HideTheBody ghosts (_VertexGhost 1) the local
        // character's head, face, hats, sash, medal and costumes whole, and its body in part (the
        // arms and legs stay). The whole ones are the player body: in reflections and shadows,
        // not in the camera's view. The body stays as it is.
        private Type hideTheBodyType, characterType;
        private FieldInfo hideHeadField, hideFaceField, hideSashField, hideMedalField, hideCostumesField, hideRefsField, refsHatsField;
        private PropertyInfo isLocalProperty;
        private bool bodyLookedUp;

        public override bool IsPlayerBody(Renderer renderer)
        {
            if (!bodyLookedUp)
            {
                bodyLookedUp = true;
                hideTheBodyType = AccessTools.TypeByName("HideTheBody");
                characterType = AccessTools.TypeByName("Character");
                if (hideTheBodyType != null)
                {
                    hideHeadField = AccessTools.Field(hideTheBodyType, "headRend");
                    hideFaceField = AccessTools.Field(hideTheBodyType, "face");
                    hideSashField = AccessTools.Field(hideTheBodyType, "sash");
                    hideMedalField = AccessTools.Field(hideTheBodyType, "medal");
                    hideCostumesField = AccessTools.Field(hideTheBodyType, "costumes");
                    hideRefsField = AccessTools.Field(hideTheBodyType, "refs");
                }
                var refsType = AccessTools.TypeByName("CustomizationRefs");
                refsHatsField = refsType != null ? AccessTools.Field(refsType, "playerHats") : null;
                isLocalProperty = characterType != null ? AccessTools.Property(characterType, "IsLocal") : null;
                if (hideTheBodyType == null || characterType == null || isLocalProperty == null || hideHeadField == null)
                    Logger.LogInfo("[PEAK] HideTheBody/Character not found: the player's head stays in view");
            }
            if (hideTheBodyType == null || characterType == null || isLocalProperty == null || hideHeadField == null)
                return false;
            // HideTheBody sits somewhere in the character's hierarchy, not necessarily above the
            // renderer.
            var character = renderer.GetComponentInParent(characterType);
            if (character == null)
                return false;
            var hide = character.GetComponentInChildren(hideTheBodyType, true);
            bool local = (bool)isLocalProperty.GetValue(character);
            bool body = hide != null && local && GhostedWhole(hide, renderer);
            if (VerboseLog.On)
                Logger.LogInfo($"[PEAK] '{renderer.name}' of '{character.name}': HideTheBody {(hide != null ? "found" : "none")}, local {local}, player body {body}{Describe(renderer)}");
            return body;
        }

        // The local character's body (HideTheBody.body), ghosted in part: W/Character dithers
        // away its texels by vertex red, high on the torso, so the arms and legs stay.
        public override int GhostVertexChannel(SkinnedMeshRenderer renderer)
        {
            if (!bodyLookedUp)
                IsPlayerBody(renderer);
            if (hideTheBodyType == null || characterType == null || isLocalProperty == null)
                return -1;
            var character = renderer.GetComponentInParent(characterType);
            if (character == null || !(bool)isLocalProperty.GetValue(character))
                return -1;
            var hide = character.GetComponentInChildren(hideTheBodyType, true);
            var bodyField = hide != null ? AccessTools.Field(hideTheBodyType, "body") : null;
            return bodyField != null && ReferenceEquals(bodyField.GetValue(hide), renderer) ? 0 : -1;
        }

        // A character renderer's colour inputs (verbose): its material's toggles and colours and
        // its mesh's mean vertex colour.
        private static string Describe(Renderer renderer)
        {
            var m = renderer.sharedMaterial;
            if (m == null)
                return "";
            string F(string p) => m.HasProperty(p) ? m.GetFloat(p).ToString("0.##") : "-";
            string C(string p) => m.HasProperty(p) ? m.GetColor(p).ToString() : "-";
            string colours = "";
            var mesh = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh != null && mesh.isReadable)
            {
                var c = mesh.colors32;
                if (c.Length > 0)
                {
                    long r = 0, g = 0, b = 0;
                    foreach (var v in c) { r += v.r; g += v.g; b += v.b; }
                    colours = $", mean vertex colour ({r / c.Length},{g / c.Length},{b / c.Length})";
                }
            }
            return $" | material '{m.name}' _UseSkinTone {F("_UseSkinTone")} _UseRawVertexColor {F("_UseRawVertexColor")} _VertexColor {F("_VertexColor")} " +
                   $"_SkinColor {C("_SkinColor")} _Tint {C("_Tint")} _BaseColor {C("_BaseColor")} keywords [{string.Join(" ", m.shaderKeywords)}]{colours}";
        }

        private bool GhostedWhole(Component hide, Renderer renderer)
        {
            if (ReferenceEquals(hideHeadField.GetValue(hide), renderer) ||
                ReferenceEquals(hideSashField?.GetValue(hide), renderer) ||
                ReferenceEquals(hideMedalField?.GetValue(hide), renderer))
                return true;
            if (hideCostumesField?.GetValue(hide) is Renderer[] costumes && Array.IndexOf(costumes, renderer) >= 0)
                return true;
            var refs = hideRefsField?.GetValue(hide);
            if (refs != null && refsHatsField?.GetValue(refs) is Renderer[] hats && Array.IndexOf(hats, renderer) >= 0)
                return true;
            var face = hideFaceField?.GetValue(hide) as Transform;
            return face != null && renderer.transform.IsChildOf(face);
        }

        // The eyes (GD/Face Cards with _IsEyes): their texture is a mask, red the eye's shape
        // and green its iris, which the shader draws in fixed colours (a navy ring, a cream
        // iris) and cuts out where red is absent. Baked as a colour mask on a white base, its
        // coverage the red.
        private static readonly Color EyeRing = new Color(0.05f, 0.05f, 0.23f), EyeIris = new Color(0.98f, 0.95f, 0.80f);

        public override bool GetColorMask(Material material, MaterialPropertyBlock block, out RemixMaterialManager.ColorMask colorMask)
        {
            colorMask = default;
            if (material.shader == null || material.shader.name != "GD/Face Cards" || !material.HasProperty("_IsEyes") ||
                material.GetFloat("_IsEyes") < 0.5f)
                return false;
            var mask = material.GetTexture("_MainTex") as Texture2D;
            if (mask == null)
                return false;
            colorMask = new RemixMaterialManager.ColorMask
            {
                mask = mask,
                color1 = EyeRing,
                color2 = EyeIris,
                color3 = Color.white,
                weight = 1f,
                baseInMaskAlpha = true,
                coverageInMaskRed = true
            };
            return true;
        }

        // Face cards draw their texture scaled within the card about its centre,
        // (uv - 0.5) / (_Scale x _BaseScale) + 0.5 (the eyes at a third of the card), clamped: as
        // a texture transform, scale 1 / s and offset 0.5 - 0.5 / s.
        private static bool IsFaceCard(Material material) => material.shader != null && material.shader.name == "GD/Face Cards";

        public override bool ClampsTexture(Material material) => IsFaceCard(material);

        public override void OverrideTextureTransform(Material material, ref Vector4 scaleOffset)
        {
            if (!IsFaceCard(material) || !material.HasProperty("_BaseScale"))
                return;
            float baseScale = material.GetFloat("_BaseScale");
            Vector4 scale = material.HasProperty("_Scale") ? material.GetVector("_Scale") : Vector4.one;
            float sx = scale.x * baseScale, sy = scale.y * baseScale;
            if (Mathf.Abs(sx) < 1e-4f || Mathf.Abs(sy) < 1e-4f)
                return;
            scaleOffset = new Vector4(1f / sx, 1f / sy, 0.5f - 0.5f / sx, 0.5f - 0.5f / sy);
        }

        private static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

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
                // The opening: the mesh's two widest axes, scaled (its thinnest is the normal; the
                // quad may be a plane in any of them), its height the one nearer world up.
                var bounds = filter.sharedMesh.bounds;
                var scale = quad.lossyScale;
                var size = new Vector3(Mathf.Abs(bounds.size.x * scale.x), Mathf.Abs(bounds.size.y * scale.y), Mathf.Abs(bounds.size.z * scale.z));
                int normalAxis = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
                int a = (normalAxis + 1) % 3, b = (normalAxis + 2) % 3;
                Vector3 dirA = quad.TransformDirection(Axis(a)).normalized, dirB = quad.TransformDirection(Axis(b)).normalized;
                if (Mathf.Abs(Vector3.Dot(dirA, Vector3.up)) > Mathf.Abs(Vector3.Dot(dirB, Vector3.up)))
                {
                    (a, b) = (b, a);
                    (dirA, dirB) = (dirB, dirA);
                }
                float width = size[a], height = size[b];
                if (width <= 0f || height <= 0f)
                    continue;
                // A portal's opening faces +Z: the side the camera is on.
                Vector3 normal = quad.TransformDirection(Axis(normalAxis)).normalized;
                Vector3 centre = quad.TransformPoint(bounds.center);
                if (Vector3.Dot(eye - centre, normal) < 0f)
                    normal = -normal;
                var frame0 = Matrix4x4.TRS(centre, Quaternion.LookRotation(normal, dirB), Vector3.one);
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
