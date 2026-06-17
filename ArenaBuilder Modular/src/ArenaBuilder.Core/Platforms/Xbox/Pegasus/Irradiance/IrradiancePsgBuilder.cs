using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.Irradiance;

/// <summary>
/// Xbox 360 sibling of <c>ArenaBuilder.Core.Platforms.PS3.Pegasus.Irradiance.IrradiancePsgBuilder</c>.
/// Wraps IrradianceData in a complete .rx2 arena via <see cref="XboxArenaWriter.Write"/>.
///
/// Three objects in dict order: VersionData, IrradianceData, TableOfContents.
/// IrradianceData is cross-platform clean (docs/X360_Port_Deltas.md §7).
/// </summary>
public static class IrradiancePsgBuilder
{
    public const uint DefaultArenaId = 0x00000001;

    public static byte[] Build(IReadOnlyList<Probe> probes, ulong tocAssetGuid,
                               uint arenaId = DefaultArenaId)
    {
        if (probes is null) throw new ArgumentNullException(nameof(probes));
        if (tocAssetGuid == 0)
            throw new ArgumentException("TOC asset GUID must be non-zero", nameof(tocAssetGuid));

        byte[] irradiance = IrradianceDataBuilder.Build(probes);
        byte[] toc        = IrradianceTocBuilder.Build(tocAssetGuid);

        var objects = new List<PsgObjectSpec>
        {
            new(VersionDataBuilder.Build(), RwTypeIds.VersionData),
            new(irradiance,                 RwTypeIds.IrradianceData),
            new(toc,                        RwTypeIds.TableOfContents)
        };

        var spec = new PsgArenaSpec
        {
            ArenaId            = arenaId,
            Objects            = objects,
            TypeRegistry       = PegasusRwConstants.CollisionTypeRegistry64,
            Toc                = new PsgTocSpec { Entries = Array.Empty<PsgTocEntry>(), TypeMap = null },
            Subrefs            = null,
            HeaderTypeIdAt0x70 = 1,
            UseFileSizeAt0x44  = true,
            DictRelocIsZero    = true
        };

        using var ms = new MemoryStream();
        GeneralArenaBuilder.Write(spec, ms, ArenaPlatform.Xbox360, "Irradiance");
        return ms.ToArray();
    }
}
