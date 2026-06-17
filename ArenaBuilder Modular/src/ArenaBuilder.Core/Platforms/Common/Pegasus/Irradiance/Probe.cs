namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

/// <summary>
/// A single SH light probe sample.
///
/// On-disk size 160 B (IDA-verified against Sk8::Render::SHLightingMan in
/// sk82_na_zd.xex — <c>AddHullLightProbes @ 0x82b71148</c> reads
/// <c>*(blob+0x04) + i*0xA0 + 0x90</c> for probe[i] position; stride 0xA0=160,
/// position offset 0x90=144).
/// Memory layout = disk layout (no fixup needed): nine SH coefficient vec4s
/// followed by a position vec4. Each vec4 packs three channels with a 4th
/// zero-padded slot.
///
/// SH ordering matches the Python reference and Sk8 runtime evaluation order:
///   0 = L0 0 (DC ambient)
///   1 = L1-1, 2 = L1 0, 3 = L1 1
///   4 = L2-2, 5 = L2-1, 6 = L2 0, 7 = L2 1, 8 = L2 2
/// </summary>
public sealed record Probe
{
    /// <summary>World-space position, game frame (Y up, right-handed).</summary>
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Z { get; init; }

    /// <summary>27 RGB SH coefficients stored as 9 channel-triplets.</summary>
    public required float[] ShR { get; init; }
    public required float[] ShG { get; init; }
    public required float[] ShB { get; init; }

    public const int ShBandCount = 9;
    public const int OnDiskSize  = 160;
}
