using ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.Irradiance;

/// <summary>
/// Public alias used by build pipelines for the X360 "Probe" (Irradiance) PSG composer.
/// IrradianceData / IrradianceProbeData are cross-platform clean
/// (docs/X360_Port_Deltas.md §7); this type is a thin wrapper over
/// <see cref="IrradiancePsgBuilder"/> so callers searching for "Probe" + "RX2" find it.
/// </summary>
public static class ProbeRX2Composer
{
    public const uint DefaultArenaId = IrradiancePsgBuilder.DefaultArenaId;

    /// <summary>Build an X360 .rx2 byte stream containing N probes + TOC.</summary>
    public static byte[] Build(
        IReadOnlyList<Probe> probes,
        ulong tocAssetGuid,
        uint arenaId = DefaultArenaId)
        => IrradiancePsgBuilder.Build(probes, tocAssetGuid, arenaId);
}
