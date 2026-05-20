namespace ArenaBuilder.Collision.EdgeCodes;

/// <summary>
/// Edge code flags. RenderWare: clusteredmeshcluster.h lines 88-95.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 654-658.
/// </summary>
public static class EdgeCodeConstants
{
    /// <summary>Angle byte 26 (5 bits).</summary>
    public const int EdgeAngleZero = 0x1A;
    /// <summary>Bit 5 - convex edge.</summary>
    public const int EdgeConvex = 0x20;
    /// <summary>Bit 6 - disables vertex collision for smoothing.</summary>
    public const int EdgeVertexDisable = 0x40;
    /// <summary>Bit 7 - unmatched edge.</summary>
    public const int EdgeUnmatched = 0x80;
}
