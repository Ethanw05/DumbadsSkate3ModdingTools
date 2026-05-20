using System.Numerics;

namespace ArenaBuilder.Collision;

/// <summary>
/// Position-based weld before triangle neighbor / edge-code generation. Used by the tile collision accumulator
/// and by <see cref="CollisionPsgComposer"/> when hosts pass unwelded soup (CLI, Blender, etc.).
/// </summary>
public static class CollisionVertexWelder
{
    /// <summary>Scales with mesh extent; cap raised for large tiles so chunk seams still merge.</summary>
    public static float ComputeAdaptiveEpsilon(IReadOnlyList<Vector3> verts)
    {
        if (verts.Count == 0)
            return 1e-3f;
        Vector3 mn = verts[0], mx = verts[0];
        foreach (var v in verts)
        {
            mn = Vector3.Min(mn, v);
            mx = Vector3.Max(mx, v);
        }
        float extent = (mx - mn).Length();
        float adaptive = MathF.Max(1e-4f, extent * 1e-5f);
        return MathF.Min(adaptive, 2e-2f);
    }

    public static (List<Vector3> Vertices, List<(int V0, int V1, int V2)> Faces) Weld(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int V0, int V1, int V2)> faces,
        float epsilon)
    {
        var vList = new List<Vector3>(verts.Count);
        foreach (var v in verts)
            vList.Add(v);
        var fList = new List<(int, int, int)>(faces.Count);
        foreach (var f in faces)
            fList.Add(f);
        return WeldInPlace(vList, fList, epsilon);
    }

    public static (List<Vector3> Vertices, List<(int V0, int V1, int V2)> Faces) WeldInPlace(
        List<Vector3> verts,
        List<(int V0, int V1, int V2)> faces,
        float epsilon)
    {
        var (newVerts, newFaces, _) = WeldInPlaceWithRemap(verts, faces, epsilon);
        return (newVerts, newFaces);
    }

    /// <summary>
    /// Same as <see cref="WeldInPlace(List{Vector3}, List{(int V0, int V1, int V2)}, float)"/>,
    /// but also returns an old-vertex-index to welded-vertex-index remap.
    /// </summary>
    public static (List<Vector3> Vertices, List<(int V0, int V1, int V2)> Faces, int[] OldToNew) WeldInPlaceWithRemap(
        List<Vector3> verts,
        List<(int V0, int V1, int V2)> faces,
        float epsilon)
    {
        if (verts.Count == 0 || faces.Count == 0)
            return (verts, faces, Array.Empty<int>());
        if (!(epsilon > 0))
            throw new ArgumentOutOfRangeException(nameof(epsilon), "epsilon must be > 0");

        int Quantize(float v) => (int)(v / epsilon);

        float epsilonSq = epsilon * epsilon;
        var map = new Dictionary<(int X, int Y, int Z), List<int>>(capacity: verts.Count);
        var newVerts = new List<Vector3>(capacity: verts.Count);
        var oldToNew = new int[verts.Count];
        Array.Fill(oldToNew, -1);
        int Remap(int oldIndex)
        {
            int cached = oldToNew[oldIndex];
            if (cached >= 0)
                return cached;

            var v = verts[oldIndex];
            var key = (Quantize(v.X), Quantize(v.Y), Quantize(v.Z));

            int bestExisting = -1;
            float bestDistSq = float.MaxValue;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var probe = (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz);
                        if (!map.TryGetValue(probe, out var candidates))
                            continue;

                        for (int i = 0; i < candidates.Count; i++)
                        {
                            int existing = candidates[i];
                            float distSq = Vector3.DistanceSquared(v, newVerts[existing]);
                            if (distSq <= epsilonSq && distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                bestExisting = existing;
                            }
                        }
                    }
                }
            }

            if (bestExisting >= 0)
            {
                oldToNew[oldIndex] = bestExisting;
                return bestExisting;
            }

            int idx = newVerts.Count;
            newVerts.Add(v);
            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>(1);
                map[key] = bucket;
            }
            bucket.Add(idx);
            oldToNew[oldIndex] = idx;
            return idx;
        }

        var newFaces = new List<(int, int, int)>(capacity: faces.Count);
        foreach (var (v0, v1, v2) in faces)
            newFaces.Add((Remap(v0), Remap(v1), Remap(v2)));

        return (newVerts, newFaces, oldToNew);
    }
}
