using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Compatibility shim for Mesh APIs added in Unity 2020.1+.
    /// Computes stride, offset, and stream from VertexAttributeDescriptor (available since 2019.1).
    /// </summary>
    internal static class MeshCompat
    {
        /// <summary>
        /// Returns the byte stride for a given vertex buffer stream.
        /// Equivalent to Mesh.GetVertexBufferStride(stream) (2020.1+).
        /// </summary>
        public static int GetVertexBufferStride(Mesh mesh, int stream)
        {
            var attrs = mesh.GetVertexAttributes();
            int stride = 0;
            foreach (var attr in attrs)
            {
                if (attr.stream == stream)
                    stride += FormatSize(attr.format) * attr.dimension;
            }
            return stride;
        }

        /// <summary>
        /// Returns the byte offset of a vertex attribute within its stream.
        /// Equivalent to Mesh.GetVertexAttributeOffset(attr) (2020.1+).
        /// </summary>
        public static int GetVertexAttributeOffset(Mesh mesh, VertexAttribute attribute)
        {
            var attrs = mesh.GetVertexAttributes();
            int targetStream = -1;

            // First pass: find which stream the attribute lives in
            foreach (var a in attrs)
            {
                if (a.attribute == attribute)
                {
                    targetStream = a.stream;
                    break;
                }
            }
            if (targetStream < 0)
                return -1;

            // Second pass: sum sizes of all attributes in the same stream that appear before this one
            int offset = 0;
            foreach (var a in attrs)
            {
                if (a.stream != targetStream)
                    continue;
                if (a.attribute == attribute)
                    return offset;
                offset += FormatSize(a.format) * a.dimension;
            }
            return -1;
        }

        /// <summary>
        /// Returns which stream a vertex attribute is in.
        /// Equivalent to Mesh.GetVertexAttributeStream(attr) (2020.1+).
        /// </summary>
        public static int GetVertexAttributeStream(Mesh mesh, VertexAttribute attribute)
        {
            var attrs = mesh.GetVertexAttributes();
            foreach (var a in attrs)
            {
                if (a.attribute == attribute)
                    return a.stream;
            }
            return -1;
        }

        /// <summary>
        /// Returns the byte size of one element of a VertexAttributeFormat.
        /// </summary>
        public static int FormatSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: return 4;
                case VertexAttributeFormat.Float16: return 2;
                case VertexAttributeFormat.UNorm8:  return 1;
                case VertexAttributeFormat.SNorm8:  return 1;
                case VertexAttributeFormat.UNorm16: return 2;
                case VertexAttributeFormat.SNorm16: return 2;
                case VertexAttributeFormat.UInt8:   return 1;
                case VertexAttributeFormat.SInt8:   return 1;
                case VertexAttributeFormat.UInt16:  return 2;
                case VertexAttributeFormat.SInt16:  return 2;
                case VertexAttributeFormat.UInt32:  return 4;
                case VertexAttributeFormat.SInt32:  return 4;
                default: return 4;
            }
        }

        /// <summary>
        /// Compute per-vertex normals by averaging face normals of adjacent triangles.
        /// Pure mathematical utility, universal across all graphics APIs.
        /// </summary>
        public static Vector3[] ComputeFaceNormals(Vector3[] verts, int[][] subMeshIndices)
        {
            if (verts == null) return null;
            var normals = new Vector3[verts.Length];
            if (subMeshIndices != null)
            {
                foreach (var indices in subMeshIndices)
                {
                    if (indices == null) continue;
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
                        var faceNormal = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                        normals[i0] += faceNormal;
                        normals[i1] += faceNormal;
                        normals[i2] += faceNormal;
                    }
                }
            }
            for (int i = 0; i < normals.Length; i++)
            {
                float len = normals[i].magnitude;
                normals[i] = len > 1e-6f ? normals[i] / len : Vector3.up;
            }
            return normals;
        }
    }
}
