using DlcBuilder.Builders;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Appends the per-race challengebanks rows for one Race challenge. Mirrors
/// <see cref="OtsChallengeRowsBuilder"/> for the OTS pipeline.
///
/// 2 rows per race (vs OTS's 7 — race doesn't use the objective-tracker
/// hierarchy, scoring is heat-time-based):
///
///   1. challenges/&lt;key&gt;            parent = &lt;framework&gt;_races
///                                      Name + GlobalData → challenge_global_data/&lt;key&gt;
///                                                       + LocalData → challenge_local_data/&lt;key&gt;.vlt
///   2. challenge_global_data/&lt;key&gt; parent = &lt;framework&gt;_races
///                                      Per-race overrides: Title (per-race HALID),
///                                      Location / MapStartLocation, MapCategory,
///                                      World. Everything else inherits from the
///                                      `&lt;framework&gt;_races` family row emitted
///                                      by <see cref="RaceFamilyRowsBuilder"/>
///                                      (ChallengeIcon, SignUpIndicator,
///                                      OnlineHUDPreloads, Competitors,
///                                      ChallengeType=0x13, Global=0x03, death-race
///                                      UI strings).
///
/// Byte target (per-instance shape, not full attribute set):
///   `AttribDumpOut/dlc_dwgh_challengebanks/Dump/Skate3_skater/Collections/
///   challenges/Hash_1DC1818A06DFDA51.xml`  (race_dwgh_01)
///   `AttribDumpOut/dlc_dwgh_challengebanks/Dump/Skate3_skater/Collections/
///   challenge_global_data/Hash_1DC1818A06DFDA51.xml`
///
/// PtrN / DepN linkage (handled automatically by the VLT infrastructure):
///   • Every <c>EA::Reflection::Text</c> / <c>tLocationID</c> / <c>RefSpec</c> /
///     <c>tVaultedRefSpec</c> / <c>ClassRefSpec_*</c> attribute is routed
///     through <see cref="VltAttributeFlags.NeedsPtrN"/> → automatic PtrN
///     entry against the bin pool.
///   • LocalData's tVaultedRefSpec blob carries an internal +0x18 PtrN to its
///     <c>"challenge_local_data\&lt;key&gt;.vlt"</c> path string (added by
///     <see cref="VaultedRefSpecHelper.AddVaultedRefSpecWithPath"/>) — engine's
///     <c>sub_A6D400</c> path dispatch reads it to construct
///     <c>"data/db/challenge_local_data/&lt;key&gt;.vlt"</c>.
///   • DepN (file-level dependency table) is fixed at 2 entries — the .vlt
///     itself + the companion .bin — handled by
///     <see cref="VltFileWriter.BuildDependencyPayload"/>; no per-row work.
public static class RaceChallengeRowsBuilder
{
    public static void AppendChallengeRows(
        RaceChallengeSpec spec,
        string frameworkKey,
        string mapCategoryKey,
        BinPoolBuilder bin,
        List<(uint, uint)> binFixups,
        List<CollectionBlob> collections)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapCategoryKey);
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(binFixups);
        ArgumentNullException.ThrowIfNull(collections);

        string parentKey = $"{frameworkKey}_races";

        // ── Row 1: challenges/<key> — 3 attrs ──────────────────────────────
        // Identity row. Verified shape against retail Danny Way
        // `challenges/Hash_1DC1818A06DFDA51.xml` (race_dwgh_01) — same
        // 3-attr layout as OTS instance rows in OtsChallengeRowsBuilder.cs.
        //
        // PtrN linkage:
        //   • Name (Text)            → PtrN to bin string (auto via NF=0x40, Text)
        //   • GlobalData (RefSpec)   → PtrN to 24B blob (auto via NF=0x08)
        //   • LocalData (VaultedRef) → PtrN to 32B blob (auto via NF=0x08)
        //                              The blob itself has +0x18 PtrN to its
        //                              path string (registered by
        //                              VaultedRefSpecHelper.AddVaultedRefSpecWithPath).
        uint rowChallengesNamePtr = bin.AddString(spec.ChallengeKey);
        uint rowChallengesGlobalDataOff = bin.AddBlob(
            VltBinHelpers.BuildRefSpec24("challenge_global_data", spec.ChallengeKey));
        uint rowChallengesLocalDataOff = VaultedRefSpecHelper.AddVaultedRefSpecWithPath(
            bin, binFixups, "challenge_local_data", spec.ChallengeKey);

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenges", spec.ChallengeKey, parentKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Name",       "EA::Reflection::Text",            rowChallengesNamePtr),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec",                 rowChallengesGlobalDataOff, 0x08),
                VltAttribute.PointerNoFixup("LocalData",  "AttribSysUtils::tVaultedRefSpec", rowChallengesLocalDataOff,  0x08),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Attrib::RefSpec",
                "AttribSysUtils::tVaultedRefSpec",
            },
            numTypesDup: 4));

        // ── Row 2: challenge_global_data/<key> — per-race overrides ──────
        // Slim row carrying ONLY what's race-instance-specific. Retail Danny
        // Way's `Hash_1DC1818A06DFDA51` ships 22 attrs (incl. AudioPlayer audio,
        // Competitors array, Vector3 camera position, etc.) — most of those
        // can be inherited from the `<framework>_races` family row, which
        // already carries the death-race defaults. We only override what the
        // family CAN'T provide per-race:
        //   • Title           — per-race HAL ID (ID_CHALLENGE_<KEY>_TITLE)
        //   • Location        — per-race start-location bin string
        //   • MapStartLocation — same
        //   • MapCategory     — per-map ClassRefSpec_map_category
        //   • World           — per-map ClassRefSpec_world
        //   • RequiredChallengeHull — single tRequiredChallengeHull{HullName=challengeKey}
        //
        // If runtime needs more retail fidelity (Competitors override, custom
        // Vector3 camera placement, etc.), add them here in a later slice —
        // the family row's defaults are accepted by the engine in the
        // meantime.
        //
        // PtrN linkage:
        //   • Title / Location / MapStartLocation are tLocationID + Text — bin
        //     pointers, auto-PtrN'd.
        //   • MapCategory / World are ClassRefSpec_* 16B blobs, NF=0x08
        //     → PtrN to the blob.
        //   • RequiredChallengeHull is a tRequiredChallengeHull[1] array; the
        //     element's HullName slot at +0 inside each 4B entry is PtrN-fixed
        //     to a bin string.
        uint rowGlobalTitlePtr           = bin.AddString(spec.TitleHalId);
        uint rowGlobalLocationPtr        = bin.AddString($"{spec.ChallengeKey}_startlocator");
        uint rowGlobalMapCatRef          = bin.AddBlob(VltBinHelpers.BuildClassRefSpec(mapCategoryKey));
        uint rowGlobalWorldRef           = bin.AddBlob(VltBinHelpers.BuildClassRefSpec(spec.Map.DistKey));

        // RequiredChallengeHull — 1-element tRequiredChallengeHull array. Each
        // element is `const char *HullName` (4B). On disk: 8B array header
        // (count=1, cap=1, typeSize=4) + 4B name ptr (PtrN-fixed) + 4B tail pad.
        uint rowGlobalChallengeKeyStr    = bin.AddString(spec.ChallengeKey);
        uint rowGlobalRequiredHullArr    = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)4);   // tRequiredChallengeHull stride = 4B (sizeof = 0x4 per IDA)
            w.WriteBE((ushort)0);
            w.WriteBE(0u);          // HullName ptr — PtrN-fixed below
            w.WriteBE(0u);          // pad to 8B alignment
        }));
        // PtrN fixup: HullName slot at +8 inside the blob (after array header).
        binFixups.Add((rowGlobalRequiredHullArr + 8u, rowGlobalChallengeKeyStr));

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_global_data", spec.ChallengeKey, parentKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Title",                 "EA::Reflection::Text",                   rowGlobalTitlePtr),
                VltAttribute.Inline        ("Location",              "Sk8::Challenge::tLocationID",            rowGlobalLocationPtr),
                VltAttribute.Inline        ("MapStartLocation",      "Sk8::Challenge::tLocationID",            rowGlobalLocationPtr),
                VltAttribute.PointerNoFixup("MapCategory",           "Attrib::Gen::ClassRefSpec_map_category", rowGlobalMapCatRef, 0x08),
                VltAttribute.PointerNoFixup("World",                 "Attrib::Gen::ClassRefSpec_world",        rowGlobalWorldRef,  0x08),
                VltAttribute.PointerNoFixup("RequiredChallengeHull", "Sk8::Challenge::tRequiredChallengeHull", rowGlobalRequiredHullArr, 0x02),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Sk8::Challenge::tLocationID",
                "Attrib::Gen::ClassRefSpec_map_category",
                "Attrib::Gen::ClassRefSpec_world",
                "Sk8::Challenge::tRequiredChallengeHull",
            }));
    }
}
