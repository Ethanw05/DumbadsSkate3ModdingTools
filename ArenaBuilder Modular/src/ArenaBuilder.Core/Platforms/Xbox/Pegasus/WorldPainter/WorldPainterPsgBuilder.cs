using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Platforms.Common.Pegasus.WorldPainter;
using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.WorldPainter;

/// <summary>
/// Xbox 360 sibling of <c>ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter.WorldPainterPsgBuilder</c>.
/// Composes the same WPQUAD/WPDICT/WPLAYER object stack — those builders are cross-platform clean
/// (docs/X360_Port_Deltas.md §7) — then writes the arena via <see cref="XboxArenaWriter.Write"/>
/// instead of the PS3 GenericArenaWriter.
///
/// API mirrors the PS3 variant for drop-in substitution. See PS3 version for option semantics.
/// </summary>
public static class WorldPainterPsgBuilder
{
    public readonly record struct WorldPainterLayerSeed(ulong LayerGuid, uint[] DictionaryValues);
    public readonly record struct WorldPainterLayerTreeSpec(
        ulong LayerGuid,
        IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> QuadNodes,
        IReadOnlyList<IReadOnlyList<uint>> DictionarySlots);

    public sealed record WorldPainterPsgBuildOptions
    {
        public uint ArenaId { get; init; } = 0x57504D49;
        public float RootCenterX { get; init; } = -64f;
        public float RootCenterY { get; init; } = 192f;
        public float RootHalfX { get; init; } = 64f;
        public float RootHalfY { get; init; } = 64f;
        public IReadOnlyList<WorldPainterLayerSeed>? Layers { get; init; }
        public IReadOnlyList<WorldPainterLayerTreeSpec>? LayerTrees { get; init; }
        public bool OmitDefaultLayerSeedFallback { get; init; }
        public bool OmitUnpaintedDefaultLayerSeeds { get; init; }
        public bool UseArenaEncodedDictionaryRefs { get; init; }
        public string? TocGuidSalt { get; init; }
    }

    public static readonly WorldPainterLayerSeed[] DefaultLayers =
    {
        new(0xEA754449D4731193, new[] { 0x4911C800u, 0x503E77E5u }),
        new(0x6879AFCFA737F03A, new[] { 0xE0FE2167u, 0x9339F7B7u }),
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
        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), RwTypeIds.VersionData));

        var tocEntries = new List<PsgTocEntry>((treeLayers?.Count ?? 0) + seedLayers.Count);

        if (treeLayers != null && treeLayers.Count > 0)
        {
            foreach (var layer in treeLayers)
                AppendLayerObjects(options, objects, tocEntries, layer.LayerGuid, layer.QuadNodes, layer.DictionarySlots);

            if (!options.OmitUnpaintedDefaultLayerSeeds)
            {
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

        objects.Add(new PsgObjectSpec(
            WorldPainterTocBuilder.Build(tocEntries),
            RwTypeIds.TableOfContents));

        return new PsgArenaSpec
        {
            ArenaId = options.ArenaId,
            Objects = objects,
            TypeRegistry = PegasusRwConstants.CollisionTypeRegistry64,
            Toc = new PsgTocSpec { Entries = tocEntries, TypeMap = null },
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
        GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Xbox360, fullPath);
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
                    $"Layer 0x{layerGuid:X16} WPDICT slot {si} must contain at least two UInt32 values (Lookup8 lo/hi).");
        }

        int slotCount = dictionarySlots.Count;
        if (slotCount > WorldPainterQuadTreeValidator.MaxDictionarySlotCount)
            throw new InvalidOperationException(
                $"Layer 0x{layerGuid:X16} has {slotCount} WPDICT slots; max {WorldPainterQuadTreeValidator.MaxDictionarySlotCount}.");

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
        int idx = Array.IndexOf(PegasusRwConstants.CollisionTypeRegistry64, typeId);
        if (idx < 0)
            throw new InvalidOperationException(
                $"TypeId 0x{typeId:X8} is not in PegasusRwConstants.CollisionTypeRegistry64.");
        return idx;
    }

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
