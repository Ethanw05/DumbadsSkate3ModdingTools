using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;
using System.Text;

using ArenaBuilder.Core.Platforms.PS3;

using ArenaBuilder.Core.Platforms.Common;

namespace ArenaBuilder.Cli.Commands;

/// <summary>
/// Walks a stream-root directory, parses every <c>.psg</c> in every <c>cPres_*</c>/<c>cSim_*</c>
/// tile folder, and produces a cross-reference of which texture/asset GUIDs are owned by which
/// folder vs. referenced by which mesh.
///
/// <para><b>Parser scope</b></para>
/// <list type="bullet">
///   <item>PSG dictionary via <see cref="PsgBinary"/> (header at 0xC0, dict pointed at by header+0x30,
///         24-byte entries).</item>
///   <item>Per-PSG TableOfContents (TypeId 0x00EB000B): entries are 0x18 bytes,
///         <c>(NameOrHash:u32, marker=0x9B0F1678:u32, Guid:u64, TocType:u32, ObjectPtr:u32)</c>.
///         These are the GUIDs that the engine's hash table registers when this PSG loads.</item>
///   <item>Per-PSG RenderMaterialData (TypeId 0x00EB0005): header (0x14) + N×0x0C materials +
///         M×0x20 channels. Each channel's <c>+0x10</c> is either a 64-bit cross-file GUID or
///         four floats (when flags &amp; 0x2 == KScalarConstant). The non-scalar GUIDs are the
///         cross-file references this mesh resolves at material-binding time.</item>
/// </list>
/// </summary>
internal static class PsgAnalyzeStreamCommand
{
    private const uint TypeIdTableOfContents = 0x00EB000B;
    private const uint TypeIdRenderMaterialData = 0x00EB0005;
    private const uint TypeIdTexture = 0x000200E8;
    private const uint TypeIdCollisionModelData = 0x00EB000A;
    private const uint TypeIdWorldPainterDictionaryData = 0x00EB0011;
    private const uint TypeIdInstanceData = 0x00EB000D;

    /// <summary>
    /// Single canonical TOC entry marker, written by <c>DynamicTocBuilder</c>/<c>TextureTocBuilder</c>
    /// at <c>+0x04</c> of every 0x18-byte entry. Same value used by mesh, texture, collision, and
    /// WorldPainter TOCs. Entries that don't carry this marker are not cross-file TOC entries.
    /// </summary>
    private const uint TocEntryMarker = 0x9B0F1678;

    /// <summary>TOC entry type ID assigned to texture cross-file refs (from TexturePsgConstants).</summary>
    private const uint TocTypeTexture = 0xAC462E4A;

    /// <summary>RenderMaterialData channel flag bit indicating the +0x10 union holds 4 floats, not a GUID.</summary>
    private const ushort ChannelFlagScalarConstant = 0x0002;

    public static int Run(string[] args)
    {
        if (args.Length < 1)
            return CliErrors.Fail("Usage: psg-analyze-stream <stream-root-dir> [--out-dir=<path>] [--samples=<N>]");

        string root = Path.GetFullPath(args[0]);
        string? outDir = GetOptionValue(args, "--out-dir=");
        int sampleCount = int.TryParse(GetOptionValue(args, "--samples="), out int s) ? s : 5;

        if (!Directory.Exists(root))
            return CliErrors.Fail($"Stream root not found: {root}");

        Console.WriteLine($"Stream root: {root}");
        Console.WriteLine();

        var psgFiles = CollectPsgFiles(root).ToList();
        Console.WriteLine($"Found {psgFiles.Count} .psg files.");

        var infos = new List<PsgFileInfo>(psgFiles.Count);
        int parseFailures = 0;
        foreach (var path in psgFiles)
        {
            try
            {
                infos.Add(ParsePsg(path, root));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  parse failed: {Path.GetRelativePath(root, path)}: {ex.GetType().Name}: {ex.Message}");
                parseFailures++;
            }
        }
        Console.WriteLine($"Parsed {infos.Count} PSGs ({parseFailures} failed).");
        Console.WriteLine();

        var report = BuildReport(infos);
        PrintReport(report, infos, sampleCount);

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            WriteCsvDump(outDir, infos, report);
            Console.WriteLine();
            Console.WriteLine($"Wrote CSV dumps to: {Path.GetFullPath(outDir)}");
        }

