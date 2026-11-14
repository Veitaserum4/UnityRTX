using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Optional Unity APIs are resolved at runtime so the plugin also builds
    /// against Unity 2019 game assemblies.
    /// </summary>
    internal static class UnityCompat
    {
        private static class SceneFinder<T> where T : Component
        {
            internal static readonly Func<bool, T[]> Find = CreateFinder();

            private static Func<bool, T[]> CreateFinder()
            {
                var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(bool) });
                return method == null ? null : (Func<bool, T[]>)Delegate.CreateDelegate(
                    typeof(Func<bool, T[]>), method.MakeGenericMethod(typeof(T)));
            }
        }

        public static T[] FindSceneComponentsIncludingInactive<T>() where T : Component
        {
            if (SceneFinder<T>.Find != null)
                return SceneFinder<T>.Find(true);

            // Resources also includes prefab assets and internal objects. Keep
            // loaded scene instances, including inactive and DontDestroyOnLoad objects.
            var result = new List<T>();
            foreach (var component in Resources.FindObjectsOfTypeAll<T>())
            {
                if (component == null)
                    continue;
                var gameObject = component.gameObject;
                if (gameObject == null || !gameObject.scene.IsValid() || !gameObject.scene.isLoaded)
                    continue;
                if ((component.hideFlags & HideFlags.DontSave) != 0 ||
                    (gameObject.hideFlags & HideFlags.DontSave) != 0)
                    continue;
                result.Add(component);
            }
            result.Sort((left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));
            return result.ToArray();
        }

        // Object.FindObjectsByType<T>(FindObjectsSortMode.None) (Unity 2021.3.18+/2022.2+):
        // active objects like FindObjectsOfType, without sorting them by instance ID.
        private static class UnsortedSceneFinder<T> where T : Component
        {
            internal static readonly Func<T[]> Find = CreateFinder();

            private static Func<T[]> CreateFinder()
            {
                try
                {
                    foreach (var method in typeof(UnityEngine.Object).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (method.Name != "FindObjectsByType" || !method.IsGenericMethodDefinition)
                            continue;
                        var parameters = method.GetParameters();
                        if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum ||
                            parameters[0].ParameterType.Name != "FindObjectsSortMode")
                            continue;
                        var none = System.Linq.Expressions.Expression.Constant(Enum.ToObject(parameters[0].ParameterType, 0));
                        var call = System.Linq.Expressions.Expression.Call(method.MakeGenericMethod(typeof(T)), none);
                        return System.Linq.Expressions.Expression.Lambda<Func<T[]>>(call).Compile();
                    }
                }
                catch
                {
                }
                return null;
            }
        }

        // Active scene components in no particular order.
        public static T[] FindActiveSceneComponents<T>() where T : Component
        {
            if (UnsortedSceneFinder<T>.Find != null)
                return UnsortedSceneFinder<T>.Find();
            if (SceneFinder<T>.Find != null)
                return SceneFinder<T>.Find(false);
            return UnityEngine.Object.FindObjectsOfType<T>();
        }

        private static readonly Func<CanvasRenderer, Mesh> canvasGetMesh = CreateCanvasGetMesh();

        private static Func<CanvasRenderer, Mesh> CreateCanvasGetMesh()
        {
            var method = typeof(CanvasRenderer).GetMethod("GetMesh", Type.EmptyTypes);
            return method == null ? null : (Func<CanvasRenderer, Mesh>)Delegate.CreateDelegate(
                typeof(Func<CanvasRenderer, Mesh>), method);
        }

        // A missing method lets the caller use its existing UI geometry fallback.
        public static Mesh GetCanvasMesh(CanvasRenderer renderer) => canvasGetMesh?.Invoke(renderer);

        // APIs newer than the oldest Unity the plugin runs in (DUSK: 2018.2). Code naming one of
        // their types fails to compile (TypeLoadException) when the method is first called, so
        // each use sits in a NoInlining method of its own, called only when the API exists.
        public static readonly bool HasParticleCullingMode =
            typeof(ParticleSystem).Assembly.GetType("UnityEngine.ParticleSystemCullingMode") != null;
        public static readonly bool HasShaderPropertyType =
            typeof(Shader).Assembly.GetType("UnityEngine.Rendering.ShaderPropertyType") != null;
        public static readonly bool HasGraphicsBuffer =
            typeof(Mesh).Assembly.GetType("UnityEngine.GraphicsBuffer") != null;
        // Mesh.GetVertexAttributeDimension and its family (2019.3). Found by name: the
        // VertexAttribute type itself is missing before 2019 (DUSK's 2018.2), and naming it
        // here would fail this class's initializer.
        public static readonly bool HasMeshVertexAttributes = HasMethod(typeof(Mesh), "GetVertexAttributeDimension");
        // Mesh.HasVertexAttribute (2019.3).
        public static readonly bool HasMeshAttributeQuery = HasMethod(typeof(Mesh), "HasVertexAttribute");
        // SkinnedMeshRenderer.BakeMesh(Mesh, bool useScale) (2020.2).
        public static readonly bool HasBakeMeshUseScale =
            typeof(SkinnedMeshRenderer).GetMethod("BakeMesh", new[] { typeof(Mesh), typeof(bool) }) != null;
        // TerrainData.holesResolution / GetHoles (2019.3) and terrainLayers (2018.3).
        public static readonly bool HasTerrainHoles = HasMethod(typeof(TerrainData), "GetHoles");
        public static readonly bool HasTerrainLayers = typeof(TerrainData).GetProperty("terrainLayers") != null;
        // RenderPipelineManager's camera events (2019.1).
        public static readonly bool HasSrpCameraEvents =
            typeof(Camera).Assembly.GetType("UnityEngine.Rendering.RenderPipelineManager") != null;

        private static bool HasMethod(Type type, string name)
        {
            foreach (var m in type.GetMethods())
                if (m.Name == name) return true;
            return false;
        }

        private static Func<T, R> Getter<T, R>(string property)
        {
            try
            {
                var getter = typeof(T).GetProperty(property)?.GetGetMethod();
                return getter == null || getter.ReturnType != typeof(R) || getter.DeclaringType != typeof(T) ? null
                    : (Func<T, R>)Delegate.CreateDelegate(typeof(Func<T, R>), getter);
            }
            catch { return null; }
        }

        private static readonly Func<Renderer, bool> forceRenderingOff = Getter<Renderer, bool>("forceRenderingOff");
        private static readonly Func<Texture, bool> textureIsReadable = Getter<Texture, bool>("isReadable");
        private static readonly Func<Texture, int> textureMipmapCount = Getter<Texture, int>("mipmapCount");
        private static readonly Func<ParticleSystemRenderer, Vector3> particleFlip = Getter<ParticleSystemRenderer, Vector3>("flip");
        private static readonly Func<CanvasRenderer, float> inheritedAlpha = CreateInheritedAlpha();

        private static Func<CanvasRenderer, float> CreateInheritedAlpha()
        {
            var method = typeof(CanvasRenderer).GetMethod("GetInheritedAlpha", Type.EmptyTypes);
            return method == null ? null : (Func<CanvasRenderer, float>)Delegate.CreateDelegate(typeof(Func<CanvasRenderer, float>), method);
        }

        /// <summary>Renderer.forceRenderingOff (2019.3); false where Unity lacks it.</summary>
        public static bool IsForceRenderingOff(Renderer renderer) => forceRenderingOff != null && forceRenderingOff(renderer);

        /// <summary>CanvasRenderer.GetInheritedAlpha (2019.1); 1 where Unity lacks it.</summary>
        public static float GetInheritedAlpha(CanvasRenderer renderer) => inheritedAlpha != null ? inheritedAlpha(renderer) : 1f;

        /// <summary>ParticleSystemRenderer.flip (2018.3); none where Unity lacks it.</summary>
        public static Vector3 GetParticleFlip(ParticleSystemRenderer renderer) => particleFlip != null ? particleFlip(renderer) : Vector3.zero;

        // Texture.isReadable and mipmapCount moved to Texture in 2018.3/2019; before, each texture
        // class declares its own (looked up on the texture's type, by reflection, rarely).
        private static readonly Dictionary<Type, System.Reflection.PropertyInfo> readableProps = new Dictionary<Type, System.Reflection.PropertyInfo>();
        private static readonly Dictionary<Type, System.Reflection.PropertyInfo> mipProps = new Dictionary<Type, System.Reflection.PropertyInfo>();

        private static object OwnProperty(Texture texture, Dictionary<Type, System.Reflection.PropertyInfo> cache, string name)
        {
            var type = texture.GetType();
            System.Reflection.PropertyInfo prop;
            lock (cache)
            {
                if (!cache.TryGetValue(type, out prop))
                    cache[type] = prop = type.GetProperty(name);
            }
            return prop?.GetValue(texture, null);
        }

        public static bool IsReadable(Texture texture)
        {
            if (texture == null) return false;
            if (textureIsReadable != null) return textureIsReadable(texture);
            return OwnProperty(texture, readableProps, "isReadable") is bool readable && readable;
        }

        public static int MipmapCount(Texture texture)
        {
            if (texture == null) return 1;
            if (textureMipmapCount != null) return textureMipmapCount(texture);
            return OwnProperty(texture, mipProps, "mipmapCount") is int count ? count : 1;
        }

        /// <summary>Mesh.HasVertexAttribute (2019.3) for UnityEngine.Rendering.VertexAttribute's value; null where Unity lacks it.</summary>
        public static bool? MeshHasAttribute(Mesh mesh, int attribute) =>
            HasMeshAttributeQuery && mesh != null ? QueryMeshAttribute(mesh, attribute) : (bool?)null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool QueryMeshAttribute(Mesh mesh, int attribute) =>
            mesh.HasVertexAttribute((UnityEngine.Rendering.VertexAttribute)attribute);

        // UnityEngine.Rendering.VertexAttribute's values (stable across versions).
        public const int AttributeNormal = 1, AttributeColor = 3, AttributeTexCoord0 = 4;

        /// <summary>SkinnedMeshRenderer.BakeMesh with useScale (2020.2); older Unity bakes with the scale already.</summary>
        public static void BakeMeshScaled(SkinnedMeshRenderer skinned, Mesh mesh)
        {
            if (HasBakeMeshUseScale) BakeWithScale(skinned, mesh);
            else skinned.BakeMesh(mesh);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BakeWithScale(SkinnedMeshRenderer skinned, Mesh mesh) => skinned.BakeMesh(mesh, true);

        /// <summary>GraphicsSettings.currentRenderPipeline (2019.3), null where Unity lacks it.</summary>
        public static object CurrentRenderPipeline() =>
            typeof(UnityEngine.Rendering.GraphicsSettings).GetProperty("currentRenderPipeline")?.GetValue(null, null);

        /// <summary>Keeps a particle system simulating while off screen (where Unity has the setting).</summary>
        public static void KeepSimulating(ParticleSystem system)
        {
            if (HasParticleCullingMode && system != null)
                SetAlwaysSimulate(system);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetAlwaysSimulate(ParticleSystem system)
        {
            var main = system.main;
            if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate)
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        }
    }
}
