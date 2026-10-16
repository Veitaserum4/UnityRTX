using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    public readonly struct StaticGeometryKey : IEquatable<StaticGeometryKey>
    {
        public readonly int MeshId;
        public readonly int Layer;
        public readonly int MaterialSignature;
        public readonly int CenterX;
        public readonly int CenterY;
        public readonly int CenterZ;
        public readonly int ExtentX;
        public readonly int ExtentY;
        public readonly int ExtentZ;

        public StaticGeometryKey(
            int meshId,
            int layer,
            int materialSignature,
            int centerX,
            int centerY,
            int centerZ,
            int extentX,
            int extentY,
            int extentZ)
        {
            MeshId = meshId;
            Layer = layer;
            MaterialSignature = materialSignature;
            CenterX = centerX;
            CenterY = centerY;
            CenterZ = centerZ;
            ExtentX = extentX;
            ExtentY = extentY;
            ExtentZ = extentZ;
        }

        public bool IsValid => MeshId != 0;

        public bool Equals(StaticGeometryKey other)
        {
            return MeshId == other.MeshId
                && Layer == other.Layer
                && MaterialSignature == other.MaterialSignature
                && CenterX == other.CenterX
                && CenterY == other.CenterY
                && CenterZ == other.CenterZ
                && ExtentX == other.ExtentX
                && ExtentY == other.ExtentY
                && ExtentZ == other.ExtentZ;
        }

        public override bool Equals(object obj)
        {
            return obj is StaticGeometryKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MeshId;
                hash = (hash * 397) ^ Layer;
                hash = (hash * 397) ^ MaterialSignature;
                hash = (hash * 397) ^ CenterX;
                hash = (hash * 397) ^ CenterY;
                hash = (hash * 397) ^ CenterZ;
                hash = (hash * 397) ^ ExtentX;
                hash = (hash * 397) ^ ExtentY;
                hash = (hash * 397) ^ ExtentZ;
                return hash;
            }
        }
    }

    public struct StaticGeometryStats
    {
        public int RawStaticRenderers;
        public int DedupedStaticRenderers;
        public int SuppressedStaticRenderers;
        public int RawStaticMeshes;
        public int DedupedStaticMeshes;
        public int SuppressedStaticMeshes;
        public int RawSceneScanInstances;
        public int DedupedSceneScanInstances;
        public int SuppressedSceneScanInstances;
        public int DedupedVisibleStaticTotal;
    }

    internal static class StaticGeometryDedupe
    {
        private const float BoundsQuantization = 1000.0f;

        public static bool TryClaimVisibleInstance(
            int rendererInstanceId,
            StaticGeometryKey dedupeKey,
            HashSet<int> claimedRendererIds,
            HashSet<StaticGeometryKey> claimedKeys)
        {
            if (rendererInstanceId != 0 && claimedRendererIds.Contains(rendererInstanceId))
                return false;

            if (dedupeKey.IsValid && !claimedKeys.Add(dedupeKey))
                return false;

            if (rendererInstanceId != 0)
                claimedRendererIds.Add(rendererInstanceId);

            return true;
        }

        public static StaticGeometryKey BuildKey(Renderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null)
                return default;

            Bounds bounds = renderer.bounds;
            return BuildKey(
                mesh.GetInstanceID(),
                renderer.gameObject.layer,
                ComputeMaterialSignature(renderer.sharedMaterials),
                in bounds);
        }

        public static StaticGeometryKey BuildKey(int meshId, int layer, int materialSignature, in Bounds bounds)
        {
            if (meshId == 0)
                return default;

            return new StaticGeometryKey(
                meshId,
                layer,
                materialSignature,
                Quantize(bounds.center.x),
                Quantize(bounds.center.y),
                Quantize(bounds.center.z),
                Quantize(bounds.extents.x),
                Quantize(bounds.extents.y),
                Quantize(bounds.extents.z));
        }

        public static int ComputeMaterialSignature(Material[] materials)
        {
            unchecked
            {
                int hash = 17;
                if (materials == null)
                    return hash;

                for (int i = 0; i < materials.Length; i++)
                {
                    int materialId = materials[i] != null ? materials[i].GetInstanceID() : 0;
                    hash = (hash * 31) ^ materialId;
                }
                return hash;
            }
        }

        private static int Quantize(float value)
        {
            return Mathf.RoundToInt(value * BoundsQuantization);
        }
    }
}
