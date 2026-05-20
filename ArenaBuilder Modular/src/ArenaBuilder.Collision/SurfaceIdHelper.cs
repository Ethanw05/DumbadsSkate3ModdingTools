namespace ArenaBuilder.Collision;

/// <summary>
/// SurfaceID encode/decode. Exact port of Collision_Export_Dumbad_Tuukkas_original.py lines 51-79.
/// Used by domain/serialization when writing triangle surface IDs.
/// </summary>
public static class SurfaceIdHelper
{
    /// <summary>Encode SurfaceID from component bitfields. audio 0-127, physics 0-31, pattern 0-15.</summary>
    public static int EncodeSurfaceId(int audio, int physics, int pattern)
    {
        return (audio & 0x7F) | ((physics & 0x1F) << 7) | ((pattern & 0x0F) << 12);
    }
}
