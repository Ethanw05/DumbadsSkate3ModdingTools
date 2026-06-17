using XboxWPBuilder = ArenaBuilder.Core.Platforms.Xbox.Pegasus.WorldPainter.WorldPainterPsgBuilder;

namespace ArenaBuilder.WorldPainter;

/// <summary>
/// Xbox 360 (.rx2) sibling of <see cref="WorldPainterExporter"/>. Thin project-level wrapper
/// over <see cref="XboxWPBuilder"/> in Core. WorldPainter object types
/// (QuadTree / Dictionary / Layer) are cross-platform clean
/// (docs/X360_Port_Deltas.md §7); the only platform delta is the arena writer.
/// </summary>
public static class WorldPainterRX2Composer
{
    public sealed record Options
    {
        public uint ArenaId { get; init; } = 0x57504D49; // "WPMI"
        public float RootCenterX { get; init; } = -64f;
        public float RootCenterY { get; init; } = 192f;
        public float RootHalfX { get; init; } = 64f;
        public float RootHalfY { get; init; } = 64f;
        public IReadOnlyList<XboxWPBuilder.WorldPainterLayerSeed>? Layers { get; init; }
        public IReadOnlyList<XboxWPBuilder.WorldPainterLayerTreeSpec>? LayerTrees { get; init; }
        public bool OmitDefaultLayerSeedFallback { get; init; }
        public bool OmitUnpaintedDefaultLayerSeeds { get; init; }
        public bool UseArenaEncodedDictionaryRefs { get; init; }
        public string? TocGuidSalt { get; init; }
    }

    /// <summary>Writes a minimal-shape X360 WorldPainter .rx2 to <paramref name="outputPath"/>.</summary>
    public static void WriteMinimal(string outputPath, Options? options = null)
    {
        options ??= new Options();
        var coreOptions = new XboxWPBuilder.WorldPainterPsgBuildOptions
        {
            ArenaId = options.ArenaId,
            RootCenterX = options.RootCenterX,
            RootCenterY = options.RootCenterY,
            RootHalfX = options.RootHalfX,
            RootHalfY = options.RootHalfY,
            Layers = options.Layers,
            LayerTrees = options.LayerTrees,
            OmitDefaultLayerSeedFallback = options.OmitDefaultLayerSeedFallback,
            OmitUnpaintedDefaultLayerSeeds = options.OmitUnpaintedDefaultLayerSeeds,
            UseArenaEncodedDictionaryRefs = options.UseArenaEncodedDictionaryRefs,
            TocGuidSalt = options.TocGuidSalt
        };
        XboxWPBuilder.WriteMinimal(outputPath, coreOptions);
    }
}
