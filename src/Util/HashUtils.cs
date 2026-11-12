using System;
using UnityEngine;
using System.Text;

namespace UnityRemix
{
    public static class HashUtils
    {
        public static ulong HashStringFNV(string str)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis
            if (!string.IsNullOrEmpty(str))
            {
                foreach (char c in str)
                {
                    hash ^= c;
                    hash *= 1099511628211UL; // FNV prime
                }
            }
            return hash;
        }

        public static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            StringBuilder path = new StringBuilder();
            while (t != null)
            {
                if (path.Length > 0)
                {
                    path.Insert(0, "/");
                }
                path.Insert(0, $"{t.name}_{t.GetSiblingIndex()}");
                t = t.parent;
            }
            return path.ToString();
        }

        public static ulong GetHierarchyHash(Transform t)
        {
            return HashStringFNV(GetHierarchyPath(t));
        }

        public static int GetHierarchyHashInt(Transform t)
        {
            return unchecked((int)GetHierarchyHash(t));
        }
    }
}
