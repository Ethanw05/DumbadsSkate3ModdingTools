using System.Numerics;

namespace ArenaBuilder.NavPower;

/// <summary>Legacy Skate v23 NavGraphHeader (312 B) + 52 B Area base + KD chunk (NavPower stream order).</summary>
internal static class NavPowerLegacyGraphSerializer
{
    /// <summary>Default build header floats (typical Skate 3 retail tiles).</summary>
    internal const float DefaultBuildScale = 0.12f;
    internal const float DefaultVoxSize = 0.35f;
    internal const float DefaultRadius = 0.20f;
    internal const float DefaultStep = 1.60f;

    /// <summary>
    /// bfxSystem.h <c>UpAxis</c> — Skate 3 retail <c>cSim_*_high</c> NavGraph headers use <c>X_UP</c> (0), not <c>Y_UP</c>.
    /// </summary>
    internal const uint BuildUpAxisRetailSkate = 0u;

    internal const int QuadEdgeCount = 4;
    internal const int QuadAreaBytes = NavPowerBinaryConstants.AreaBaseLegacyBytes
        + NavPowerBinaryConstants.EdgeBytes32 * QuadEdgeCount;

    /// <summary>
    /// Write a quad-shaped (4-edge) area whose vertices match the cell AABB projected onto
    /// <paramref name="pos"/>.Y, with per-edge adjacency pointers to neighbouring cells.
    /// </summary>
    /// <param name="adjOffsets">
    /// Byte offsets (from NavGraph image start) for the adjacent area on each of the 4 edges,
    /// ordered: minX face, maxZ face, maxX face, minZ face. 0 = no neighbour.
    /// </param>
    internal static void WriteLegacyQuadArea(
        Vector3 pos,
        float radius,
        int island,
        Vector3 min,
        Vector3 max,
        ReadOnlySpan<uint> adjOffsets,
        BigEndianWriter w)
    {
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteFloat32(pos.X);
        w.WriteFloat32(pos.Y);
        w.WriteFloat32(pos.Z);
        w.WriteFloat32(radius);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        uint flags1 = (((uint)(island << 7) & 0x00FFFF80u) | (uint)QuadEdgeCount);
        w.WriteUInt32(flags1);
        w.WriteUInt32(DefaultFlags2());
        w.WriteUInt32(NavPowerBinaryConstants.RetailAreaFlags3GraphIndex);

        float y = pos.Y;
        // CCW winding in XZ: bottom-left -> bottom-right -> top-right -> top-left
        // Edge 0: minX face  (BL -> TL)   adjOffsets[0]
        // Edge 1: maxZ face  (TL -> TR)   adjOffsets[1]
        // Edge 2: maxX face  (TR -> BR)   adjOffsets[2]
        // Edge 3: minZ face  (BR -> BL)   adjOffsets[3]
        Span<float> ex = stackalloc float[] { min.X, min.X, max.X, max.X };
        Span<float> ez = stackalloc float[] { min.Z, max.Z, max.Z, min.Z };

        for (int i = 0; i < QuadEdgeCount; i++)
        {
            w.WriteUInt32(adjOffsets[i]);
            w.WriteFloat32(ex[i]);
            w.WriteFloat32(y);
            w.WriteFloat32(ez[i]);
            w.WriteUInt32(0xFFFF0000u);
            w.WriteUInt32(0);
        }
    }

    /// <summary>Retail DIST_University area flags2: usage_count=256, layer=2, ob/static cost mult=0.</summary>
    private static uint DefaultFlags2() =>
        0x100u | (2u << 11);

    internal static void WriteLegacyNavGraphHeader(
        int areaBytes,
        int totalBytes,
        Box worldBBox,
        NavPowerBuildOptions build,
        BigEndianWriter w)
    {
        w.WriteUInt32(NavPowerBinaryConstants.NavGraphVersionSkate);
        w.WriteUInt32(0); // layer = 0 (retail)
        w.WriteInt32(areaBytes);
        w.WriteInt32(totalBytes);
        w.WriteFloat32(build.HeaderBuildScale);
        w.WriteFloat32(build.HeaderVoxSize);
        w.WriteFloat32(build.HeaderRadius);
        w.WriteFloat32(build.HeaderStep);
        w.WriteFloat32(worldBBox.Min.X);
        w.WriteFloat32(worldBBox.Min.Y);
        w.WriteFloat32(worldBBox.Min.Z);
        w.WriteFloat32(worldBBox.Max.X);
        w.WriteFloat32(worldBBox.Max.Y);
        w.WriteFloat32(worldBBox.Max.Z);
        w.WriteUInt32(BuildUpAxisRetailSkate);
        w.WriteBytes(stackalloc byte[NavPowerBinaryConstants.NavGraphPadBytes]);
    }

    internal static Box ExpandAreasBounds(IReadOnlyList<(Vector3 Pos, float Radius)> areas)
    {
        if (areas.Count == 0)
            return new Box(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
        var mn = new Vector3(float.MaxValue);
        var mx = new Vector3(float.MinValue);
        foreach (var (p, r) in areas)
        {
            mn = Vector3.Min(mn, p - new Vector3(r));
            mx = Vector3.Max(mx, p + new Vector3(r));
        }

        return new Box(mn, mx);
    }
}
