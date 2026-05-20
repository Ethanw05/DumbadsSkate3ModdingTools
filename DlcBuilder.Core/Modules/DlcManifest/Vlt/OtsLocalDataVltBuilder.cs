using System.Linq;
using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest.Vlt.Templates;
using DlcBuilder.Modules.LocatorPsg;
using DlcBuilder.Modules.OtsPsg;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// `db/challenge_local_data/&lt;challengeKey&gt;.vlt` — per-instance OTS challenge
/// VLT. Two rows:
///
///   1. challenge_local_data/default (parent=Hash_0) — the 45-attr inline-default
///      override. Same row every freeskate / OTS challenge_local_data VLT
///      ships (verified via vlt_rows.py against retail OTS bins).
///   2. challenge_local_data/&lt;key&gt; (parent=&lt;framework&gt;_own_the_spots) —
///      6-attr per-instance row with the actual ChallengeBoundary /
///      DiscoveryBoundary / OTSScoringBoundary GUIDs from the OTS PSG. The
///      48-byte layout blob holds schema-mapped HostCharacter + 2× trigger
///      volume slots that the engine reads from row+0x28 → bin pool.
///
/// Per-instance trigger-volume names MUST match the canonical names emitted
/// in cSim_Global.psg's tTriggerInstance entries (cTriggerVolumeManager keys
/// on the GuidLocal / VolumeID for resolution).
///
/// VisualIndicators audit (retail Attrib XML under <c>documentation/</c>, all challenge types):
/// run <c>Dumping Tools/visual_indicators_attrib_audit.py</c>. High-signal <c>challenge_local_data</c>
/// location-kinds: OTS DLC <c>0x3C</c> (<c>ots_dwmc_01</c>), stock OTS <c>0x58</c> (<c>ots_dwtn_01</c>),
/// race <c>0x08</c>, photo <c>0x60</c>, Danny Way framework rows <c>0xD8</c>. Second RefSpec is almost
/// always <c>ribbon_spline_colour</c>/<c>default</c> (<c>F0DB8767…</c> / <c>D7EDBD36…</c>).
public static class OtsLocalDataVltBuilder
{
    public sealed record OtsLocalDataArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static OtsLocalDataArtifacts Build(OtsChallengeSpec spec, string frameworkKey)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);

        string parentKey = $"{frameworkKey}_own_the_spots";
        string fileName = spec.ChallengeKey;
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        var collections = new List<CollectionBlob>();
        var binFixups = new List<(uint, uint)>();

        // ── Row 1: 45-attr default/Hash_0 — MUST land first so tail blob is
        //   at exactly 0x60 in the bin pool (asserted by the template builder).
        FreeskateChallengeLocalDataTemplate.AppendDefaultHash0Row(bin, collections);

        // After the default row's bin layout is locked in, we can add per-instance
        // strings + blobs without disturbing the template's offset assertions.
        uint emptyPathStr = bin.AddString("");

        // ── Row 2: challenge_local_data/<key> per-instance row ──
        // Trigger volume names must match what OtsPsgBuilder emits in
        // cSim_Global.psg — the engine binds via the canonical name.
        OtsTriggerVolume? challengeBoundary = spec.Triggers.FirstOrDefault(
            t => t.Name != null && t.Name.Contains("challengeboundary", StringComparison.OrdinalIgnoreCase));
        OtsTriggerVolume? discoveryBoundary = spec.Triggers.FirstOrDefault(
            t => t.Name != null && t.Name.Contains("discoveryboundary", StringComparison.OrdinalIgnoreCase));
        OtsTriggerVolume? scoringBoundary = spec.Triggers.FirstOrDefault(
            t => t.Name != null && t.Name.Contains("scoringboundary", StringComparison.OrdinalIgnoreCase));

        uint challengeBoundaryNameOff = challengeBoundary != null
            ? bin.AddString(challengeBoundary.Name)
            : emptyPathStr;
        uint scoringBoundaryNameOff = scoringBoundary != null
            ? bin.AddString(scoringBoundary.Name)
            : emptyPathStr;
        // Retail Danny Way ots_dwmc_01.xml proof:
        //   DiscoveryBoundary  GUID=2C701706003D0BB5  name=…|ots_dwmc_01_scoringboundary|…
        //   OTSScoringBoundary GUID=2C701706003D0BB5  name=…|ots_dwmc_01_scoringboundary|…
        //   ChallengeBoundary  GUID=2C701706003D0BAC  name=…|ots_dwmc_01_challengeboundary|…
        // DiscoveryBoundary aliases to the scoring volume, NOT to a separate
        // discoveryboundary trigger and NOT to challengeboundary. There is no
        // discoveryboundary volume in retail's PSG either (3 instances:
        // challengeboundary, scoringboundary, spotvolume_1).
        uint discoveryBoundaryNameOff = scoringBoundary != null
            ? scoringBoundaryNameOff
            : discoveryBoundary != null
                ? bin.AddString(discoveryBoundary.Name)
                : challengeBoundaryNameOff;
        uint startLocatorNameOff = bin.AddString($"{spec.ChallengeKey}_startlocator");

        string chevPrefix = $"{spec.ChallengeKey}_chev_";
        string visPrefix = $"{spec.ChallengeKey}_vis_";
        static int SuffixOrdinal(string name, string prefix)
        {
            ReadOnlySpan<char> suffix = name.AsSpan(prefix.Length);
            int n = 0;
            for (int i = 0; i < suffix.Length && char.IsAsciiDigit(suffix[i]); i++)
                n = n * 10 + (suffix[i] - '0');
            return n == 0 ? int.MaxValue : n;
        }

        LocationDescDataBuilder.SubLocSpec[] chevSubsOrdered = spec.SubLocators
            .Where(s => s.Name.StartsWith(chevPrefix, StringComparison.Ordinal)
                && !s.OmitFromChallengeLocalVisualIndicators)
            .OrderBy(s => SuffixOrdinal(s.Name, chevPrefix))
            .ToArray();

        LocationDescDataBuilder.SubLocSpec[] visSubsOrdered = spec.SubLocators
            .Where(s => s.Name.StartsWith(visPrefix, StringComparison.Ordinal))
            .OrderBy(s => SuffixOrdinal(s.Name, visPrefix))
            .ToArray();

        // Freeskate signup ribbon uses `{key}_vis_1` + SignUpIndicator on challenge_global_data — it must
        // NOT appear in challenge_local_data.VisualIndicators (otherwise it spawns again during the run).
        string signupVisSubName = $"{spec.ChallengeKey}_vis_1";
        LocationDescDataBuilder.SubLocSpec[] visSubsChallengeOnly = visSubsOrdered
            .Where(s => !string.Equals(s.Name, signupVisSubName, StringComparison.Ordinal))
            .ToArray();

        // VisualIndicators: _chev_* then in-challenge `_vis_2..` only (`_vis_1` omitted). PtrN → PSG sub strings.
        var indicatorSubsOrdered = new List<LocationDescDataBuilder.SubLocSpec>(
            chevSubsOrdered.Length + visSubsChallengeOnly.Length);
        indicatorSubsOrdered.AddRange(chevSubsOrdered);
        indicatorSubsOrdered.AddRange(visSubsChallengeOnly);

        var locatorNameOffs = new List<uint>(indicatorSubsOrdered.Count);
        foreach (LocationDescDataBuilder.SubLocSpec sub in indicatorSubsOrdered)
            locatorNameOffs.Add(bin.AddString(sub.Name));

        int visualIndicatorCount = locatorNameOffs.Count;

        // Empty-array headers — typeSize MUST match canonical FieldDefinition.Size:
        //   IntroPresentationEvents → tChallengePresentationEvent (16B)
        //   VisualIndicators        → tVisualEditorData            (80B)
        // Wrong stride miscomputes element offsets even when count=0 (engine's
        // listener-registry walker does element-offset arithmetic before
        // checking count).
        uint emptyArrayHeader16 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        // One tVisualEditorData per visual row: authored _chev_* then _vis_*.
        // Chev trail uses same Lookup8 mCollectionKey as retail in-challenge ribbons (D6C61BC5…); vis uses arrow.
        ulong ribbonIndicatorClassHash = Lookup8Hashing.Hash("ribbon_indicator");
        uint visualIndicatorsArray80 = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)visualIndicatorCount);
            w.WriteBE((ushort)visualIndicatorCount);
            w.WriteBE((ushort)80);
            w.WriteBE((ushort)0);

            // tVisualEditorData 80 B element (Sk8::Challenge), BE — verified against
            // documentation/**/*.xml VisualIndicators; strict challenge_local_data/*.xml
            // rows always have +0x0C..+0x17 and +0x1C zero (visual_indicators_attrib_audit.py --payload-invariants).
            // OTS ots_*.xml rows use +0x00/+0x04 = 0; photo / some dlc_dwgh_* locals use +0x04 = 8 on elem 0 only.
            for (int i = 0; i < visualIndicatorCount; i++)
            {
                w.WriteBE(0u); // +0x00
                w.WriteBE(0u); // +0x04 (OTS retail 0; leave spec hook if a non-OTS mode is added later)
                w.WriteBE(spec.VisualIndicatorLocationKind); // +0x08 location-kind
                w.WriteBE(0u); // +0x0C pad
                w.WriteBE(0UL); // +0x10 reserved / second qword (0 for challenge_local_data)

                w.WriteBE(locatorNameOffs[i]); // +0x18 Location → StrE .bin (PtrN)
                w.WriteBE(0u); // +0x1C pad

                // First RefSpec: _chev_* → ribbon_indicator + retail secondary key hash (ots_dwmc_01 elems 2–4 / race_dwtn_01);
                // _vis_* → arrow. SubLocSpec.RibbonIndicatorCollectionKey overrides with a string key.
                // Colour: ribbon_spline_colour/default.
                if (spec.StockOtsStyleAllArrowRibbonKeys)
                    w.Write(VltBinHelpers.BuildRefSpec24("ribbon_indicator", "arrow"));
                else
                {
                    LocationDescDataBuilder.SubLocSpec sub = indicatorSubsOrdered[i];
                    if (!string.IsNullOrEmpty(sub.RibbonIndicatorCollectionKey))
                        w.Write(VltBinHelpers.BuildRefSpec24("ribbon_indicator", sub.RibbonIndicatorCollectionKey));
                    else if (sub.Name.Contains("_chev_", StringComparison.Ordinal))
                        w.Write(VltBinHelpers.BuildRefSpec24(
                            ribbonIndicatorClassHash, VltBinHelpers.RibbonIndicatorSecondarySpotKeyHash));
                    else
                        w.Write(VltBinHelpers.BuildRefSpec24("ribbon_indicator", "arrow"));
                }

                w.Write(VltBinHelpers.BuildRefSpec24("ribbon_spline_colour", "default"));
            }
        }));

        // Per-attribute tTriggerVolumeInstanceID stubs (16B each).
        ulong challengeBoundaryGuid = challengeBoundary?.GuidLocal ?? 0UL;
        // Retail-verified alias: DiscoveryBoundary GUID == scoringBoundary GUID
        // (Danny Way ots_dwmc_01.xml: both 2C701706003D0BB5). The earlier shape
        // pointed Discovery at challengeboundary, which sent `cTriggerVolumeManager`
        // signup discovery checks through the wrong physical hull.
        ulong scoringBoundaryGuid   = scoringBoundary?.GuidLocal   ?? 0UL;
        ulong discoveryBoundaryGuid = scoringBoundary?.GuidLocal
                                      ?? discoveryBoundary?.GuidLocal
                                      ?? challengeBoundary?.GuidLocal
                                      ?? 0UL;
        uint challengeBoundaryStub = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(challengeBoundaryGuid));
        uint discoveryBoundaryStub = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(discoveryBoundaryGuid));
        uint scoringBoundaryStub   = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(scoringBoundaryGuid));
        uint manualWalkSpotVolumesArr = bin.AddBlob(VltPayload.Build(w =>
        {
            // tTriggerVolumeInstanceID[1] = scoring boundary
            w.WriteBE((ushort)1);      // count
            w.WriteBE((ushort)1);      // capacity
            w.WriteBE((ushort)16);     // typeSize
            w.WriteBE((ushort)0);      // align
            w.WriteBE(0u);             // VolumeName ptr (PtrN-fixed)
            w.WriteBE(0u);             // padding
            w.WriteBE(scoringBoundaryGuid); // VolumeID
        }));

        // PtrN fixups for the trigger volume name pointers (offset +0 of each
        // 16B struct). Provides the human-readable name for debug / fallback;
        // engine's primary resolution is via VolumeID at +8 against the PSG-
        // loaded cTriggerVolumeManager.LocalToInstanceMap.
        binFixups.Add((challengeBoundaryStub, challengeBoundaryNameOff));
        binFixups.Add((discoveryBoundaryStub, discoveryBoundaryNameOff));
        binFixups.Add((scoringBoundaryStub,   scoringBoundaryNameOff));
        // First array element starts at +8; patch its VolumeName pointer.
        binFixups.Add((manualWalkSpotVolumesArr + 8u, scoringBoundaryNameOff));
        // VisualIndicators[i].Location — PtrN fixup per element (80-byte stride).
        for (int i = 0; i < visualIndicatorCount; i++)
            binFixups.Add((visualIndicatorsArray80 + 8u + (uint)i * 80u + 24u, locatorNameOffs[i]));

        // ── 48-byte per-instance layout blob ──
        // Schema-mapped fields the engine reads from row+0x28. Verified
        // byte-for-byte from DannyWayDLC ots_dwmc_01.bin @ 0x1C8:
        //   +0x00  HostCharacter u32 (Sk8::Audio::eSk8Characters)
        //   +0x04  reserved
        //   +0x08  ChallengeBoundary name ptr (PtrN-fixed)
        //   +0x0C  reserved
        //   +0x10  ChallengeBoundary GUID u64 BE
        //   +0x18  ScoringBoundary name ptr (PtrN-fixed)
        //   +0x1C  reserved
        //   +0x20  ScoringBoundary GUID u64 BE
        //   +0x28  array header (count=1, cap=1, typeSize=16) — schema sentinel
        uint layoutBlobOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x00000068U);             // +0x00 HostCharacter (matches DW)
            w.WriteBE(0u);                       // +0x04 reserved
            w.WriteBE(0u);                       // +0x08 ChallengeBoundary name ptr (PtrN-fixed)
            w.WriteBE(0u);                       // +0x0C reserved
            w.WriteBE(challengeBoundaryGuid);   // +0x10 ChallengeBoundary GUID
            w.WriteBE(0u);                       // +0x18 ScoringBoundary name ptr (PtrN-fixed)
            w.WriteBE(0u);                       // +0x1C reserved
            w.WriteBE(scoringBoundaryGuid);     // +0x20 ScoringBoundary GUID
            // +0x28 array header: count=1, cap=1, typeSize=16, align=0
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)0x10);
            w.WriteBE((ushort)0);
        }));
        binFixups.Add((layoutBlobOff + 0x08u, challengeBoundaryNameOff));
        binFixups.Add((layoutBlobOff + 0x18u, scoringBoundaryNameOff));

        // 5 real types + 1 trailing zero-qword sentinel slot. Retail
        // ots_dwmc_01: num_types=5, num_types_dup=6, type table = 5 hashes +
        // 1 zero qword. Listing "Hash_0" in explicitTypes would emit
        // Lookup8("Hash_0") (a non-zero hash) where the engine expects 0.
        string[] rowTypes =
        {
            "Sk8::Audio::eSk8Characters",
            "Sk8::Challenge::tTriggerVolumeInstanceID",
            "Sk8::Challenge::tChallengePresentationEvent",
            "Sk8::Challenge::tLocationID",
            "Sk8::Challenge::tVisualEditorData",
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_local_data", spec.ChallengeKey, parentKey, layoutBlobOff,
            new[]
            {
                VltAttribute.PointerNoFixup("ChallengeBoundary",       "Sk8::Challenge::tTriggerVolumeInstanceID",    challengeBoundaryStub,    0x00),
                VltAttribute.PointerNoFixup("DiscoveryBoundary",       "Sk8::Challenge::tTriggerVolumeInstanceID",    discoveryBoundaryStub,    0x00),
                VltAttribute.PointerNoFixup("IntroPresentationEvents", "Sk8::Challenge::tChallengePresentationEvent", emptyArrayHeader16,       0x02),
                VltAttribute.PointerNoFixup("ManualWalkConnectInSpotVolumes", "Sk8::Challenge::tTriggerVolumeInstanceID", manualWalkSpotVolumesArr, 0x02),
                VltAttribute.PointerNoFixup("OTSScoringBoundary",      "Sk8::Challenge::tTriggerVolumeInstanceID",    scoringBoundaryStub,      0x00),
                VltAttribute.Inline        ("StartLocation",           "Sk8::Challenge::tLocationID",                 startLocatorNameOff),
                VltAttribute.PointerNoFixup("VisualIndicators",        "Sk8::Challenge::tVisualEditorData",           visualIndicatorsArray80, 0x0A),
            },
            explicitTypes: rowTypes,
            numTypesDup: 6));

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, binFixups);
        byte[] binBytes = bin.BuildBinFile();
        return new OtsLocalDataArtifacts(fileName, vltBytes, binBytes);
    }
}

