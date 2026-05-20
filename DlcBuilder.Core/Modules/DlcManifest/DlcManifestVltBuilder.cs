using DlcBuilder.Builders;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest.Vlt;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.DlcManifest;

/// Output of the DLC manifest builder. The VLT (`.vlt`) is the binary vault
/// file the engine reads at boot; the BIN (`.bin`) is its companion string /
/// data pool. The shipping naming is `dlc_<package_slug>_minimal.{vlt,bin}`.
public sealed record ManifestArtifacts
{
    public required string PackageSlug { get; init; }
    public required IReadOnlyList<DlcSpec> MapSpecs { get; init; }
    /// String / blob pool referenced by the VLT (always written, even when
    /// the VLT itself is still partial). Available as soon as
    /// <see cref="DlcManifestVltBuilder.PreparePool"/> is called.
    public required byte[] BinFile { get; init; }
    /// Vault file bytes. Null while the VLT writer port is in progress;
    /// populated once <see cref="DlcManifestVltBuilder.Build"/> can produce
    /// real bytes.
    public byte[]? VltFile { get; init; }
}

/// Builds the DLC manifest pair (`.vlt` + `.bin`) from a public-API
/// <see cref="PackageInput"/>. The pair tells the engine:
///   - The package's category / filter / listing keys.
///   - Each map's WorldStreamName, BIG mount, HAL display strings, FE image.
///   - Audio bigfile descriptors (so DLC unload can release per-DLC audio).
///   - Spawn / locator / progression cross-references.
///
/// In retail Skate 3 this comes out of MinimalDlcBuilder.DlcBuilder.WriteAllOutputFiles
/// (a 250 KB orchestrator that emits ~30 different VLT collection rows). This
/// new module ports the work into a clean, modular shape:
///
///   1. <see cref="PreparePool"/>  — derives DlcSpecs + lays out the BinPool.
///                                   Always succeeds; produces the BIN file.
///   2. <see cref="Build"/>        — writes the actual VLT bytes by emitting
///                                   each collection row. Currently throws
///                                   NotImplementedException for the VLT-write
///                                   step; collection-row writers will land
///                                   incrementally as separate ports.
///
/// Front-ends can call PreparePool today to validate input and stage the .bin
/// while the VLT writers are being ported.
public static class DlcManifestVltBuilder
{
    /// Derive all DlcSpecs + lay out the BinPool string table for a package.
    /// Doesn't touch disk. Returns artifacts with VltFile = null.
    public static ManifestArtifacts PreparePool(PackageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Maps.Count == 0)
            throw new InvalidOperationException("PackageInput has no maps to derive specs from.");

        // Derive per-map specs.
        var specs = new List<DlcSpec>(input.Maps.Count);
        foreach (var m in input.Maps)
            specs.Add(DlcSpec.FromMapInput(m, input.Prefix));

        // Same rationale as Build() below — package-level slug always derives
        // from PackageName, never from a single DIST.
        string packageSlug = DlcSpec.ToSlug(input.PackageName);
        if (string.IsNullOrWhiteSpace(packageSlug))
            throw new InvalidOperationException("Package slug is empty (PackageName produced nothing).");

        // Lay out the BinPool with the strings the VLT collection rows will
        // reference. We seed it with the strings we know retail manifests
        // always include; the VLT writers (when ported) will add more.
        var pool = new BinPoolBuilder();
        // Standard always-present strings (one entry kept; offsets ignored — the
        // VLT writer will reference these by name when it materializes).
        pool.AddString(input.PackageName);
        foreach (var s in specs)
        {
            pool.AddString(s.WorldStreamName);
            pool.AddString($"world{s.WorldStreamName}.big");
            pool.AddString(s.WorldHalName);
            pool.AddString(s.LocationHalName);
            pool.AddString(s.LocationDescHalName);
            pool.AddString(s.LocationHelperHalName);
            pool.AddString(s.MapCategoryKey);
            pool.AddString(s.MapFilterKey);
            pool.AddString(s.MapListingProgressionKey);
            pool.AddString(s.DistKey);
            pool.AddString(s.DisplayName);
            pool.AddString(s.DescriptionText);
            if (!string.IsNullOrEmpty(s.SectionLabel)) pool.AddString(s.SectionLabel);
            if (!string.IsNullOrEmpty(s.FeLocationImageVaultPath)) pool.AddString(s.FeLocationImageVaultPath);
            pool.AddString(s.SkyBoxModelPath);
            pool.AddString(s.SkyBoxTexturePath);
        }