        return 0;
    }

    // ───────────────────────────────────────────────────────────────────────
    // Discovery
    // ───────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> CollectPsgFiles(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root, "*.psg", SearchOption.TopDirectoryOnly))
            yield return f;

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            foreach (var f in Directory.EnumerateFiles(dir, "*.psg", SearchOption.TopDirectoryOnly))
                yield return f;
    }

    // ───────────────────────────────────────────────────────────────────────
    // Parsing
    // ───────────────────────────────────────────────────────────────────────

    private sealed record PsgTocEntryView(
        uint NameOrHash,
        ulong Guid,
        uint TocType,
        uint ObjectPtr);

    private sealed record ChannelRef(
        int MaterialIndex,
        int ChannelIndex,
        string ChannelName,
        ushort Flags,
        ushort ImageChannel,
        bool IsScalar,
        ulong Guid,
        string StreamName);

    /// <summary>
    /// One physical texture image inside a texture PSG. Read straight off the 40-byte
    /// TextureInformationPS3 object at <see cref="TypeIdTexture"/> per
    /// <c>TextureRwBuilder.Build</c> (format@0, mip@1, dim@2, w@8(BE u16), h@10(BE u16),
    /// pitch@16, storeType@28, format2@39).
    /// </summary>
    private sealed record TextureInfoView(
        byte Ps3Format,
        byte MipCount,
        ushort Width,
        ushort Height,
        uint Pitch);

    private sealed class PsgFileInfo
    {
        public required string AbsolutePath { get; init; }
        public required string FolderName { get; init; }            // "cPres_50_50_high"
        public required string StreamCategory { get; init; }        // "cPres" / "cSim" / "Other"
        public required string FileName { get; init; }              // "ABCD....psg"
        public required long FileSize { get; init; }
        public required IReadOnlyDictionary<uint, int> TypeCounts { get; init; }
        public required string Kind { get; init; }                  // "Mesh" / "Texture" / "Collision" / "WorldPainter" / "Mixed" / "Other"
        public required IReadOnlyList<PsgTocEntryView> TocEntries { get; init; }
        public required IReadOnlyList<ChannelRef> ChannelRefs { get; init; }
        public required IReadOnlyList<TextureInfoView> Textures { get; init; }
    }

    private static PsgFileInfo ParsePsg(string path, string root)
    {
        var bytes = File.ReadAllBytes(path);
        var psg = PsgBinary.Parse(bytes);

        var typeCounts = new Dictionary<uint, int>();
        foreach (var o in psg.Objects)
            typeCounts[o.TypeId] = typeCounts.TryGetValue(o.TypeId, out int c) ? c + 1 : 1;

        var tocEntries = ExtractTocEntries(bytes, psg);
        var channelRefs = ExtractChannelRefs(bytes, psg);
        var textures = ExtractTextureInfos(bytes, psg);

        // PSG kind: classified by which "leading" data type it contains.
        // Ordering matters because some PSGs have multiple — e.g. a mesh PSG also has TableOfContents.
        string kind;
        if (typeCounts.ContainsKey(TypeIdRenderMaterialData)) kind = "Mesh";
        else if (typeCounts.ContainsKey(TypeIdTexture)) kind = "Texture";
        else if (typeCounts.ContainsKey(TypeIdCollisionModelData)) kind = "Collision";
        else if (typeCounts.ContainsKey(TypeIdWorldPainterDictionaryData)) kind = "WorldPainter";
        else if (typeCounts.ContainsKey(TypeIdInstanceData)) kind = "Instance";
        else kind = "Other";

        string folderRel = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        string folderName = string.IsNullOrEmpty(folderRel) || folderRel == "." ? "<root>" : folderRel;
        string streamCategory = ClassifyStreamCategory(folderName);

        return new PsgFileInfo
        {
            AbsolutePath = path,
            FolderName = folderName,
            StreamCategory = streamCategory,
            FileName = Path.GetFileName(path),
            FileSize = bytes.Length,
            TypeCounts = typeCounts,
            Kind = kind,
            TocEntries = tocEntries,
            ChannelRefs = channelRefs,
            Textures = textures,
        };
    }

    private static IReadOnlyList<TextureInfoView> ExtractTextureInfos(byte[] bytes, PsgBinary psg)
    {
        var result = new List<TextureInfoView>();
        foreach (var obj in psg.Objects)
        {
            if (obj.TypeId != TypeIdTexture) continue;
            if (obj.Size < 0x14) continue;
            if (obj.Ptr < 0 || obj.Ptr + 0x14 > bytes.Length) continue;

            var s = bytes.AsSpan(obj.Ptr, Math.Min(obj.Size, 40));
            byte format = s[0];
            byte mips = s[1];
            ushort w = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(8, 2));
            ushort h = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(10, 2));
            uint pitch = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(16, 4));
            result.Add(new TextureInfoView(format, mips, w, h, pitch));
        }
        return result;
    }

    private static string ClassifyStreamCategory(string folderName)
    {
        if (folderName.StartsWith("cPres", StringComparison.OrdinalIgnoreCase)) return "cPres";
        if (folderName.StartsWith("cSim", StringComparison.OrdinalIgnoreCase)) return "cSim";
        return "Other";
    }

    private static IReadOnlyList<PsgTocEntryView> ExtractTocEntries(byte[] bytes, PsgBinary psg)
    {
        // ONE canonical TOC entry layout — exactly what DynamicTocBuilder/TextureTocBuilder writes
        // and what the Blender importer's "builder" path reads:
        //   +0x00  m_Name      (u32)   — string offset within names blob (0 if no name)
        //   +0x04  marker      (u32)   = 0x9B0F1678
        //   +0x08  m_uiGuid    (u64)   — cross-file asset GUID
        //   +0x10  m_uiType    (u32)   — RW type id (e.g. Texture 0xAC462E4A)
        //   +0x14  m_pObject   (u32)   — pointer/dict-idx into containing PSG
        // Entries whose +0x04 word is not the marker are not cross-file TOC entries and are skipped.
        // (Mesh PSGs in stock content carry only local material/instance subref entries that don't
        //  use this marker; cross-file texture references for meshes live in RenderMaterialData
        //  channel GUIDs, parsed separately in ExtractChannelRefs.)
        var result = new List<PsgTocEntryView>();
        foreach (var obj in psg.Objects)
        {
            if (obj.TypeId != TypeIdTableOfContents) continue;
            if (obj.Size < 0x14) continue;
            if (obj.Ptr < 0 || obj.Ptr + obj.Size > bytes.Length) continue;

            var s = bytes.AsSpan(obj.Ptr, obj.Size);

            uint count = U32(s, 0);
            uint arrayOff = U32(s, 4);

            const int entrySize = 0x18;
            if (count > 4096) continue; // sanity guard
            if (arrayOff + count * entrySize > (uint)obj.Size) continue;

            for (int i = 0; i < (int)count; i++)
            {
                int eo = (int)arrayOff + i * entrySize;

                uint marker = U32(s, eo + 4);
                if (marker != TocEntryMarker) continue;

                uint nameOrHash = U32(s, eo + 0x00);
                ulong guid = U64(s, eo + 0x08);
                uint tocType = U32(s, eo + 0x10);
                uint objPtr = U32(s, eo + 0x14);
                result.Add(new PsgTocEntryView(nameOrHash, guid, tocType, objPtr));
            }
        }
        return result;
    }

    private static IReadOnlyList<ChannelRef> ExtractChannelRefs(byte[] bytes, PsgBinary psg)
    {
        var result = new List<ChannelRef>();
        foreach (var obj in psg.Objects)
        {
            if (obj.TypeId != TypeIdRenderMaterialData) continue;
            if (obj.Size < 0x14) continue;
            if (obj.Ptr < 0 || obj.Ptr + obj.Size > bytes.Length) continue;

            var s = bytes.AsSpan(obj.Ptr, obj.Size);

            uint numMaterials = U32(s, 0);
            uint numChannels = U32(s, 4);
            uint materialsOff = U32(s, 8);
            uint channelsOff = U32(s, 12);

            if (numMaterials > 256 || numChannels > 4096) continue;
            if (materialsOff + numMaterials * 0x0C > (uint)obj.Size) continue;
            if (channelsOff + numChannels * 0x20 > (uint)obj.Size) continue;

            // For each material, walk its (numMatChannels) starting at the material's channelsPtr.
            // ChannelsPtr is a relative offset from RenderMaterialData base (already +stringListOffset
            // adjusted for the channel name when it was written; for OUR purposes here, channelsPtr
            // points to the material's first channel within `s`).
            uint channelCursor = 0;
            for (int m = 0; m < (int)numMaterials; m++)
            {
                int mo = (int)materialsOff + m * 0x0C;
                uint matNumChannels = U32(s, mo + 0);
                // uint matFlags = U32(s, mo + 4);
                uint matChannelsPtr = U32(s, mo + 8);

                // matChannelsPtr should equal channelsOff + channelCursor * 0x20 in well-formed files.
                // Use it directly (with bounds check) so we tolerate any deviation.
                if (matChannelsPtr + matNumChannels * 0x20 > (uint)obj.Size) continue;

                for (int c = 0; c < (int)matNumChannels; c++)
                {
                    int co = (int)matChannelsPtr + c * 0x20;
                    uint shaderInputOff = U32(s, co + 0);
                    ushort flags = U16(s, co + 4);
                    ushort imageChannel = U16(s, co + 6);
                    bool isScalar = (flags & ChannelFlagScalarConstant) != 0;

                    string channelName = ReadCStringSafe(s, shaderInputOff, 64);

                    ulong guid = 0;
                    string streamName = "";
                    if (!isScalar)
                    {
                        guid = U64(s, co + 0x10);
                        uint streamNameOff = U32(s, co + 0x18);
                        if (streamNameOff != 0 && streamNameOff < (uint)obj.Size)
                            streamName = ReadCStringSafe(s, streamNameOff, 128);
                    }

                    result.Add(new ChannelRef(
                        MaterialIndex: m,
                        ChannelIndex: c,
                        ChannelName: channelName,
                        Flags: flags,
                        ImageChannel: imageChannel,
                        IsScalar: isScalar,
                        Guid: guid,
                        StreamName: streamName));
                }

                channelCursor += matNumChannels;
            }
        }
        return result;
    }

    private static string ReadCStringSafe(ReadOnlySpan<byte> s, uint off, int max)
    {
        if (off == 0 || off >= (uint)s.Length) return "";
        int end = (int)off;
        int limit = Math.Min(s.Length, (int)off + max);
        while (end < limit && s[end] != 0) end++;
        if (end <= (int)off) return "";
        return Encoding.UTF8.GetString(s.Slice((int)off, end - (int)off));
    }

    private static uint U32(ReadOnlySpan<byte> s, int off) =>
        BinaryPrimitives.ReadUInt32BigEndian(s.Slice(off, 4));
    private static ulong U64(ReadOnlySpan<byte> s, int off) =>
        BinaryPrimitives.ReadUInt64BigEndian(s.Slice(off, 8));
    private static ushort U16(ReadOnlySpan<byte> s, int off) =>
        BinaryPrimitives.ReadUInt16BigEndian(s.Slice(off, 2));

    // ───────────────────────────────────────────────────────────────────────
    // Cross-reference / report
    // ───────────────────────────────────────────────────────────────────────

    private sealed record GuidOwner(string FolderName, string StreamCategory, string FileName, uint TocType);

    private sealed class CrossRefReport
    {
        public required Dictionary<ulong, List<GuidOwner>> GuidOwners { get; init; }
    }

    private static CrossRefReport BuildReport(List<PsgFileInfo> infos)
    {
        var owners = new Dictionary<ulong, List<GuidOwner>>();
        foreach (var info in infos)
        {
            foreach (var entry in info.TocEntries)
            {
                if (entry.Guid == 0) continue;
                if (!owners.TryGetValue(entry.Guid, out var list))
                {
                    list = new List<GuidOwner>();
                    owners[entry.Guid] = list;
                }
                list.Add(new GuidOwner(info.FolderName, info.StreamCategory, info.FileName, entry.TocType));
            }
        }
        return new CrossRefReport { GuidOwners = owners };
    }

    private static void PrintReport(CrossRefReport report, List<PsgFileInfo> infos, int sampleCount)
    {
        // ─── A. Per-stream-type breakdown ──────────────────────────────────
        Console.WriteLine("=== A. Per-stream-category PSG breakdown ===");
        Console.WriteLine($"{"Category",-10} {"Folders",7} {"PSGs",6} {"Mesh",6} {"Texture",8} {"Collision",10} {"WorldPainter",13} {"Instance",9} {"Other",6} {"TotalKB",10}");

        var byCat = infos.GroupBy(i => i.StreamCategory)
                         .OrderByDescending(g => g.Count());
        foreach (var g in byCat)
        {
            int folders = g.Select(x => x.FolderName).Distinct().Count();
            int meshN = g.Count(i => i.Kind == "Mesh");
            int texN = g.Count(i => i.Kind == "Texture");
            int colN = g.Count(i => i.Kind == "Collision");
            int wpN = g.Count(i => i.Kind == "WorldPainter");
            int instN = g.Count(i => i.Kind == "Instance");
            int otherN = g.Count(i => i.Kind == "Other");
            long totKb = g.Sum(i => i.FileSize) / 1024;
            Console.WriteLine($"{g.Key,-10} {folders,7} {g.Count(),6} {meshN,6} {texN,8} {colN,10} {wpN,13} {instN,9} {otherN,6} {totKb,10:N0}");
        }
        Console.WriteLine();

        // ─── B. Texture-GUID owner overlap ─────────────────────────────────
        Console.WriteLine("=== B. Texture-TOC-GUID owner stream-categories ===");
        Console.WriteLine("(For every cross-file Texture-typed TOC entry: which category(ies) own a PSG with this GUID?)");

        var texGuidOwners = report.GuidOwners
            .Where(kv => kv.Value.Any(o => o.TocType == TocTypeTexture))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        int totalTexGuids = texGuidOwners.Count;
        var byCategorySet = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, owners) in texGuidOwners)
        {
            var cats = owners.Select(o => o.StreamCategory).Distinct().OrderBy(c => c, StringComparer.Ordinal);
            string key = string.Join("+", cats);
            byCategorySet[key] = (byCategorySet.TryGetValue(key, out int v) ? v : 0) + 1;
        }
        Console.WriteLine($"Total distinct Texture-TOC GUIDs: {totalTexGuids}");
        foreach (var kv in byCategorySet.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  Owned by {kv.Key,-30} : {kv.Value,6}");
        Console.WriteLine();

        // ─── C. Mesh material-channel-GUID resolution ──────────────────────
        Console.WriteLine("=== C. Mesh RenderMaterialData channel GUIDs: where do they resolve? ===");
        Console.WriteLine("(For every non-scalar channel GUID in every mesh PSG: which folder(s) own the matching TOC entry?)");

        var allMeshChannels = infos
            .Where(i => i.Kind == "Mesh")
            .SelectMany(i => i.ChannelRefs.Where(c => !c.IsScalar && c.Guid != 0)
                .Select(c => (mesh: i, channel: c)))
            .ToList();

        int total = allMeshChannels.Count;
        int unresolved = 0;
        var resolutionByCategorySet = new Dictionary<string, int>(StringComparer.Ordinal);

        // Counters scoped to cPres meshes (the ones whose resolution behavior we care about).
        int presTotal = 0;
        int presSameFolder = 0;
        int presOtherCPres = 0;
        int presOtherCategoryOnly = 0;
        int presUnresolved = 0;

        // Channel-name breakdown
        var channelStats = new Dictionary<string, (int total, int resolved, int sameFolder, int crossCPres, int unresolved)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mesh, channel) in allMeshChannels)
        {
            string nameKey = string.IsNullOrEmpty(channel.ChannelName) ? "<unnamed>" : channel.ChannelName;
            if (!channelStats.ContainsKey(nameKey)) channelStats[nameKey] = default;
            var st = channelStats[nameKey];
            st.total++;

            bool isPresMesh = mesh.StreamCategory == "cPres";
            if (isPresMesh) presTotal++;

            if (!report.GuidOwners.TryGetValue(channel.Guid, out var owners))
            {
                unresolved++;
                st.unresolved++;
                if (isPresMesh) presUnresolved++;
                channelStats[nameKey] = st;
                continue;
            }
            st.resolved++;

            var cats = owners.Select(o => o.StreamCategory).Distinct().ToHashSet();
            string catKey = string.Join("+", cats.OrderBy(c => c, StringComparer.Ordinal));
            resolutionByCategorySet[catKey] = (resolutionByCategorySet.TryGetValue(catKey, out int v) ? v : 0) + 1;

            bool sameFolder = owners.Any(o => o.FolderName == mesh.FolderName);
            if (sameFolder) st.sameFolder++;

            bool ownerHasCPres = cats.Contains("cPres");

            if (isPresMesh)
            {
                if (sameFolder) presSameFolder++;
                else if (ownerHasCPres) presOtherCPres++;
                else presOtherCategoryOnly++;

                if (ownerHasCPres && !sameFolder) st.crossCPres++;
            }

            channelStats[nameKey] = st;
        }

        Console.WriteLine($"Total non-scalar mesh channel GUIDs : {total}");
        Console.WriteLine($"  Resolved to some owner             : {total - unresolved}");
        Console.WriteLine($"  UNRESOLVED (no owner anywhere)     : {unresolved}");
        Console.WriteLine();
        Console.WriteLine($"  cPres-mesh channel-GUID resolution (n={presTotal}):");
        Console.WriteLine($"    same cPres folder as the mesh           : {presSameFolder,7}");
        Console.WriteLine($"    different cPres folder                  : {presOtherCPres,7}");
        Console.WriteLine($"    only in some other category              : {presOtherCategoryOnly,7}");
        Console.WriteLine($"    UNRESOLVED                              : {presUnresolved,7}");
        Console.WriteLine();
        Console.WriteLine("  Resolution-category histogram (which set of stream-categories own this GUID, all meshes):");
        foreach (var kv in resolutionByCategorySet.OrderByDescending(k => k.Value))
            Console.WriteLine($"    {kv.Key,-30}  {kv.Value,7}");

        Console.WriteLine();
        Console.WriteLine("  Per-channel-name breakdown:");
        Console.WriteLine($"    {"channel",-32} {"total",7} {"resolved",9} {"sameFold",9} {"->cPres",9} {"unresol",8}");
        foreach (var kv in channelStats.OrderByDescending(k => k.Value.total))
        {
            var st = kv.Value;
            Console.WriteLine($"    {Truncate(kv.Key, 32),-32} {st.total,7} {st.resolved,9} {st.sameFolder,9} {st.crossCPres,9} {st.unresolved,8}");
        }
        Console.WriteLine();

        // ─── E. Texture pixel-size distribution by stream category ─────────
        Console.WriteLine("=== E. Texture pixel-size distribution by stream category ===");
        Console.WriteLine($"{"category",-8} {"count",6} {"min",10} {"median",10} {"p90",10} {"max",10} {"avgKB/PSG",10}");
        foreach (var g in infos.Where(i => i.Textures.Count > 0).GroupBy(i => i.StreamCategory).OrderByDescending(g => g.Count()))
        {
            var pixelCounts = g.SelectMany(i => i.Textures).Select(t => (long)t.Width * t.Height).Where(p => p > 0).OrderBy(p => p).ToList();
            if (pixelCounts.Count == 0) continue;
            long minPx = pixelCounts.First();
            long medPx = pixelCounts[pixelCounts.Count / 2];
            long p90 = pixelCounts[(int)(pixelCounts.Count * 0.9)];
            long maxPx = pixelCounts.Last();
            long avgKb = g.Sum(i => i.FileSize) / Math.Max(1, g.Count()) / 1024;
            Console.WriteLine(
                $"{g.Key,-8} {pixelCounts.Count,6} {FormatPixels(minPx),10} {FormatPixels(medPx),10} {FormatPixels(p90),10} {FormatPixels(maxPx),10} {avgKb,10:N0}");
        }
        Console.WriteLine();

        Console.WriteLine("  Texture dimension histogram (each cell = #textures of that exact W x H):");
        // Build a (W, H, category) histogram and pivot on category.
        var dimHist = new Dictionary<(ushort W, ushort H), Dictionary<string, int>>();
        foreach (var info in infos)
        {
            foreach (var t in info.Textures)
            {
                var key = (t.Width, t.Height);
                if (!dimHist.TryGetValue(key, out var perCat))
                    dimHist[key] = perCat = new Dictionary<string, int>();
                perCat[info.StreamCategory] = perCat.TryGetValue(info.StreamCategory, out int c) ? c + 1 : 1;
            }
        }
        Console.WriteLine($"    {"WxH",-12} {"cPres",7} {"total",7}");
        foreach (var kv in dimHist.OrderByDescending(k => k.Value.Values.Sum()).Take(20))
        {
            int cp = kv.Value.TryGetValue("cPres", out int p) ? p : 0;
            int tot = kv.Value.Values.Sum();
            Console.WriteLine($"    {kv.Key.W}x{kv.Key.H,-8} {cp,7} {tot,7}");
        }
        Console.WriteLine();

        // ─── F. Per-GUID texture size table by stream category ────────────
        Console.WriteLine("=== F. Sample Texture-GUID size table by stream category ===");
        var guidToSize = new Dictionary<ulong, (string cat, string folder, ushort w, ushort h, byte fmt, byte mips)>();
        foreach (var info in infos.Where(i => i.Kind == "Texture" && i.Textures.Count > 0))
        {
            var t = info.Textures[0];
            foreach (var entry in info.TocEntries.Where(e => e.TocType == TocTypeTexture))
            {
                if (!guidToSize.ContainsKey(entry.Guid))
                    guidToSize[entry.Guid] = (info.StreamCategory, info.FolderName, t.Width, t.Height, t.Ps3Format, t.MipCount);
            }
        }
        var byCatGuids = guidToSize.GroupBy(kv => kv.Value.cat).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (cat, list) in byCatGuids.OrderByDescending(kv => kv.Value.Count))
        {
            var ordered = list.OrderByDescending(kv => (long)kv.Value.w * kv.Value.h).ToList();
            Console.WriteLine($"  {cat}-owned texture GUIDs: {ordered.Count} total");
            Console.WriteLine($"    largest 5:");
            foreach (var (guid, info) in ordered.Take(5))
                Console.WriteLine($"      0x{guid:X16} {info.w}x{info.h} fmt=0x{info.fmt:X2} mips={info.mips} in {info.folder}");
            Console.WriteLine($"    smallest 5:");
            foreach (var (guid, info) in ordered.AsEnumerable().Reverse().Take(5))
                Console.WriteLine($"      0x{guid:X16} {info.w}x{info.h} fmt=0x{info.fmt:X2} mips={info.mips} in {info.folder}");
            Console.WriteLine();
        }

        // ─── D. Sample mesh resolution traces ──────────────────────────────
        Console.WriteLine($"=== D. Sample mesh→texture trace ({sampleCount} mesh PSGs) ===");
        var sampleMeshes = infos.Where(i => i.Kind == "Mesh" && i.ChannelRefs.Any(c => !c.IsScalar && c.Guid != 0))
                                .Take(sampleCount);
        foreach (var mesh in sampleMeshes)
        {
            Console.WriteLine($"  [{mesh.StreamCategory}] {mesh.FolderName}/{mesh.FileName}");
            foreach (var ch in mesh.ChannelRefs.Where(c => !c.IsScalar && c.Guid != 0))
            {
                if (report.GuidOwners.TryGetValue(ch.Guid, out var owners) && owners.Count > 0)
                {
                    var first = owners[0];
                    string extra = owners.Count > 1 ? $" (+{owners.Count - 1} more)" : "";
                    Console.WriteLine(
                        $"      ch[{ch.MaterialIndex}.{ch.ChannelIndex}] {Truncate(ch.ChannelName, 24),-24} GUID=0x{ch.Guid:X16} -> [{first.StreamCategory}] {first.FolderName}/{first.FileName}{extra}");
                }
                else
                {
                    Console.WriteLine(
                        $"      ch[{ch.MaterialIndex}.{ch.ChannelIndex}] {Truncate(ch.ChannelName, 24),-24} GUID=0x{ch.Guid:X16} -> UNRESOLVED");
                }
            }
        }
    }

    private static void WriteCsvDump(string outDir, List<PsgFileInfo> infos, CrossRefReport report)
    {
        // 1. all PSGs
        using (var w = new StreamWriter(Path.Combine(outDir, "psgs.csv")))
        {
            w.WriteLine("folder,streamCategory,kind,file,bytes,numTocEntries,numChannels");
            foreach (var i in infos)
                w.WriteLine($"\"{i.FolderName}\",{i.StreamCategory},{i.Kind},\"{i.FileName}\",{i.FileSize},{i.TocEntries.Count},{i.ChannelRefs.Count}");
        }

        // 2. all TOC entries
        using (var w = new StreamWriter(Path.Combine(outDir, "toc_entries.csv")))
        {
            w.WriteLine("folder,streamCategory,file,guid,tocType,nameOrHash,objectPtr");
            foreach (var i in infos)
                foreach (var e in i.TocEntries)
                    w.WriteLine($"\"{i.FolderName}\",{i.StreamCategory},\"{i.FileName}\",0x{e.Guid:X16},0x{e.TocType:X8},0x{e.NameOrHash:X8},0x{e.ObjectPtr:X8}");
        }

        // 3. all material channels
        using (var w = new StreamWriter(Path.Combine(outDir, "channels.csv")))
        {
            w.WriteLine("folder,streamCategory,file,materialIdx,channelIdx,channelName,flags,imageChannel,isScalar,guid,streamName");
            foreach (var i in infos)
                foreach (var c in i.ChannelRefs)
                    w.WriteLine($"\"{i.FolderName}\",{i.StreamCategory},\"{i.FileName}\",{c.MaterialIndex},{c.ChannelIndex},\"{c.ChannelName}\",0x{c.Flags:X4},{c.ImageChannel},{c.IsScalar},0x{c.Guid:X16},\"{c.StreamName}\"");
        }

        // 4. all texture infos
        using (var w = new StreamWriter(Path.Combine(outDir, "textures.csv")))
        {
            w.WriteLine("folder,streamCategory,file,bytes,width,height,format,mips,pitch");
            foreach (var i in infos)
                foreach (var t in i.Textures)
                    w.WriteLine($"\"{i.FolderName}\",{i.StreamCategory},\"{i.FileName}\",{i.FileSize},{t.Width},{t.Height},0x{t.Ps3Format:X2},{t.MipCount},{t.Pitch}");
        }

        // 5. mesh→owner resolution
        using (var w = new StreamWriter(Path.Combine(outDir, "channel_resolution.csv")))
        {
            w.WriteLine("meshFolder,meshStreamCategory,meshFile,channelName,channelGuid,ownerFolder,ownerStreamCategory,ownerFile,ownerTocType");
            foreach (var i in infos.Where(x => x.Kind == "Mesh"))
            {
                foreach (var ch in i.ChannelRefs.Where(c => !c.IsScalar && c.Guid != 0))
                {
                    if (report.GuidOwners.TryGetValue(ch.Guid, out var owners))
                    {
                        foreach (var o in owners)
                            w.WriteLine($"\"{i.FolderName}\",{i.StreamCategory},\"{i.FileName}\",\"{ch.ChannelName}\",0x{ch.Guid:X16},\"{o.FolderName}\",{o.StreamCategory},\"{o.FileName}\",0x{o.TocType:X8}");
                    }
                    else
                    {
                        w.WriteLine($"\"{i.FolderName}\",{i.StreamCategory},\"{i.FileName}\",\"{ch.ChannelName}\",0x{ch.Guid:X16},,,,");
                    }
                }
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string FormatPixels(long pixels)
    {
        // Try to render as WxH if it's a perfect square; else as the raw pixel count.
        int side = (int)Math.Round(Math.Sqrt(pixels));
        if ((long)side * side == pixels && side > 0) return $"{side}x{side}";
        return $"{pixels:N0}px";
    }

    private static string? GetOptionValue(IEnumerable<string> args, string optionPrefix)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
                return a.Substring(optionPrefix.Length);
        }
        return null;
    }
}
