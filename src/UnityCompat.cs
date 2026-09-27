using System;
using System.Collections.Generic;
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

        private static readonly Func<CanvasRenderer, Mesh> canvasGetMesh = CreateCanvasGetMesh();

        private static Func<CanvasRenderer, Mesh> CreateCanvasGetMesh()
        {
            var method = typeof(CanvasRenderer).GetMethod("GetMesh", Type.EmptyTypes);
            return method == null ? null : (Func<CanvasRenderer, Mesh>)Delegate.CreateDelegate(
                typeof(Func<CanvasRenderer, Mesh>), method);
        }

        // A missing method lets the caller use its existing UI geometry fallback.
        public static Mesh GetCanvasMesh(CanvasRenderer renderer) => canvasGetMesh?.Invoke(renderer);
    }
}