        return new ManifestArtifacts
        {
            PackageSlug = packageSlug,
            MapSpecs = specs,
            BinFile = pool.BuildBinFile(),
            VltFile = null,
        };
    }

    /// Build the full DLC manifest pair (`.vlt` + `.bin`). Composes
    /// `world` + `fe_locations` + `map_category` + `map_filter` + `map_listing` +
    /// `dlc_mapping` rows for the package, hands them to the VLT writer, and
    /// returns both the assembled bytes and the per-map derived specs.
    ///
    /// What's implemented today:
    ///   • One `world` + one `fe_locations` per map.
    ///   • One `map_category` (parent=world) per package.
    ///   • One main `map_filter` (parent=default_dlc) for the offline filter.
    ///   • One combined online `map_filter` (parent=freeskate_locations) +
    ///     matching `map_listing` for the Online → Freeskate menu.
    ///   • One `map_listing` (parent=progression_locations) for the offline
    ///     menu, ordering=7 (matches retail).
    ///   • One `dlc_mapping` per package.
    ///
    /// Not yet wired (subsequent ports):
    ///   • `aud_worlddata`, `aud_bigfiles`, `worldpainter_fe_location_triggers`,
    ///     `progression_summary_group` per-map rows.
    ///   • Multi-section sub-categories.
    ///   • bin fixups for the FE layout struct + Lua descriptors (the
    ///     attributes still emit data=0 with PtrN entries via NeedsPtrN, but
    ///     the BinPool layout helpers below mirror retail bin offsets so
    ///     fixups can be added when we wire them).
    public static ManifestArtifacts Build(
        PackageInput input,
        out IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagList = new List<Diagnostic>();

        if (input.Maps.Count == 0)
            throw new InvalidOperationException("PackageInput has no maps to build a manifest for.");

        // Derive per-map specs (existing logic).
        var specs = new List<DlcSpec>(input.Maps.Count);
        foreach (var m in input.Maps)
            specs.Add(DlcSpec.FromMapInput(m, input.Prefix));

        // Package-level identifiers are derived from PackageName, never from
        // the DIST slug. Original PackageSpec.Build (MinimalDlcBuilder) only
        // falls back to the DIST slug when no `packageOverride` is provided —
        // and the editor's export prompt always provides one, so we always
        // take the packageOverride path here. Using the DIST slug instead
        // breaks the language-pack filename pattern (`LANGUAGE_English_<slug>_*.BIN`)
        // and the in-game category title because the engine cross-references
        // these against PackageName-derived keys.
        string packageSlug = DlcSpec.ToSlug(input.PackageName);
        if (string.IsNullOrWhiteSpace(packageSlug))
            throw new InvalidOperationException("Package slug is empty (PackageName produced nothing).");

        string categoryKey = packageSlug + "dlc";
        string filterKey = packageSlug + "dlc";
        string listingKey = "progression_locations_dlc_" + packageSlug;
        string categoryDisplayHal = "ID_MAP_CATEGORY_" + packageSlug.ToUpperInvariant();
        string filterHelperHal = "ID_MAP_HELPER_" + packageSlug.ToUpperInvariant();

        // Lay out the BinPool. We're tracking pointer offsets for each map
        // so the row builders can reference them.
        var bin = new BinPoolBuilder();
        uint filterNamePtr = bin.AddString(filterKey);
        uint categoryNamePtr = bin.AddString(categoryKey);
        uint dispPtr = bin.AddString(categoryDisplayHal);
        uint helperPtr = bin.AddString(filterHelperHal);

        // Per-map BinPool slots used by world + fe_locations + dlc_mapping.
        var perMap = new List<MapBinSlots>(input.Maps.Count);
        foreach (var s in specs)
        {
            perMap.Add(new MapBinSlots
            {
                Spec = s,
                WorldNamePtr      = bin.AddString(s.DistKey),
                WorldHalPtr       = bin.AddString(s.WorldHalName),
                LocHalPtr         = bin.AddString(s.LocationHalName),
                LocDescPtr        = bin.AddString(s.LocationDescHalName),
                WorldStreamPtr    = bin.AddString(s.WorldStreamName),
                WorldBigPtr       = bin.AddString($"world{s.WorldStreamName}.big"),
                ShortNamePtr      = bin.AddString(s.ShortName),
                SpawnPtr          = bin.AddString($"Z_{s.Slug}_Start"),
                SkyBoxModelPtr    = bin.AddString(s.SkyBoxModelPath),
                SkyBoxTexturePtr  = bin.AddString(s.SkyBoxTexturePath),
                WorldRef16Off     = bin.AddBlob(VltBinHelpers.BuildClassRef16(s.DistKey)),
            });
        }
        bool useSubsections = specs.Any(m => !string.IsNullOrWhiteSpace(m.SectionLabel));
        List<string> orderedSectionSlugs = !useSubsections
            ? new List<string>()
            : specs
                .Select(m => string.IsNullOrWhiteSpace(m.SectionLabel) ? "default" : DlcSpec.ToSlug(m.SectionLabel))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

        // map_filter Listing target → 24-byte typed RefSpec at fixup-time
        uint listingRefOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("map_filter", filterKey));

        // Offline Lua filter source string + 16-byte tCompiledLua descriptor.
        string offlineLua = BuildOfflineFilterLua(specs);
        uint offlineSrcOff = bin.AddString(offlineLua);
        uint offlineNulOff = offlineSrcOff + (uint)System.Text.Encoding.ASCII.GetByteCount(offlineLua);
        uint filterDescOff = bin.AddBlob(VltBinHelpers.BuildLuaDescriptor(offlineSrcOff, offlineNulOff));

        // Per-map FE layout struct (12 bytes; PtrN-fixed-up at runtime).
        foreach (var m in perMap)
            m.FeLayoutOffset = bin.AddBlob(VltBinHelpers.BuildFeLayout(m.WorldNamePtr, m.SpawnPtr, m.LocHalPtr));

        // Online combined filter (one row covers every map in the package via OR'd Lua).
        string combinedFilterKey  = "freeskate_locations_" + filterKey;
        string combinedListingKey = "online_freeskate_" + filterKey;
        uint combinedFilterNamePtr = bin.AddString(combinedFilterKey);
        string onlineLua = BuildOnlineFilterLua(specs);
        uint onlineSrcOff = bin.AddString(onlineLua);
        uint onlineNulOff = onlineSrcOff + (uint)System.Text.Encoding.ASCII.GetByteCount(onlineLua);
        // Stock freeskate filters ship 12 zero bytes (no Lua source on disk);
        // we mirror that, but the engine's ITER 20 fix REQUIRES PtrN fixups
        // here — wired in the binFixups list below.
        uint combinedFilterDescOff = bin.AddBlob(new byte[12]);
        uint combinedListingRefOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("map_filter", combinedFilterKey));

        // DMOBanks: retail Danny Way `world/dlc_dw_megacompound` ships four `dmo_banks`
        // RefSpecs (see VltBinHelpers.BuildDannyWayMegacompoundDmoBanksArray). One shared
        // bin blob for all maps in the package — matches single-world DLC layout.
        uint dmoBanksSharedOff = bin.AddBlob(VltBinHelpers.BuildDannyWayMegacompoundDmoBanksArray());

        // Per-map FE map info + audio data + DMO banks + map icon offset blobs.
        var worldMapCategoryRef24ByKey = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var m in perMap)
        {
            // FE map panel bounds — match retail "downtown" profile.
            m.FeMapInfoOff = bin.AddBlob(VltBinHelpers.BuildTMapInfo(
                boundMaxX: 2050, boundMinX: -850,
                boundMaxY: 1050, boundMinY: -1250,
                tileX: 512, tileY: 512));
            m.FeMapTextureKeyPtr = bin.AddString("map1024");

            // Dedicated zero blobs — caching slots are guaranteed-zero so the
            // engine's audio path doesn't deref garbage.
            m.AudioDataOff = bin.AddBlob(new byte[24]);
            m.DmoBanksOff = dmoBanksSharedOff;
            m.FeMapIconOffsetOff = bin.AddBlob(new byte[8]);

            // map_category world-ref (24B) — shared across maps with same category key.
            // CRITICAL: this is the PACKAGE-level category key (`testpkgdlc`),
            // NOT the per-DIST `MapCategoryKey` (`underpasstrueskatedlc`). The
            // engine resolves world rows' MapCategory ref against the actual
            // map_category row that this package emits — and we only emit ONE
            // map_category row per package, keyed by `categoryKey`. Hashing
            // the per-DIST key here generated a Lookup8 that pointed at a
            // non-existent row → engine binds World.MapCategory to NULL →
            // online filter chain drops the entry. Verified vs MinimalDlcBuilder
            // /DlcBuilder.cs:192-199 (`ResolveMapCategoryKeyForMap` returns
            // `pkg.CategoryKey` for single-section packs and
            // `pkg.CategoryKey + "_" + sectionSlug` for multi-section).
            string mapCatKey = useSubsections
                ? categoryKey + "_" + (string.IsNullOrWhiteSpace(m.Spec.SectionLabel)
                    ? "default"
                    : DlcSpec.ToSlug(m.Spec.SectionLabel))
                : categoryKey;
            if (!worldMapCategoryRef24ByKey.TryGetValue(mapCatKey, out uint worldMapCatRef24))
            {
                worldMapCatRef24 = bin.AddBlob(VltBinHelpers.BuildMapCategoryWorldRef24(mapCatKey));
                worldMapCategoryRef24ByKey[mapCatKey] = worldMapCatRef24;
            }
            m.WorldMapCategoryRef24 = worldMapCatRef24;

            // Per-map fe_locations.MapCategory ref (24B, includes per-map DistKey hash).
            m.FeMapCategoryRef24 = bin.AddBlob(VltBinHelpers.BuildMapCategoryFeLocationsRef24(mapCatKey, m.Spec.DistKey));
        }

        // Build bin fixups list. Mirrors the retail layout: filter descriptor
        // src/nul pointers + per-map FE layout slots + per-map FE map info
        // texture key.
        var binFixups = new List<(uint fixupOffset, uint ptrValue)>
        {
            (filterDescOff, offlineSrcOff),
            (filterDescOff + 8u, offlineNulOff),
            (combinedFilterDescOff, onlineSrcOff),
            (combinedFilterDescOff + 8u, onlineNulOff),
        };
        foreach (var m in perMap)
        {
            binFixups.Add((m.FeLayoutOffset, m.WorldNamePtr));
            binFixups.Add((m.FeLayoutOffset + 4u, m.SpawnPtr));
            binFixups.Add((m.FeLayoutOffset + 8u, m.LocHalPtr));
            // tMapInfo's first 4 bytes are a key-pointer slot.
            binFixups.Add((m.FeMapInfoOff, m.FeMapTextureKeyPtr));
        }

        string psgFrameworkKey = "dlc_" + packageSlug.ToLowerInvariant();

        // Compose collection rows in canonical retail emit order.
        var rows = new List<CollectionBlob>();

        // BIN POOL ORDERING — matches MinimalDlcBuilder/DlcBuilder.cs:212-292
        // byte-for-byte. The world rows come first, then per-map worldpainter
        // + aud rows interleave with their bin blobs in this exact sequence:
        //
        //   world row (no new bin)
        //   wpLayoutOff = 40 zero bytes
        //   worldpainter row
        //   emitterFilesOff = ArrayHeader(typeSize=4)
        //   nisSpeechOff = 24 zero bytes
        //   aud_worlddata row
        //   "data/audio/english/" string  (added per-map iter; first iter
        //   "" string                       defines the offset, others append
        //   aud_bigfiles row                fresh copies — BinPool doesn't dedup)
        //
        // Earlier shape pulled the audio strings + array headers out of this
        // loop (and added PSG blobs in the same loop); the resulting bin
        // pool offsets diverged from retail by enough to break OFFLINE
        // freeskate registration too. Match retail order exactly.
        foreach (var m in perMap)
        {
            rows.Add(VltRowBuilders.BuildWorldCollection(
                m.Spec, m.WorldNamePtr, m.WorldHalPtr, m.WorldStreamPtr, m.ShortNamePtr,
                m.WorldBigPtr, m.WorldStreamPtr, m.WorldMapCategoryRef24, m.FeMapInfoOff,
                m.SkyBoxModelPtr, m.SkyBoxTexturePtr, m.AudioDataOff, m.DmoBanksOff, m.FeMapIconOffsetOff));

            // worldpainter_fe_location_triggers (registers freeskate trigger volumes)
            m.WpLayoutOff = bin.AddBlob(new byte[40]);
            rows.Add(VltAudRowBuilders.BuildWorldPainterFeLocationTriggersCollection(m.Spec.DistKey, m.WpLayoutOff));

            // aud_worlddata (engine audio sees the world)
            m.EmitterFilesArrayOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(4));
            m.NisSpeechRefOff = bin.AddBlob(new byte[24]);
            rows.Add(VltAudRowBuilders.BuildAudWorldDataCollection(m.Spec.DistKey, m.EmitterFilesArrayOff, m.NisSpeechRefOff));

            // aud_bigfiles (DLC audio cleanup chain anchor) — per-map fresh
            // strings so BIN offsets stay in lockstep with retail.
            uint defaultAudioPathPtr = bin.AddString("data/audio/english/");
            uint emptyStringPtr = bin.AddString("");
            rows.Add(VltAudRowBuilders.BuildAudBigfilesCollection(m.Spec.DistKey, defaultAudioPathPtr, emptyStringPtr));
        }

        rows.Add(VltRowBuilders.BuildMapCategoryCollection(
            categoryKey, "world", categoryNamePtr, dispPtr, sortKey: 20000u));
        if (useSubsections)
        {
            for (int i = 0; i < orderedSectionSlugs.Count; i++)
            {
                string sectionSlug = orderedSectionSlugs[i];
                string childKey = $"{categoryKey}_{sectionSlug}";
                uint childNamePtr = bin.AddString(childKey);
                uint childDisplayPtr = bin.AddString(BuildMapSectionDisplayHalId(packageSlug, sectionSlug));
                uint sortKey = (uint)(20100 + i * 100);
                rows.Add(VltRowBuilders.BuildMapCategoryCollection(
                    childKey, categoryKey, childNamePtr, childDisplayPtr, sortKey));
            }
        }

        foreach (var m in perMap)
        {
            // Match MinimalDlcBuilder/DlcBuilder.cs:313 — when no FE image is
            // configured, original points Image at an empty string in the bin
            // pool (NOT NULL). The bin pool dedupes "" so subsequent blobs
            // are stable; the engine's Text resolver reads "" and skips image
            // lookup gracefully. Passing 0u makes the row's Image pointer
            // resolve to garbage at bin[0].
            uint feImgPtr = string.IsNullOrWhiteSpace(m.Spec.FeLocationImageVaultPath)
                ? bin.AddString(string.Empty)
                : bin.AddString(m.Spec.FeLocationImageVaultPath);
            rows.Add(VltRowBuilders.BuildFeLocationsCollection(
                m.Spec, m.FeLayoutOffset, m.LocDescPtr, feImgPtr, m.FeMapCategoryRef24, m.WorldRef16Off));
        }

        rows.Add(VltRowBuilders.BuildMapFilterCollection(
            filterKey, "default_dlc", filterNamePtr, helperPtr, filterDescOff,
            input.Maps.Count == 1 ? perMap[0].WorldHalPtr : dispPtr));

        rows.Add(VltRowBuilders.BuildMapFilterCollection(
            combinedFilterKey, "freeskate_locations", combinedFilterNamePtr, helperPtr, combinedFilterDescOff,
            input.Maps.Count == 1 ? perMap[0].WorldHalPtr : dispPtr,
            exactInstanceFlags: true));

        rows.Add(VltRowBuilders.BuildMapListingCollection(
            listingKey, "progression_locations", listingRefOff, ordering: 7));

        rows.Add(VltRowBuilders.BuildDlcMappingCollection(packageSlug, bin));

        // progression_summary_group: framework anchor + per-map entries.
        // Bin blobs for each entry are added INSIDE this loop (not earlier)
        // to match MinimalDlcBuilder/DlcBuilder.cs:354-401 — retail emits
        // these blobs late, after map_category / fe_locations / dlc_mapping.
        rows.Add(VltAudRowBuilders.BuildProgressionSummaryGroupAnchor(psgFrameworkKey));
        ushort psgOrder = 14;  // unique-ish ordering; retail DW=13.
        foreach (var m in perMap)
        {
            string entryKey = m.Spec.Slug + "_dlc";
            m.PsgChallengeGroupArrayOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
            m.PsgIconRef16Off = bin.AddBlob(VltBinHelpers.BuildClassRef16("teleport"));
            m.PsgTitlePtr = bin.AddString("ID_DLC_" + m.Spec.Slug.ToUpperInvariant() + "_SUMMARY_TITLE");
            rows.Add(VltAudRowBuilders.BuildProgressionSummaryGroupEntry(
                entryKey, psgFrameworkKey,
                m.PsgChallengeGroupArrayOff, m.PsgIconRef16Off, m.PsgTitlePtr,
                psgOrder));
        }

        rows.Add(VltRowBuilders.BuildMapListingCollection(
            combinedListingKey, "online_freeskate", combinedListingRefOff, ordering: 11,
            exactInstanceFlags: true));

        // Assemble the .vlt — DepN chunk inside the VLT must reference the
        // SAME `dlc_<slug>_minimal.{vlt,bin}` filename token used on disk,
        // not the user's display name. The engine reads DepN to find the
        // sibling .bin file; a mismatch makes BIN load fail at boot.
        // Match MinimalDlcBuilder/PackageSpec.Build:41 — pkg.PackageName is
        // the synthesized "<prefix>_<slug>_minimal" token.
        string packageFileName = $"{DlcSpec.ToSlug(input.Prefix)}_{packageSlug}_minimal";
        string vltFileName = packageFileName + ".vlt";
        string binFileName = packageFileName + ".bin";
        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(vltFileName, binFileName, rows, binFixups);
        byte[] binBytes = bin.BuildBinFile();

        diagList.Add(new Diagnostic(DiagnosticLevel.Info, "DlcManifest",
            $"Built manifest: {rows.Count} VLT rows, {binBytes.Length}B BIN, {vltBytes.Length}B VLT, {binFixups.Count} fixups."));

        diagnostics = diagList;
        return new ManifestArtifacts
        {
            PackageSlug = packageSlug,
            MapSpecs = specs,
            BinFile = binBytes,
            VltFile = vltBytes,
        };
    }

    /// Per-map BinPool offset book-keeping (mirrors MinimalDlcBuilder.MapBinData
    /// but scoped to this builder's local layout pass).
    private sealed class MapBinSlots
    {
        public required DlcSpec Spec;
        public uint WorldNamePtr;
        public uint WorldHalPtr;
        public uint LocHalPtr;
        public uint LocDescPtr;
        public uint WorldStreamPtr;
        public uint WorldBigPtr;
        public uint ShortNamePtr;
        public uint SpawnPtr;
        public uint SkyBoxModelPtr;
        public uint SkyBoxTexturePtr;
        public uint WorldRef16Off;
        public uint FeLayoutOffset;
        public uint FeMapInfoOff;
        public uint FeMapTextureKeyPtr;
        public uint AudioDataOff;
        public uint DmoBanksOff;
        public uint FeMapIconOffsetOff;
        public uint WorldMapCategoryRef24;
        public uint FeMapCategoryRef24;
        // aud + worldpainter + progression_summary_group slots
        public uint EmitterFilesArrayOff;
        public uint NisSpeechRefOff;
        public uint WpLayoutOff;
        public uint PsgChallengeGroupArrayOff;
        public uint PsgIconRef16Off;
        public uint PsgTitlePtr;
    }

    private static string BuildOfflineFilterLua(IReadOnlyList<DlcSpec> specs)
    {
        // Byte-for-byte mirror of MinimalDlcBuilder/DlcBuilder.cs:55-66.
        // Header line uses ` ) then` (SPACE before `then`, NO `and item.Unlocked`).
        // Earlier audit pass mis-introduced `and item.Unlocked` here — that
        // syntax matches the FREESKATE filter shape, not the OFFLINE
        // fe_locations filter. Restoring the exact retail shape.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("do");
        sb.AppendLine("    if( item.ClassKey == Attrib.ClassName.fe_locations ) then");
        foreach (var s in specs)
        {
            sb.AppendLine($"        if( LocationFilterHelpers.IsLocationInWorld( item, \"{s.DistKey}\" ) ) then");
            sb.AppendLine("            return true;");
            sb.AppendLine("        end");
        }
        sb.AppendLine("    end");
        sb.AppendLine("    return false;");
        sb.Append("end;");
        return sb.ToString();
    }

    private static string BuildOnlineFilterLua(IReadOnlyList<DlcSpec> specs)
    {
        // Combined online freeskate filter — covers every world in the package
        // via OR'd `IsLocationInWorld(item, <dist>)`. The IsSkatePark guard +
        // ChallengeType check match retail per-world filter shape (verified at
        // retail dlc_danny_way_park.bin offset 0x2A4).
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("do");
        sb.AppendLine("\tif( item.ClassKey == Attrib.ClassName.challenges )then");
        sb.AppendLine("\t\tif( item.GlobalData.ChallengeType == Challenge.eChallengeTypes.OnlineFreeSkate )then");
        sb.Append("\t\t\tif( item.GlobalData.World.IsSkatePark == false and (");
        for (int i = 0; i < specs.Count; i++)
        {
            if (i == 0) sb.AppendLine();
            sb.Append($"\t\t\t\tLocationFilterHelpers.IsLocationInWorld( item, \"{specs[i].DistKey}\" )");
            if (i + 1 < specs.Count) sb.AppendLine(" or"); else sb.AppendLine();
        }
        sb.AppendLine("\t\t\t) )then");
        sb.AppendLine("\t\t\t\treturn true;");
        sb.AppendLine("\t\t\tend");
        sb.AppendLine("\t\tend");
        sb.AppendLine("\tend");
        sb.AppendLine("\t");
        sb.AppendLine("\treturn false;");
        sb.Append("end;");
        return sb.ToString();
    }

    private static string BuildMapSectionDisplayHalId(string packageSlug, string sectionSlug) =>
        "ID_MAP_SECTION_" + DlcSpec.ToSlug(packageSlug).ToUpperInvariant() + "_" + sectionSlug.ToUpperInvariant();
}
