namespace ArenaBuilder.Collision.Math;

/// <summary>
/// Alignment helpers for PSG layout. Same semantics as Python align/align_qw.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 105-111.
/// </summary>
public static class AlignmentHelpers
{
    /// <summary>Align n to the next multiple of a. (n + (a-1)) & ~(a-1).</summary>
    public static long Align(long n, int a)
    {
        if (a <= 0) throw new ArgumentOutOfRangeException(nameof(a));
        return (n + (a - 1)) & ~(a - 1L);
    }

    /// <summary>Align to 16 bytes (quad-word). (n + 15) & ~15.</summary>
    public static long AlignQw(long n) => (n + 15) & ~15L;
}
