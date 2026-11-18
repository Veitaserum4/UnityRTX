using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// Which blood stains to draw: those not hidden under the others. ULTRAKILL keeps up to
    /// bloodStainMax stains (100000 by default) and composites them as one mask (where any stain's
    /// atlas texel is opaque), so a stain whose opaque texels all lie inside other stains' opaque
    /// texels on the same plane changes nothing it shows. Traced as paint, every such stain is
    /// another layer each ray peels and another quad the raster draws: a floor under thousands
    /// of stains cost several ms more than one under the few that show.
    ///
    /// The test is exact: each opaque texel run of the stain, as a parallelogram on its plane,
    /// is cut by the covering stains' runs (convex polygon subtraction) until nothing is left.
    /// Stains are hidden only by stains kept, so the kept stains always cover all of them; a
    /// kept stain the game drops (its ring buffer wraps, a checkpoint reloads) brings back the
    /// hidden stains it covered that no other stain does. New stains are tested in time slices
    /// (UltrakillStainPruning runs them off the main thread); one not yet tested is drawn.
    /// </summary>
    internal sealed class UltrakillStainPruner
    {
        // Plane grouping: normals and offsets within these are one plane.
        private const float NormalStep = 1e-3f, OffsetStep = 1e-3f;
        private const float GridCell = 1f;
        // Pieces of a stain smaller than this are left uncovered by float error, not by a gap.
        private const double AreaEpsilon = 1e-10;

        private struct Rect { public float u0, u1, v0, v1; public int row; }

        private sealed class Stain
        {
            public Vector3 a, b, c, d; // corners as last read, to see a stain replaced
            public bool valid;         // a flat parallelogram, its texels mapped affinely
            public long plane;
            public Vector2d origin, eu, ev; // plane coordinates of uv (0,0) and the uv axes
            public double minX, minY, maxX, maxY;
            public int cell;
            public bool kept = true, tested;
            public List<Vector2d[]> runs; // its opaque runs on its plane, counter-clockwise
            public double[] boxes;        // each run's bounds: x0, y0, x1, y1
            // The run last found uncovered: tested first next time, it settles most tests at once.
            public int witness;
        }

        private struct Vector2d
        {
            public double x, y;
            public Vector2d(double x, double y) { this.x = x; this.y = y; }
            public static Vector2d operator +(Vector2d p, Vector2d q) => new Vector2d(p.x + q.x, p.y + q.y);
            public static Vector2d operator -(Vector2d p, Vector2d q) => new Vector2d(p.x - q.x, p.y - q.y);
            public static Vector2d operator *(Vector2d p, double s) => new Vector2d(p.x * s, p.y * s);
        }

        private sealed class Plane
        {
            public Vector3 normal, t1, t2;
            public float offset;
            public readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
        }

        private readonly List<Rect>[] cellRuns = new List<Rect>[5];
        // Each cell's first run of every atlas row (and one past the last row).
        private readonly int[][] rowStart = new int[5][];
        private int rows;
        private bool haveAtlas;
        private readonly List<Stain> stains = new List<Stain>();
        private readonly Dictionary<long, Plane> planes = new Dictionary<long, Plane>();
        private readonly Queue<int> pending = new Queue<int>();
        private readonly Stopwatch clock = new Stopwatch();
        public int Hidden { get; private set; }

        /// <summary>The atlas's opaque texels (5 cells side by side, rows from the bottom).</summary>
        public void SetAtlas(bool[] opaque, int width, int height)
        {
            haveAtlas = opaque != null && width % 5 == 0 && width > 0 && height > 0;
            if (!haveAtlas)
                return;
            int cellWidth = width / 5;
            rows = height;
            for (int c = 0; c < 5; c++)
            {
                var runs = new List<Rect>();
                rowStart[c] = new int[height + 1];
                for (int y = 0; y < height; y++)
                {
                    rowStart[c][y] = runs.Count;
                    for (int x = 0; x < cellWidth;)
                    {
                        if (!opaque[y * width + c * cellWidth + x]) { x++; continue; }
                        int start = x;
                        while (x < cellWidth && opaque[y * width + c * cellWidth + x])
                            x++;
                        runs.Add(new Rect { u0 = (float)start / cellWidth, u1 = (float)x / cellWidth, v0 = (float)y / height, v1 = (float)(y + 1) / height, row = y });
                    }
                }
                rowStart[c][height] = runs.Count;
                cellRuns[c] = runs;
            }
            Reset();
        }

        public void Reset()
        {
            stains.Clear();
            planes.Clear();
            pending.Clear();
            Hidden = 0;
        }

        public bool IsKept(int index) => index >= stains.Count || stains[index].kept;

        /// <summary>
        /// Reads the stains (quad s: vertices 4s..4s+3), notes the replaced and the dropped, and
        /// tests within the budget. True when the kept set changed.
        /// </summary>
        public bool Update(Vector3[] vertices, Vector2[] uvs, int count, double budgetMilliseconds)
        {
            if (!haveAtlas)
                return false;
            bool changed = false;
            var released = new List<Stain>();
            for (int s = 0; s < count; s++)
            {
                int v = s * 4;
                Vector3 a = vertices[v], b = vertices[v + 1], c = vertices[v + 2], d = vertices[v + 3];
                if (s < stains.Count)
                {
                    var old = stains[s];
                    if (old.a == a && old.b == b && old.c == c && old.d == d)
                        continue;
                    if (Release(s, old))
                        released.Add(old);
                    changed |= !old.kept;
                }
                var stain = Read(s, a, b, c, d, uvs, v);
                if (s < stains.Count)
                    stains[s] = stain;
                else
                    stains.Add(stain);
                if (stain.valid)
                    pending.Enqueue(s);
            }
            for (int s = count; s < stains.Count; s++)
                if (Release(s, stains[s]))
                    released.Add(stains[s]);
            if (stains.Count > count)
            {
                for (int s = count; s < stains.Count; s++)
                    changed |= !stains[s].kept;
                stains.RemoveRange(count, stains.Count - count);
            }
            // Hidden stains a released one covered come back unless the kept still cover them.
            foreach (var gone in released)
                changed |= Uncover(gone);
            changed |= Continue(budgetMilliseconds);
            Hidden = 0;
            foreach (var stain in stains)
                if (!stain.kept)
                    Hidden++;
            return changed;
        }

        /// <summary>Tests pending stains within the budget. True when the kept set changed.</summary>
        public bool Continue(double budgetMilliseconds)
        {
            bool changed = false;
            clock.Restart();
            while (pending.Count > 0 && clock.Elapsed.TotalMilliseconds < budgetMilliseconds)
            {
                int s = pending.Dequeue();
                if (s >= stains.Count)
                    continue;
                var stain = stains[s];
                if (stain.tested || !stain.valid)
                    continue;
                stain.tested = true;
                if (Covered(s))
                {
                    stain.kept = false;
                    changed = true;
                    continue;
                }
                Insert(s);
                // A new stain may hide kept ones under it.
                foreach (int other in Overlapping(stain))
                {
                    if (other == s || !stains[other].kept || !stains[other].tested)
                        continue;
                    if (Covered(other))
                    {
                        Remove(other);
                        stains[other].kept = false;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                Hidden = 0;
                foreach (var stain in stains)
                    if (!stain.kept)
                        Hidden++;
            }
            return changed;
        }

        public bool Pending => pending.Count > 0;

        private Stain Read(int s, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2[] uvs, int v)
        {
            var stain = new Stain { a = a, b = b, c = c, d = d, cell = s % 5 };
            Vector3 normal = Vector3.Cross(b - a, d - a);
            if (normal.sqrMagnitude < 1e-12f || uvs == null || uvs.Length < v + 4)
                return stain;
            normal.Normalize();
            // A flat parallelogram, mapped affinely: c = b + d - a, in place and in uv.
            if ((c - (b + d - a)).sqrMagnitude > 1e-8f || Mathf.Abs(Vector3.Dot(c - a, normal)) > 1e-4f)
                return stain;
            Vector2 ua = uvs[v], ub = uvs[v + 1], uc = uvs[v + 2], ud = uvs[v + 3];
            if ((uc - (ub + ud - ua)).sqrMagnitude > 1e-8f)
                return stain;
            var key = PlaneKey(normal, Vector3.Dot(normal, a), out var plane);
            Vector2d pa = Project(plane, a), pb = Project(plane, b), pd = Project(plane, d);
            // Solve P(u, v) = pa + (u - ua.x, v - ua.y) M, M from the edges a->b and a->d.
            double du1 = ub.x - ua.x, dv1 = ub.y - ua.y, du2 = ud.x - ua.x, dv2 = ud.y - ua.y;
            double det = du1 * dv2 - du2 * dv1;
            if (Math.Abs(det) < 1e-12)
                return stain;
            Vector2d e1 = pb - pa, e2 = pd - pa;
            stain.eu = (e1 * dv2 - e2 * dv1) * (1 / det);
            stain.ev = (e2 * du1 - e1 * du2) * (1 / det);
            stain.origin = pa - stain.eu * ua.x - stain.ev * ua.y;
            stain.plane = key;
            stain.valid = true;
            var cellRunList = cellRuns[stain.cell];
            stain.runs = new List<Vector2d[]>(cellRunList.Count);
            stain.boxes = new double[cellRunList.Count * 4];
            stain.minX = stain.minY = double.MaxValue;
            stain.maxX = stain.maxY = double.MinValue;
            bool flip = stain.eu.x * stain.ev.y - stain.eu.y * stain.ev.x < 0;
            for (int i = 0; i < cellRunList.Count; i++)
            {
                var r = cellRunList[i];
                var p0 = Map(stain, r.u0, r.v0);
                var p1 = Map(stain, r.u1, r.v0);
                var p2 = Map(stain, r.u1, r.v1);
                var p3 = Map(stain, r.u0, r.v1);
                var poly = flip ? new[] { p0, p3, p2, p1 } : new[] { p0, p1, p2, p3 };
                stain.runs.Add(poly);
                Bounds(poly, out stain.boxes[i * 4], out stain.boxes[i * 4 + 1], out stain.boxes[i * 4 + 2], out stain.boxes[i * 4 + 3]);
                stain.minX = Math.Min(stain.minX, stain.boxes[i * 4]);
                stain.minY = Math.Min(stain.minY, stain.boxes[i * 4 + 1]);
                stain.maxX = Math.Max(stain.maxX, stain.boxes[i * 4 + 2]);
                stain.maxY = Math.Max(stain.maxY, stain.boxes[i * 4 + 3]);
            }
            if (stain.runs.Count == 0)
                stain.valid = false;
            return stain;
        }

        private static Vector2d Map(Stain s, float u, float v) => s.origin + s.eu * u + s.ev * v;

        private long PlaneKey(Vector3 normal, float offset, out Plane plane)
        {
            long nx = (long)Mathf.Round(normal.x / NormalStep), ny = (long)Mathf.Round(normal.y / NormalStep),
                 nz = (long)Mathf.Round(normal.z / NormalStep), o = (long)Mathf.Round(offset / OffsetStep);
            long key = ((nx * 4099 + ny) * 4099 + nz) * 1000003 + o;
            if (!planes.TryGetValue(key, out plane))
            {
                var n = new Vector3(nx * NormalStep, ny * NormalStep, nz * NormalStep).normalized;
                var t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                plane = new Plane { normal = n, t1 = t1, t2 = Vector3.Cross(n, t1), offset = o * OffsetStep };
                planes[key] = plane;
            }
            return key;
        }

        private static Vector2d Project(Plane plane, Vector3 p) =>
            new Vector2d(Vector3.Dot(p, plane.t1), Vector3.Dot(p, plane.t2));

        private static long GridKey(int x, int y) => ((long)x << 32) ^ (uint)y;

        private IEnumerable<long> Cells(Stain s)
        {
            int x0 = (int)Math.Floor(s.minX / GridCell), x1 = (int)Math.Floor(s.maxX / GridCell);
            int y0 = (int)Math.Floor(s.minY / GridCell), y1 = (int)Math.Floor(s.maxY / GridCell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    yield return GridKey(x, y);
        }

        private void Insert(int s)
        {
            var plane = planes[stains[s].plane];
            foreach (long cell in Cells(stains[s]))
            {
                if (!plane.grid.TryGetValue(cell, out var list))
                    plane.grid[cell] = list = new List<int>();
                list.Add(s);
            }
        }

        private void Remove(int s)
        {
            if (!planes.TryGetValue(stains[s].plane, out var plane))
                return;
            foreach (long cell in Cells(stains[s]))
                if (plane.grid.TryGetValue(cell, out var list))
                    list.Remove(s);
        }

        // A stain leaves (replaced or dropped): out of the grid. True when it was a tested kept
        // stain, whose hidden neighbours may need it.
        private bool Release(int s, Stain stain)
        {
            if (!stain.valid || !stain.tested || !stain.kept)
                return false;
            Remove(s);
            return true;
        }

        // The kept stains whose bounds meet this one's, on its plane.
        private HashSet<int> Overlapping(Stain s)
        {
            var found = new HashSet<int>();
            if (!planes.TryGetValue(s.plane, out var plane))
                return found;
            foreach (long cell in Cells(s))
                if (plane.grid.TryGetValue(cell, out var list))
                    foreach (int other in list)
                    {
                        var o = stains[other];
                        if (o.maxX >= s.minX && o.minX <= s.maxX && o.maxY >= s.minY && o.minY <= s.maxY)
                            found.Add(other);
                    }
            return found;
        }

        private bool Uncover(Stain gone)
        {
            bool changed = false;
            for (int s = 0; s < stains.Count; s++)
            {
                var stain = stains[s];
                if (stain.kept || stain.plane != gone.plane || stain.maxX < gone.minX || stain.minX > gone.maxX ||
                    stain.maxY < gone.minY || stain.minY > gone.maxY)
                    continue;
                if (!Covered(s))
                {
                    stain.kept = true;
                    Insert(s);
                    changed = true;
                }
            }
            return changed;
        }

        // Whether every opaque run of stain s lies inside the kept stains' runs.
        private bool Covered(int s)
        {
            var stain = stains[s];
            var others = Overlapping(stain);
            others.Remove(s);
            if (others.Count == 0)
                return false;
            int runCount = stain.runs.Count;
            for (int k = 0; k < runCount; k++)
            {
                int i = (stain.witness + k) % runCount;
                if (!RunCovered(stain, i, others))
                {
                    stain.witness = i;
                    return false;
                }
            }
            return true;
        }

        private readonly List<Vector2d[]> pieces = new List<Vector2d[]>(), nextPieces = new List<Vector2d[]>();

        // Whether run i of the stain lies inside the other stains' runs.
        private bool RunCovered(Stain stain, int i, HashSet<int> others)
        {
            var pieceList = pieces;
            var next = nextPieces;
            pieceList.Clear();
            pieceList.Add(stain.runs[i]);
            double rx0 = stain.boxes[i * 4], ry0 = stain.boxes[i * 4 + 1], rx1 = stain.boxes[i * 4 + 2], ry1 = stain.boxes[i * 4 + 3];
            foreach (int other in others)
            {
                var o = stains[other];
                if (o.maxX < rx0 || o.minX > rx1 || o.maxY < ry0 || o.minY > ry1)
                    continue;
                // The cover's rows the run's bounds can meet, through its inverse map.
                RowRange(o, rx0, ry0, rx1, ry1, out int row0, out int row1);
                var starts = rowStart[o.cell];
                for (int j = starts[row0]; j < starts[row1 + 1]; j++)
                {
                    if (o.boxes[j * 4 + 2] < rx0 || o.boxes[j * 4] > rx1 || o.boxes[j * 4 + 3] < ry0 || o.boxes[j * 4 + 1] > ry1)
                        continue;
                    next.Clear();
                    foreach (var piece in pieceList)
                        Subtract(piece, o.runs[j], next);
                    (pieceList, next) = (next, pieceList);
                    if (pieceList.Count == 0)
                        return true;
                }
            }
            return false;
        }

        private void RowRange(Stain o, double x0, double y0, double x1, double y1, out int row0, out int row1)
        {
            double det = o.eu.x * o.ev.y - o.eu.y * o.ev.x;
            double vMin = double.MaxValue, vMax = double.MinValue;
            for (int k = 0; k < 4; k++)
            {
                double px = (k & 1) == 0 ? x0 : x1, py = (k & 2) == 0 ? y0 : y1;
                double dx = px - o.origin.x, dy = py - o.origin.y;
                double v = (o.eu.x * dy - o.eu.y * dx) / det;
                vMin = Math.Min(vMin, v);
                vMax = Math.Max(vMax, v);
            }
            row0 = Math.Max(0, Math.Min(rows - 1, (int)Math.Floor(vMin * rows) - 1));
            row1 = Math.Max(0, Math.Min(rows - 1, (int)Math.Floor(vMax * rows) + 1));
            if (vMax < 0 || vMin > 1)
                row1 = row0 - 1;
        }

        private static void Bounds(Vector2d[] poly, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = double.MaxValue;
            x1 = y1 = double.MinValue;
            foreach (var p in poly)
            {
                x0 = Math.Min(x0, p.x);
                y0 = Math.Min(y0, p.y);
                x1 = Math.Max(x1, p.x);
                y1 = Math.Max(y1, p.y);
            }
        }

        // piece minus cover (both convex, counter-clockwise): the parts outside each of cover's
        // edges in turn, each inside the edges before it. Slivers of float error are dropped.
        private static void Subtract(Vector2d[] piece, Vector2d[] cover, List<Vector2d[]> output)
        {
            // Most pieces lie wholly outside one of cover's edges (kept whole) or inside all of
            // them (gone): settled without clipping.
            bool inside = true;
            for (int i = 0; i < cover.Length; i++)
            {
                var p = cover[i];
                var q = cover[(i + 1) % cover.Length];
                bool allOut = true, allIn = true;
                foreach (var x in piece)
                {
                    double side = (q.x - p.x) * (x.y - p.y) - (q.y - p.y) * (x.x - p.x);
                    allOut &= side <= 0;
                    allIn &= side >= 0;
                }
                if (allOut)
                {
                    output.Add(piece);
                    return;
                }
                inside &= allIn;
            }
            if (inside)
                return;
            var rest = piece;
            for (int i = 0; i < cover.Length && rest != null; i++)
            {
                var p = cover[i];
                var q = cover[(i + 1) % cover.Length];
                var outside = Clip(rest, p, q, false);
                if (outside != null && Area(outside) > AreaEpsilon)
                    output.Add(outside);
                rest = Clip(rest, p, q, true);
            }
        }

        // The part of poly on the inner (left) or outer side of the line p->q.
        private static Vector2d[] Clip(Vector2d[] poly, Vector2d p, Vector2d q, bool inner)
        {
            var result = new List<Vector2d>(poly.Length + 2);
            double Side(Vector2d x) => (q.x - p.x) * (x.y - p.y) - (q.y - p.y) * (x.x - p.x);
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                double sa = Side(a), sb = Side(b);
                bool ina = inner ? sa >= 0 : sa <= 0, inb = inner ? sb >= 0 : sb <= 0;
                if (ina)
                    result.Add(a);
                if (ina != inb)
                {
                    double t = sa / (sa - sb);
                    result.Add(a + (b - a) * t);
                }
            }
            return result.Count >= 3 ? result.ToArray() : null;
        }

        private static double Area(Vector2d[] poly)
        {
            double sum = 0;
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                sum += a.x * b.y - b.x * a.y;
            }
            return Math.Abs(sum) * 0.5;
        }
    }
}
