using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;

/// <summary>
/// Builds minimal WorldPainter PSG files containing VersionData + (WPQUAD/WPDICT/WPLAYER) layer sets.
/// No intermediate JSON is required.
/// </summary>
public static class WorldPainterPsgBuilder
{
    /// <summary>
    /// Full 64-entry type registry used by game PSGs (mesh/collision compatible order).
    /// Keeping this order preserves dictionary type_index compatibility.
    /// </summary>
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

    public readonly record struct WorldPainterLayerSeed(ulong LayerGuid, uint[] DictionaryValues);
    public readonly record struct WorldPainterLayerTreeSpec(
        ulong LayerGuid,
        IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> QuadNodes,
        IReadOnlyList<IReadOnlyList<uint>> DictionarySlots);

    public sealed record WorldPainterPsgBuildOptions
    {
        public uint ArenaId { get; init; } = 0x57504D49; // "WPMI"
        public float RootCenterX { get; init; } = -64f;
        public float RootCenterY { get; init; } = 192f;
        public float RootHalfX { get; init; } = 64f;
        public float RootHalfY { get; init; } = 64f;
        public IReadOnlyList<WorldPainterLayerSeed>? Layers { get; init; }
        public IReadOnlyList<WorldPainterLayerTreeSpec>? LayerTrees { get; init; }

        /// <summary>
        /// When true, do not substitute <see cref="DefaultLayers"/> if <see cref="LayerTrees"/> is null or empty.
        /// Used when baking from <c>worldpainter_paint.json</c> so tiles outside the painted AABB emit an empty WP stack
        /// instead of silently writing hardcoded University/BlackBox seeds.
        /// </summary>
        public bool OmitDefaultLayerSeedFallback { get; init; }

        /// <summary>
        /// When true and <see cref="LayerTrees"/> is non-empty, do not append <see cref="DefaultLayers"/> for GUIDs missing from the bake.
        /// Use for retail round-trip tests so the rebuilt arena contains only extracted layers.
        /// </summary>
        public bool OmitUnpaintedDefaultLayerSeeds { get; init; }

        /// <summary>
        /// When true, WPLAYER quad/dict refs and TOC <c>m_pObject</c> use packed arena dictionary refs per
        /// <c>pegasus::tWorldPainterLayerData::Fixup</c> (see <see cref="ArenaDictionaryEncodedPointer"/>).
        /// Default is false: write raw global dict indices, which matches RPCS3/Skate behavior observed with minimal tile PSGs
        /// (encoded rows can instant-crash if the runtime type map does not match <see cref="TypeRegistry64"/> layout).
        /// </summary>
        public bool UseArenaEncodedDictionaryRefs { get; init; }

        /// <summary>
        /// Per-tile salt mixed into TOC asset GUID generation so that every PSG file produces unique
        /// GUIDs even when <see cref="ArenaId"/>, layer GUIDs, and dict indices are identical.
        /// Set this to a value unique per output file (e.g. tile key string).
        /// When null/empty, falls back to <see cref="ArenaId"/> only (legacy behavior — causes GUID
        /// collisions when multiple WP PSGs share the same spec-level ArenaId).
        /// </summary>
        public string? TocGuidSalt { get; init; }
    }

    /// <summary>
    /// Default minimal WorldPainter stack for local testing: University-style audio_ambience region
    /// (univ_mt_high Lookup8 halves), BlackBox reverb + district_locations donors unchanged.
    /// </summary>
    public static readonly WorldPainterLayerSeed[] DefaultLayers =
    {
        // audio_ambience — DIST_University univ_mt_high (low u32, high u32) per stream parse / Lookup8
        new(0xEA754449D4731193, new[] { 0x4911C800u, 0x503E77E5u }),
        // audio_reverb — DIST_BlackBoxPark reverb17
        new(0x6879AFCFA737F03A, new[] { 0xE0FE2167u, 0x9339F7B7u }),
        // district_locations — DIST_BlackBoxPark blackbox_skatepark
        new(0xD5B9AC56592787A1, new[] { 0x3D423EA0u, 0x2CC4C454u }),
    };

