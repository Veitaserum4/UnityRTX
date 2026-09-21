using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Thin wrappers around Unity APIs that all current targets (Unity 2020+) support directly.
    /// Previously contained reflection-based fallbacks for Unity 2018/2019 that are no longer needed.
    /// </summary>
    internal static class UnityCompat
    {
        /// <summary>
        /// Finds all objects of type T, optionally including inactive ones.
        /// Uses FindObjectsOfType&lt;T&gt;(bool) available in Unity 2020.1+.
        /// </summary>
        public static T[] FindObjects<T>(bool includeInactive = false) where T : Object
        {
            return Object.FindObjectsOfType<T>(includeInactive);
        }

        /// <summary>
        /// Returns mipmap count for a texture. Available directly on Texture since Unity 2019.1.
        /// </summary>
        public static int GetMipmapCount(Texture tex)
        {
            return tex != null ? tex.mipmapCount : 1;
        }

        /// <summary>
        /// Returns whether the texture's pixel data is readable on the CPU.
        /// Available directly on Texture since Unity 2019.1.
        /// </summary>
        public static bool IsReadable(Texture tex)
        {
            return tex != null && tex.isReadable;
        }
    }
}
