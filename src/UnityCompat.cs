using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Universal engine compatibility helpers across Unity 2018.x - Unity 6+.
    /// </summary>
    internal static class UnityCompat
    {
        private static readonly MethodInfo _findObjectsOfTypeWithInactive;
        private static readonly bool _hasFindObjectsWithInactive;

        static UnityCompat()
        {
            try
            {
                // FindObjectsOfType(Type, bool) was introduced in Unity 2020.1
                _findObjectsOfTypeWithInactive = typeof(UnityEngine.Object).GetMethod(
                    "FindObjectsOfType",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(Type), typeof(bool) },
                    null);

                _hasFindObjectsWithInactive = _findObjectsOfTypeWithInactive != null;
            }
            catch
            {
                _hasFindObjectsWithInactive = false;
            }
        }

        /// <summary>
        /// Finds all objects of type T. Safely handles includeInactive across all Unity versions:
        /// - If includeInactive is false: uses standard FindObjectsOfType&lt;T&gt;() (all Unity versions).
        /// - If includeInactive is true on Unity 2020.1+: uses FindObjectsOfType(Type, bool).
        /// - If includeInactive is true on pre-2020.1 (Unity 2018/2019): uses Resources.FindObjectsOfTypeAll&lt;T&gt;()
        ///   filtered to valid, loaded scene objects only (excluding prefabs and assets).
        /// </summary>
        public static T[] FindObjects<T>(bool includeInactive = false) where T : UnityEngine.Object
        {
            if (!includeInactive)
            {
                return UnityEngine.Object.FindObjectsOfType<T>();
            }

            if (_hasFindObjectsWithInactive)
            {
                try
                {
                    var raw = (UnityEngine.Object[])_findObjectsOfTypeWithInactive.Invoke(
                        null, new object[] { typeof(T), true });

                    if (raw == null || raw.Length == 0)
                        return Array.Empty<T>();

                    var typed = new T[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                        typed[i] = (T)raw[i];

                    return typed;
                }
                catch
                {
                    // Fall back to Resources.FindObjectsOfTypeAll
                }
            }

            // Fallback for Unity 2018.x - 2019.x
            var all = Resources.FindObjectsOfTypeAll<T>();
            if (all == null || all.Length == 0)
                return Array.Empty<T>();

            bool isComponent = typeof(Component).IsAssignableFrom(typeof(T));
            bool isGameObject = typeof(GameObject).IsAssignableFrom(typeof(T));

            if (!isComponent && !isGameObject)
                return all;

            var list = new List<T>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                var obj = all[i];
                if (obj == null) continue;

                if (isComponent)
                {
                    var comp = (Component)(object)obj;
                    try
                    {
                        var sc = comp.gameObject.scene;
                        if (sc.IsValid() && sc.isLoaded)
                            list.Add(obj);
                    }
                    catch { }
                }
                else if (isGameObject)
                {
                    var go = (GameObject)(object)obj;
                    try
                    {
                        var sc = go.scene;
                        if (sc.IsValid() && sc.isLoaded)
                            list.Add(obj);
                    }
                    catch { }
                }
            }

            return list.ToArray();
        }
    }
}
