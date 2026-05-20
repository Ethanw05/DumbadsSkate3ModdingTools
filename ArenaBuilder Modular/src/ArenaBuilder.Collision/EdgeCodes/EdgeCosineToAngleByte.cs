namespace ArenaBuilder.Collision.EdgeCodes;

/// <summary>
/// Quantize extended edge cosine to 5-bit angle byte. ClusteredMeshBuilderUtils::EdgeCosineToAngleByte (clusteredmeshbuilderutils.cpp lines 30-63).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 166-213.
/// </summary>
public static class EdgeCosineToAngleByte
{
    private const float MinAngle = 6.6e-5f;

    /// <summary>Returns value in range 0-26. B=0 fully convex, B=26 coplanar.</summary>
    public static int Execute(float edgeCosine)
    {
        float angle = edgeCosine > 1f
            ? System.MathF.Acos(2f - edgeCosine)
            : System.MathF.Acos(edgeCosine);
        angle = System.MathF.Max(angle, MinAngle);
        int result = (int)(-2f * System.MathF.Log(angle / System.MathF.PI) / System.MathF.Log(2f));
        if (result < 0) return 0;
        if (result > 26) return 26;
        return result;
    }
}
