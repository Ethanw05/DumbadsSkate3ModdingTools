using System.Numerics;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Buckets collision triangles on XZ into coarse cells so NavGraph area count stays bounded.
/// This is a <strong>placeholder</strong> nav mesh (centroid sampling), not NavPower-quality mesh graph connectivity.
/// </summary>
internal static class NavPowerTriangleBucket
{
    internal static List<(Vector3 Pos, float Radius, Vector3 Min, Vector3 Max)> BuildCells(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int A, int B, int C)> faces,
        float minX,
        float maxX,
        float minZ,
        float maxZ,
        float cellSize,
        int maxCells)
    {
        if (faces.Count == 0)
        {
            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;
            float cy = 0f;
            var p = new Vector3(cx, cy, cz);
            return new List<(Vector3, float, Vector3, Vector3)>
            {
                (p, MathF.Max(maxX - minX, maxZ - minZ) * 0.25f + 0.5f, p - Vector3.One, p + Vector3.One),
            };
        }

        cellSize = Math.Max(cellSize, 0.5f);
        int nx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellSize));
        int nz = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / cellSize));

        var accum = new Dictionary<(int Ix, int Iz), CellAgg>();

        foreach (var (a, b, c) in faces)
        {
            var v0 = verts[a];
            var v1 = verts[b];
            var v2 = verts[c];
            var triMin = Vector3.Min(Vector3.Min(v0, v1), v2);
            var triMax = Vector3.Max(Vector3.Max(v0, v1), v2);
            var centroid = (v0 + v1 + v2) / 3f;

            int ix = (int)MathF.Floor((centroid.X - minX) / cellSize);
            int iz = (int)MathF.Floor((centroid.Z - minZ) / cellSize);
            ix = Math.Clamp(ix, 0, nx - 1);
            iz = Math.Clamp(iz, 0, nz - 1);

            var key = (ix, iz);
            if (!accum.TryGetValue(key, out var cell))
                cell = new CellAgg { Min = triMin, Max = triMax, Count = 0 };
            else
            {
                cell.Min = Vector3.Min(cell.Min, triMin);
                cell.Max = Vector3.Max(cell.Max, triMax);
            }

            cell.Count++;
            accum[key] = cell;
        }

        var list = accum
            .Select(kv => (Key: kv.Key, Agg: kv.Value))
            .OrderBy(x => x.Key.Iz)
            .ThenBy(x => x.Key.Ix)
            .Select(x => x.Agg)
            .ToList();

        if (list.Count > maxCells)
        {
            int step = (int)MathF.Ceiling((float)list.Count / maxCells);
            var merged = new List<CellAgg>();
            for (int i = 0; i < list.Count; i += step)
            {
                var slice = list.Skip(i).Take(step);
                var agg = new CellAgg
                {
                    Min = new Vector3(float.MaxValue),
                    Max = new Vector3(float.MinValue),
                    Count = 0,
                };
                foreach (var c in slice)
                {
                    agg.Min = Vector3.Min(agg.Min, c.Min);
                    agg.Max = Vector3.Max(agg.Max, c.Max);
                    agg.Count += c.Count;
                }

                merged.Add(agg);
            }

            list = merged;
        }

        var result = new List<(Vector3, float, Vector3, Vector3)>(list.Count);
        foreach (var c in list)
        {
            var center = (c.Min + c.Max) * 0.5f;
            float ext = Vector3.Distance(c.Min, c.Max) * 0.5f + 0.15f;
            ext = Math.Max(ext, 0.25f);
            result.Add((center, ext, c.Min, c.Max));
        }

        return result;
    }

    private struct CellAgg
    {
        internal Vector3 Min;
        internal Vector3 Max;
        internal int Count;
    }
}
