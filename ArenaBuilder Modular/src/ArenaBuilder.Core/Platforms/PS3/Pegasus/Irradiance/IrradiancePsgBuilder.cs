using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

using ArenaBuilder.Core.Platforms.Common;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Irradiance;

/// <summary>
/// Wraps an IrradianceData blob into a complete RW4 PS3 arena PSG via
/// <see cref="GenericArenaWriter.Write"/>. Three objects in dict order:
///   0: VersionData         (RwTypeIds.VersionData,     16 B)
///   1: IrradianceData      (RwTypeIds.IrradianceData,  variable)
///   2: TableOfContents     (RwTypeIds.TableOfContents, 244 B)
///
/// Same arena/section shape as <see cref="AIPath.AiPathPsgBuilder"/>: mesh-style
/// 0x180-byte sections, file size at header +0x44, no subref records, header
/// type id 1.
/// </summary>
public static class IrradiancePsgBuilder
{
    /// <summary>64-entry SectionTypes registry shared by every PSG asset class.</summary>
    private static readonly uint[] TypeRegistry64 =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033, 0x00010034,
        0x00010010, 0x00EB0000, 0x00EB0001, 0x00EB0003, 0x00EB0004, 0x00EB0005,
        0x00EB0006, 0x00EB000A, 0x00EB000D, 0x00EB0019, 0x00EB0007, 0x00EB0008,
        0x00EB000C, 0x00EB0009, 0x00EB000B, 0x00EB000E, 0x00EB0011, 0x00EB000F,
        0x00EB0010, 0x00EB0012, 0x00EB0022, 0x00EB0013, 0x00EB0014, 0x00EB0015,
        0x00EB0016, 0x00EB001A, 0x00EB001C, 0x00EB001D, 0x00EB001B, 0x00EB001E,
        0x00EB001F, 0x00EB0021, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0023,
        0x00EB0025, 0x00EB0026, 0x00EB0027, 0x00EB0028, 0x00EB0029, 0x00EB0018,
        0x00EC0010, 0x00010000, 0x00010002, 0x000200EB, 0x000200EA, 0x000200E9,
        0x00020081, 0x000200E8, 0x00080002, 0x00080001, 0x00080006, 0x00080003,
        0x00080004, 0x00040006, 0x00040007, 0x0001000F
    };

    public const uint DefaultArenaId = 0x00000001;

    public static byte[] Build(IReadOnlyList<Probe> probes, ulong tocAssetGuid,
                               uint arenaId = DefaultArenaId, ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        if (probes is null) throw new ArgumentNullException(nameof(probes));
        if (tocAssetGuid == 0)
            throw new ArgumentException("TOC asset GUID must be non-zero", nameof(tocAssetGuid));

        byte[] irradiance = IrradianceDataBuilder.Build(probes);
        byte[] toc        = IrradianceTocBuilder.Build(tocAssetGuid);

        var objects = new List<PsgObjectSpec>
        {
            new(VersionDataBuilder.Build(), RwTypeIds.VersionData),     // dict 0
            new(irradiance,                 RwTypeIds.IrradianceData),  // dict 1
            new(toc,                        RwTypeIds.TableOfContents)  // dict 2
        };

        var spec = new PsgArenaSpec
        {
            ArenaId      = arenaId,
            Objects      = objects,
            TypeRegistry = TypeRegistry64,
            Toc          = new PsgTocSpec
            {
                Entries = Array.Empty<PsgTocEntry>(),
                TypeMap = null
            },
            Subrefs                 = null,
            HeaderTypeIdAt0x70      = 1,
            UseFileSizeAt0x44       = true,
            DictRelocIsZero         = true
        };

        using var ms = new MemoryStream();
        GeneralArenaBuilder.Write(spec, ms, platform, "Irradiance");
        return ms.ToArray();
    }
}