    public static PsgArenaSpec ComposeMinimal(WorldPainterPsgBuildOptions? options = null)
    {
        options ??= new WorldPainterPsgBuildOptions();
        IReadOnlyList<WorldPainterLayerTreeSpec>? treeLayers = options.LayerTrees;
        bool hasTrees = treeLayers != null && treeLayers.Count > 0;
        IReadOnlyList<WorldPainterLayerSeed> seedLayers = hasTrees
            ? Array.Empty<WorldPainterLayerSeed>()
            : (options.OmitDefaultLayerSeedFallback
                ? (options.Layers ?? Array.Empty<WorldPainterLayerSeed>())
                : (options.Layers ?? DefaultLayers));

        if (!hasTrees && seedLayers.Count == 0 && !options.OmitDefaultLayerSeedFallback)
            throw new InvalidOperationException("At least one WorldPainter layer seed/tree is required.");

        var objects = new List<PsgObjectSpec>();

        // Keep a non-worldpainter object at dict index 0. WPLAYER/TOC links: raw dict indices by default; optional encoded refs (see options).
        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData));

        var tocEntries = new List<PsgTocEntry>((treeLayers?.Count ?? 0) + seedLayers.Count);

        if (treeLayers != null && treeLayers.Count > 0)
        {
            foreach (var layer in treeLayers)
                AppendLayerObjects(options, objects, tocEntries, layer.LayerGuid, layer.QuadNodes, layer.DictionarySlots);

            if (!options.OmitUnpaintedDefaultLayerSeeds)
            {
                // worldpainter_paint.json usually defines audio_ambience only. Retail tile PSGs still register
                // additional layer GUIDs (reverb, district_locations, …). Emitting paint alone can leave the
                // runtime audio stack incomplete even when the quadtree matches JSON — add single-leaf defaults
                // for every DefaultLayers GUID not already covered by the baked trees.
                var paintedGuids = new HashSet<ulong>();
                foreach (var t in treeLayers)
                    paintedGuids.Add(t.LayerGuid);
                foreach (var seed in DefaultLayers)
                {
                    if (paintedGuids.Contains(seed.LayerGuid))
                        continue;
                    if (seed.DictionaryValues == null || seed.DictionaryValues.Length < 2)
                        continue;
                    AppendLayerObjects(
                        options,
                        objects,
                        tocEntries,
                        seed.LayerGuid,
                        new[] { WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode.Leaf(0) },
                        new[] { (IReadOnlyList<uint>)seed.DictionaryValues });
                }
            }
        }
        else
        {
            foreach (var layer in seedLayers)
            {
                // Runtime resolves Lookup8-style keys from two consecutive UInt32s (lo|hi); see GetAttribData / WPDICT notes in documentation.
                if (layer.DictionaryValues == null || layer.DictionaryValues.Length < 2)
                    throw new InvalidOperationException(
                        $"Layer 0x{layer.LayerGuid:X16} must define at least two dictionary UInt32 values (low Lookup8, high Lookup8).");
                AppendLayerObjects(
                    options,
                    objects,
                    tocEntries,
                    layer.LayerGuid,
                    new[] { WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode.Leaf(0) },
                    new[] { (IReadOnlyList<uint>)layer.DictionaryValues });
            }
        }

        // Add RW TableOfContents object so cross-links resolve through TOC as in real files.
        objects.Add(new PsgObjectSpec(
            WorldPainterTocBuilder.Build(tocEntries),
            RwTypeIds.TableOfContents));

        return new PsgArenaSpec
        {
            ArenaId = options.ArenaId,
            Objects = objects,
            TypeRegistry = TypeRegistry64,
            Toc = new PsgTocSpec
            {
                Entries = tocEntries,
                TypeMap = null
            },
            Subrefs = null,
            HeaderTypeIdAt0x70 = 1,
            UseFileSizeAt0x44 = true,
            DictRelocIsZero = true
        };
    }

    public static void WriteMinimal(string outputPath, WorldPainterPsgBuildOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var spec = ComposeMinimal(options);
        using var fs = File.Create(fullPath);
        GenericArenaWriter.Write(spec, fs, fullPath);
    }

    private static ulong BuildDeterministicTocAssetGuid(uint arenaId, ulong layerGuid, uint objectPtr, string? salt)
    {
        string saltPart = string.IsNullOrEmpty(salt) ? "" : $"_{salt}";
        string seed = FormattableString.Invariant(
            $"worldpainter_toc_asset_{arenaId:X8}_{layerGuid:X16}_{objectPtr:D}{saltPart}");
        ulong guid = Lookup8Hash.HashString(seed);
        return guid == 0 ? 0x1000000000000001ul : guid;
    }

    private static void AppendLayerObjects(
        WorldPainterPsgBuildOptions options,
        List<PsgObjectSpec> objects,
        List<PsgTocEntry> tocEntries,
        ulong layerGuid,
        IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> quadNodes,
        IReadOnlyList<IReadOnlyList<uint>> dictionarySlots)
    {
        if (quadNodes == null || quadNodes.Count == 0)
            throw new InvalidOperationException($"Layer 0x{layerGuid:X16} must define at least one quadtree node.");
        if (dictionarySlots == null || dictionarySlots.Count == 0)
            throw new InvalidOperationException($"Layer 0x{layerGuid:X16} must define at least one dictionary slot.");
        for (int si = 0; si < dictionarySlots.Count; si++)
        {
            if (dictionarySlots[si] == null || dictionarySlots[si].Count < 2)
                throw new InvalidOperationException(
                    $"Layer 0x{layerGuid:X16} WPDICT slot {si} must contain at least two UInt32 values (Lookup8 lo/hi), matching game dictionary entry layout.");
        }

        int slotCount = dictionarySlots.Count;
        if (slotCount > WorldPainterQuadTreeValidator.MaxDictionarySlotCount)
            throw new InvalidOperationException(
                $"Layer 0x{layerGuid:X16} has {slotCount} WPDICT slots; game GetAttribData uses int16 for the lookup index " +
                $"(max {WorldPainterQuadTreeValidator.MaxDictionarySlotCount}).");

        WorldPainterQuadTreeValidator.ValidateOrThrow(quadNodes, slotCount, $"0x{layerGuid:X16}");

        uint quadDictIndex = (uint)objects.Count;
        byte[] quadData = WorldPainterQuadTreeDataBuilder.Build(
            options.RootCenterX,
            options.RootCenterY,
            options.RootHalfX,
            options.RootHalfY,
            quadNodes);
        objects.Add(new PsgObjectSpec(quadData, RwTypeIds.WorldPainterQuadTreeData));

        uint dictDictIndex = (uint)objects.Count;
        byte[] dictData = WorldPainterDictionaryDataBuilder.BuildFromSlots(dictionarySlots);
        objects.Add(new PsgObjectSpec(dictData, RwTypeIds.WorldPainterDictionaryData));

        uint wplayerDictIndex = (uint)objects.Count;
        uint quadRef;
        uint dictRef;
        uint tocObjectPtr;
        if (options.UseArenaEncodedDictionaryRefs)
        {
            int quadOrdinal = CountObjectsOfTypeIdBefore(objects, RwTypeIds.WorldPainterQuadTreeData, (int)quadDictIndex);
            int dictOrdinal = CountObjectsOfTypeIdBefore(objects, RwTypeIds.WorldPainterDictionaryData, (int)dictDictIndex);
            int layerOrdinal = CountObjectsOfTypeIdBefore(objects, RwTypeIds.WorldPainterLayerData, (int)wplayerDictIndex);
            quadRef = ArenaDictionaryEncodedPointer.Encode(GetTypeIndex(RwTypeIds.WorldPainterQuadTreeData), quadOrdinal);
            dictRef = ArenaDictionaryEncodedPointer.Encode(GetTypeIndex(RwTypeIds.WorldPainterDictionaryData), dictOrdinal);
            tocObjectPtr = ArenaDictionaryEncodedPointer.Encode(GetTypeIndex(RwTypeIds.WorldPainterLayerData), layerOrdinal);
        }
        else
        {
            quadRef = quadDictIndex;
            dictRef = dictDictIndex;
            tocObjectPtr = wplayerDictIndex;
        }

        byte[] layerData = WorldPainterLayerDataBuilder.Build(quadRef, dictRef, layerGuid);
        objects.Add(new PsgObjectSpec(layerData, RwTypeIds.WorldPainterLayerData));

        ulong tocAssetGuid = BuildDeterministicTocAssetGuid(options.ArenaId, layerGuid, wplayerDictIndex, options.TocGuidSalt);
        tocEntries.Add(new PsgTocEntry(
            NameOrHash: 0,
            Guid: tocAssetGuid,
            TypeId: RwTypeIds.WorldPainterLayerData,
            ObjectPtr: tocObjectPtr));
    }

    private static int GetTypeIndex(uint typeId)
    {
        int idx = Array.IndexOf(TypeRegistry64, typeId);
        if (idx < 0)
            throw new InvalidOperationException(
                $"TypeId 0x{typeId:X8} is not in WorldPainterPsgBuilder.TypeRegistry64.");
        return idx;
    }

    /// <summary>Count objects of <paramref name="typeId"/> among indices strictly less than <paramref name="beforeIndex"/>.</summary>
    private static int CountObjectsOfTypeIdBefore(List<PsgObjectSpec> objects, uint typeId, int beforeIndex)
    {
        int n = 0;
        int cap = Math.Min(beforeIndex, objects.Count);
        for (int i = 0; i < cap; i++)
        {
            if (objects[i].TypeId == typeId)
                n++;
        }
        return n;
    }
}
