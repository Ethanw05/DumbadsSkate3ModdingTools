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
    /// Retail Skate 3 areas store a non-zero <c>GRAPH_INDEX</c> in flags3 (bits 16–29), commonly 512 or 768.
    /// Some loaders may consult on-disk flags before NavGraph fully patches runtime state; keep parity with retail.
    /// </summary>
    internal const uint RetailAreaFlags3GraphIndex = 512u << 16;

    internal static uint PointerSize32 => 0u;
}