/*
 * PtrN / DepN audit (challenge_local_data/<key>.vlt + .bin) — compare retail vs ours:
 *
 * DepN: exactly two NUL-padded dependency slots — `<key>.vlt` and `<key>.bin`
 *       (VltFileWriter.BuildDependencyPayload).
 *
 * PtrN (VltFileWriter.BuildPtrnPayload):
 *   (1) Bin-pool patch list from binFixups: type=3, idx=1, ptr = offset into StrE .bin.
 *       For OTS instance row these include:
 *       - tTriggerVolumeInstanceID stubs: ChallengeBoundary, Discovery, Scoring (+ ManualWalk array).
 *       - layout blob +0x08 / +0x18 (challenge + scoring DIST name strings).
 *       - Per VisualIndicators element: Location string (offset +8 header + i*80 + 24).
 *   (2) Row PtrNs: layout_offset field at row+0x28 when non-zero; then each attribute
 *       with NeedsPtrN(NodeFlags,Type) gets Data patched (NF 0x00/0x02/0x0A/…).
 *   RefSpec bodies inside each tVisualEditorData element are hash-inline (BuildRefSpec24);
 *   they do NOT get separate PtrN rows — only the Location u32 does.
 *
 * Side-by-side retail: run `python Dumping Tools/vlt_rows.py <path>.vlt --bin <path>.bin`
 * and compare "PtrN DepRelative(dep=1) patches" + VisualIndicators array decode.
 *
 * Multi-type retail reference: `python Dumping Tools/visual_indicators_attrib_audit.py`
 * (`--summary`, `--payload-invariants`, `--decode <160 nibbles>`).
 *
 * OTS: <c>_chev_*</c> use <c>ribbon_indicator</c> + <c>RibbonIndicatorSecondarySpotKeyHash</c> (D6C61BC5…);
 *       <c>_vis_2..</c> use <c>arrow</c>. <c>_vis_1</c> signup is omitted here (still in PSG; freeskate via
 *       <c>SignUpIndicator</c> on challenge_global_data). Danny Way parity may still list <c>_vis_1</c> on some locals.
 */
