namespace ArenaBuilder.NavPower;

/// <summary>On-disk sizes and magic values matching Skate-era NavPower + BabelFlux (see documentation/PSG_NavPower_Binary_Layout.md).</summary>
internal static class NavPowerBinaryConstants
{
    internal const uint EndianBig = 0xFFFFFFFFu;
    internal const uint ResourceHeaderVersion = 2;
    internal const uint NavGraphVersionSkate = 23;
    internal const uint NavSetVersionSkate = 23;

    /// <summary>SurfacePlanner + type 0 → 0x00010000 (bfxComponents.h).</summary>
    internal const uint SectionIdSurfacePlanner = 0x00010000u;

    internal const int ResourceHeaderBytes = 24;
    internal const int ResourceSectionHeaderBytes = 12;
    internal const int NavSetHeaderBytes = 12;
    internal const int LegacyNavGraphHeaderBytes = 312;
    internal const int PegasusNavPowerPrefixBytes = 64;
    internal const int AreaBaseLegacyBytes = 52;
    internal const int EdgeBytes32 = 24;

    /// <summary>Retail Skate static areas use a closed polygon; BFX iterators assume at least one edge ring (see DIST_University parses).</summary>
    internal const int LegacyClosedLoopEdgeCount = 3;
    internal const int NavGraphPadBytes = 252;

    internal const uint KdLeafMask = 0x80000000u;
    internal const uint KdAxisMask = 0x70000000u;
    internal const int KdAxisShift = 28;
    internal const uint KdRightOffsetMask = 0x0FFFFFFFu;
    internal const uint KdPrimOffsetMask = 0x7FFFFFFFu;

    internal const int KdNodeBytes = 12;
    internal const int KdLeafBytes = 4;
    internal const int KdTreeDataPrefixBytes = 28;

    /// <summary>
    /// Skate 3 v23 NavPower repurposes flags3 upper bits as BASIS_VERT (bits 24–30), NOT GRAPH_INDEX as
    /// in modern (v26+) NavPower SDK headers. EBOOT path-planner (<c>sub_9B9F88 @ 0x9B9F88</c>) reads
    /// <c>(flags3 &gt;&gt; 24) &amp; 0x7F</c> as an edge index for surface-normal calculation. Stock
    /// DIST_Industrial varies basis 2–8 per polygon; hardcoding 512 (basis=2) only works for triangles
    /// and produces a flipped surface normal on every quad/poly, which makes pedestrian spawn probes
    /// shoot the wrong direction and rejected spawns leak BFX planner expansion nodes.
    /// </summary>
    internal const int Flags3BasisVertShift = 24;
    internal const uint Flags3BasisVertMask = 0x7F000000u;

    internal static uint PointerSize32 => 0u;
}
