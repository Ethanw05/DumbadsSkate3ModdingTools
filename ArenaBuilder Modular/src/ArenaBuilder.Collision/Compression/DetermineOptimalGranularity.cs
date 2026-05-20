using System.Numerics;

namespace ArenaBuilder.Collision.Compression;

/// <summary>
/// Find the finest granularity that keeps all vertices within int32 range for 32-bit compression.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 524-629.
/// </summary>
public static class DetermineOptimalGranularity
{
    private const int Int32Min = -2147483648;
    private const int Int32Max = 2147483647;
    private const double Tolerance = 1e-9;

    public static float Execute(
        IReadOnlyList<Vector3> verts,
        float minGranularity = 0.001f,
        float maxGranularity = 10.0f)
    {
        if (verts == null || verts.Count == 0)
            return minGranularity;

        float meshMinX = float.MaxValue, meshMaxX = float.MinValue;
        float meshMinY = float.MaxValue, meshMaxY = float.MinValue;
        float meshMinZ = float.MaxValue, meshMaxZ = float.MinValue;
        foreach (var v in verts)
        {
            if (v.X < meshMinX) meshMinX = v.X;
            if (v.X > meshMaxX) meshMaxX = v.X;
            if (v.Y < meshMinY) meshMinY = v.Y;
            if (v.Y > meshMaxY) meshMaxY = v.Y;
            if (v.Z < meshMinZ) meshMinZ = v.Z;
            if (v.Z > meshMaxZ) meshMaxZ = v.Z;
        }

        double maxAbsCoord = System.Math.Max(
            System.Math.Max(System.Math.Abs(meshMaxX), System.Math.Abs(meshMinX)),
            System.Math.Max(System.Math.Max(System.Math.Abs(meshMaxY), System.Math.Abs(meshMinY)),
                System.Math.Max(System.Math.Abs(meshMaxZ), System.Math.Abs(meshMinZ))));
        double theoreticalMin = maxAbsCoord > 0 ? maxAbsCoord / Int32Max : minGranularity;
        double granularityLow = System.Math.Max(minGranularity, theoreticalMin);
        double granularityHigh = maxGranularity;

        if (granularityLow > granularityHigh)
            throw new InvalidOperationException(
                $"Mesh too large for 32-bit compression. Max abs coord: {maxAbsCoord}. Required granularity: {theoreticalMin}. Scale mesh down or split.");

        double bestGranularity = granularityHigh;
        while (granularityHigh - granularityLow > Tolerance)
        {
            double granularityMid = (granularityLow + granularityHigh) / 2.0;
            bool fits = true;
            foreach (var v in verts)
            {
                int x = (int)(v.X / granularityMid);
                int y = (int)(v.Y / granularityMid);
                int z = (int)(v.Z / granularityMid);
                if (x < Int32Min || x > Int32Max || y < Int32Min || y > Int32Max || z < Int32Min || z > Int32Max)
                {
                    fits = false;
                    break;
                }
            }
            if (fits)
            {
                bestGranularity = granularityMid;
                granularityHigh = granularityMid;
            }
            else
                granularityLow = granularityMid;
        }

        if (bestGranularity < 0.01)
            return (float)System.Math.Round(bestGranularity, 6);
        return (float)System.Math.Round(bestGranularity, 4);
    }
}
