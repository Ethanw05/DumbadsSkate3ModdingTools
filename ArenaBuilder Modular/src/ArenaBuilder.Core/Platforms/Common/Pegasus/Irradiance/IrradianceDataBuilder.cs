using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

/// <summary>
/// Packs an <c>IrradianceData</c> (type 0x00EB0024) object body.
///
/// On-disk shape decoded from Sk2 Sk8::Render::SHLightingMan::AddHullLightProbes
/// (sk82_na_zd.xex @ 0x82b71148; IDA-verified disasm 2026-06-04):
///   +0x00  u32  probe count                             (lwz r10, 0(r30) @ 0x82b711cc)
///   +0x04  u32  POINTER to probe[0] at runtime          (lwz r10, 4(r30) @ 0x82b712b0;
///                                                        addi r10, r10, 0x90;
///                                                        lvx128 v63, r0, r10)
///   +0x08  8B   padding (0xDE filler per Python reference; engine ignores)
///   +0x10  N×160 B  probes  (9 SH vec4 + 1 position vec4 each; position @ +0x90)
///
/// ** OPEN QUESTION (load-time fixup for +0x04) **
/// The engine treats *(blob+0x04) as a memory address, so something between PSG-load
/// and AddHullLightProbes must convert the on-disk value (we write 16) into an
/// absolute pointer into the loaded blob. ProcessLightProbeData (0x832ca248) does NOT
/// patch it; Subreferences (0x00010007) populates its own dict, not the object body.
/// AIPath PSGs ship with Subrefs=null and reportedly work via "self-relative" fixup
/// per project memory — but AIPath access patterns differ (engine adds container+offset
/// at access time; IrradianceData here loads a raw pointer). If hulls fail to bind on
/// custom DLC, suspect missing per-object pointer fixup at offset +0x04.
///
/// Engine hard cap: 1681 probes per asset (SpatialGrid is fixed 41×41 cells).
/// If <c>count &gt; 1681</c>, the engine clamps and prints
/// <c>[SH LightingMan] lightprobes for this hull are %d while the system can only handle %d</c>.
/// Hull count cap: <c>mnSize &lt; 0xC</c> at SHLightingMan +0xC4 (12 hulls max).
/// We refuse early so the caller has to split into smaller tiles.
/// </summary>
public static class IrradianceDataBuilder
{
    /// <summary>Max probes per single IrradianceData asset (engine SpatialGrid 41×41).</summary>
    public const int MaxProbesPerAsset = 1681;

    private const int HeaderSize     = 16;
    private const int ProbeStride    = Probe.OnDiskSize;
    private const int ProbeArrayOff  = HeaderSize;
    private const byte PadFiller     = 0xDE;

    public static byte[] Build(IReadOnlyList<Probe> probes)
    {
        if (probes is null) throw new ArgumentNullException(nameof(probes));
        if (probes.Count > MaxProbesPerAsset)
            throw new ArgumentException(
                $"IrradianceData asset has {probes.Count} probes; engine cap is {MaxProbesPerAsset} (split into smaller tiles).",
                nameof(probes));

        int size = HeaderSize + probes.Count * ProbeStride;
        var buf = new byte[size];
        var s = buf.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), (uint)probes.Count);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), ProbeArrayOff);
        // Python reference fills +0x08..+0x10 with 0xDE; engine doesn't read it.
        for (int i = 0x08; i < 0x10; i++) s[i] = PadFiller;

        int cur = ProbeArrayOff;
        for (int i = 0; i < probes.Count; i++)
        {
            WriteProbe(s.Slice(cur, ProbeStride), probes[i]);
            cur += ProbeStride;
        }

        return buf;
    }

    private static void WriteProbe(Span<byte> dst, Probe p)
    {
        if (p.ShR.Length != Probe.ShBandCount ||
            p.ShG.Length != Probe.ShBandCount ||
            p.ShB.Length != Probe.ShBandCount)
            throw new ArgumentException($"Probe SH arrays must each have {Probe.ShBandCount} entries.");

        for (int band = 0; band < Probe.ShBandCount; band++)
        {
            int off = band * 16;
            BinaryPrimitives.WriteSingleBigEndian(dst.Slice(off + 0,  4), p.ShR[band]);
            BinaryPrimitives.WriteSingleBigEndian(dst.Slice(off + 4,  4), p.ShG[band]);
            BinaryPrimitives.WriteSingleBigEndian(dst.Slice(off + 8,  4), p.ShB[band]);
            BinaryPrimitives.WriteSingleBigEndian(dst.Slice(off + 12, 4), 0f);
        }
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(144, 4), p.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(148, 4), p.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(152, 4), p.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(156, 4), 0f);
    }
}
