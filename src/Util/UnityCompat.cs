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
        // Mesh.GetVertexAttributeDimension and its family (2019.3).
        public static readonly bool HasMeshVertexAttributes =
            typeof(Mesh).GetMethod("GetVertexAttributeDimension", new[] { typeof(UnityEngine.Rendering.VertexAttribute) }) != null;

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
