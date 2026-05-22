using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Appends the Race family rows to the DLC challengebanks vault. Mirrors
/// <see cref="OtsFamilyRowsBuilder"/> for the OTS pipeline — every per-race
/// instance row chains through these family anchors during challenge
/// construction.
///
/// 2 rows added per DLC (byte target:
/// `AttribDumpOut/dlc_dwgh_challengebanks/Dump/Skate3_skater/Collections/`):
///
///   challenges/&lt;framework&gt;_races              4 attrs (Name + 3 refspecs)
///   challenge_global_data/&lt;framework&gt;_races   ~25 attrs (death-race UI + identity)
///
/// `GlobalData` / `LocalData` / `StateGraph` refspecs route to base-game stock
/// rows (`challenge_global_data/races`, `challenge_local_data/races`,
/// `challenge_stategraph/race_shared`) so the engine has guaranteed-resolved
/// references during construction. The death-race UI strings
/// (`ID_MISSION_TEMPLATE_DEATH_RACE_TITLE`,
/// `ID_MISSION_DEATHRACE_CHALLENGE_DESCRIPTION`) live on the
/// `challenge_global_data` row and are mirrored verbatim from stock retail's
/// `challenge_global_data/races` row. Per-instance race rows still ship as
/// the parent chain that engine construction walks.
///
/// Note: stock Danny Way ships ONLY these 2 family rows for race (verified
/// by inspecting `AttribDumpOut/dlc_dwgh_challengebanks/`) — unlike OTS
/// (4 family rows incl. `challenge_objective` / `challenge_objectives_group`).
/// Race tier-completion uses the heat's `KilledItTime` + heat timer, not
/// objective tracking, so the objective anchors aren't needed.
public static class RaceFamilyRowsBuilder
{
    /// `Sk8::Challenge::eChallengeTypes` enum value for races. Verified from
    /// stock `challenge_global_data/races.xml`:
    /// `<ChallengeType>00000013</ChallengeType>`.
    private const uint RaceChallengeType = 0x13;

    /// `Sk8::Challenge::eGlobalType` for races. Stock dump shows `00000003`.
    private const uint RaceGlobalType = 0x03;

    /// Anonymous attribute key hashes — these appear in retail dumps as
    /// `Hash_XXX` because their canonical string names aren't in AttribCLI's
    /// Keys.txt. They MUST be emitted via *RawHash overloads so the stored
    /// FieldKeyHash matches the engine's lookup at runtime.
    private const ulong RawHash_B7D4C152 = 0xB7D4C1528E49806DUL;  // bool, race-specific flag
    private const ulong RawHash_713D1933 = 0x713D193371A2B4E6UL;  // Text → death-race description HALID
    private const ulong RawHash_59D3D499 = 0x59D3D4996319A190UL;  // Float (stock: 0x42F00000 = 120.0f)
    private const ulong RawHash_598056F3 = 0x598056F37D38D27AUL;  // tCompetitorInfo[] (race competitors)

    /// `Sk8::Challenge::tChallengeIconDefinition` for race. Verified against
    /// retail Danny Way `challenge_global_data/dlc_dwgh_races.xml`:
    /// `F788504A8D922CEDBBE9BE24D291856D0000000000000000`.
    /// The atlas hash `F788504A8D922CED` is base-game (shared with OTS / others).
    private const ulong RaceChallengeIconAtlas = 0xF788504A8D922CEDUL;
    private const ulong RaceChallengeIconSlot  = 0xBBE9BE24D291856DUL;

    /// `SignUpIndicator` Attrib::RefSpec target — verified retail value.
    /// First qword is Lookup8("ribbon_indicator"); second is the death-race
    /// ribbon variant key (canonical name not in Keys.txt; stored as raw hash).
    private const ulong RaceSignUpIndicatorClass = 0x3297556BECB605E2UL;  // "ribbon_indicator"
    private const ulong RaceSignUpIndicatorKey   = 0x7EC4660FEDCDE306UL;  // death-race variant

    public static void AppendChallengeBanksFamilyRows(
        string frameworkKey,
        BinPoolBuilder bin,
        List<(uint, uint)> binFixups,
        uint emptyPathStr,
        List<CollectionBlob> collections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(binFixups);
        ArgumentNullException.ThrowIfNull(collections);

        string familyKey = $"{frameworkKey}_races";

        // ── Row 1: challenges/<framework>_races — 4 attrs ─────────────────
        // Identity row: routes GlobalData / LocalData / StateGraph to stock
        // base-game rows. Verified shape from retail Danny Way
        // `challenges/dlc_dwgh_races.xml`:
        //   GlobalData  = RefSpec24(challenge_global_data, races, 0)
        //   LocalData   = VaultedRefSpec(challenge_local_data, races, …, path)
        //   StateGraph  = VaultedRefSpec(challenge_stategraph, race_shared, …, path)
        uint famNamePtr = bin.AddString(familyKey);
        uint famGlobalDataOff = bin.AddBlob(
            VltBinHelpers.BuildRefSpec24("challenge_global_data", "races"));
        uint famLocalDataOff = VaultedRefSpecHelper.AddVaultedRefSpecWithPath(
            bin, binFixups, "challenge_local_data", "races");
        uint famStateGraphOff = VaultedRefSpecHelper.AddVaultedRefSpecWithPath(
            bin, binFixups, "challenge_stategraph", "race_shared");

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenges", familyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Name",       "EA::Reflection::Text",            famNamePtr),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec",                 famGlobalDataOff, 0x08),
                VltAttribute.PointerNoFixup("LocalData",  "AttribSysUtils::tVaultedRefSpec", famLocalDataOff,  0x08),
                VltAttribute.PointerNoFixup("StateGraph", "AttribSysUtils::tVaultedRefSpec", famStateGraphOff, 0x08),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Attrib::RefSpec",
                "AttribSysUtils::tVaultedRefSpec",
            },
            numTypesDup: 4));

        // ── Row 2: challenge_global_data/<framework>_races — ~25 attrs ────
        // The big one. Carries the death-race UI strings (`Title`, `HudTitle`,
        // `Hash_713D1933…` Description) + identity (`ChallengeType=0x13`,
        // `Global=0x03`) + UI assets (`ChallengeIcon`, `SignUpIndicator`,
        // `OnlineHUDPreloads`, `Competitors`). Every per-race instance row
        // inherits these via the parent chain
        // `<race_key>` → `<framework>_races` → `<framework>` → `default`.
        uint famDeathRaceTitlePtr = bin.AddString("ID_MISSION_TEMPLATE_DEATH_RACE_TITLE");
        uint famDeathRaceDescPtr  = bin.AddString("ID_MISSION_DEATHRACE_CHALLENGE_DESCRIPTION");

        // ChallengeIcon: 24B blob (16B + 8B zero). Same atlas+slot as retail.
        uint famChallengeIconOff = bin.AddBlob(
            VltBinHelpers.BuildChallengeIconDefinitionDw24(RaceChallengeIconAtlas, RaceChallengeIconSlot));

        // MapCategory: 16B ClassRefSpec_map_category targeting stock `races`
        // map_category row (Lookup8("races") = 0x5A2CAFA61416F9D3).
        uint famMapCategoryOff = bin.AddBlob(VltBinHelpers.BuildClassRefSpec("races"));

        // SignUpIndicator: 24B RefSpec with precomputed hashes (canonical key
        // names not in our Keys.txt corpus).
        uint famSignUpIndicatorOff = bin.AddBlob(
            VltBinHelpers.BuildRefSpec24(RaceSignUpIndicatorClass, RaceSignUpIndicatorKey));

        // OnlineHUDPreloads: array of 8 Sk8::HUD::eHUDComponent u32 BE values.
        // Retail values (preserves runtime HUD prefetch ordering — engine
        // walks them in this order during heat warmup):
        //   3, 0, 1, 9, 7, 0x17, 0x18, 0x0E
        uint famOnlineHudPreloadsOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)8);   // count
            w.WriteBE((ushort)8);   // capacity
            w.WriteBE((ushort)4);   // typeSize (u32 enum)
            w.WriteBE((ushort)0);   // align
            w.WriteBE(0x00000003U);
            w.WriteBE(0x00000000U);
            w.WriteBE(0x00000001U);
            w.WriteBE(0x00000009U);
            w.WriteBE(0x00000007U);
            w.WriteBE(0x00000017U);
            w.WriteBE(0x00000018U);
            w.WriteBE(0x0000000EU);
        }));

        // PreloadAssets: 1-element Sk8::Challenge::tAttributeLink array.
        // Retail value: u32 0x00000914 (a bin-pool offset to a stock asset
        // name string). Best-effort — until we know what string lives at
        // 0x914 in DW's bin, we point at emptyPathStr; the engine treats
        // missing preloads as "load nothing" and proceeds.
        uint famPreloadAssetsOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)4);   // tAttributeLink stride = 4B (u32 hash)
            w.WriteBE((ushort)0);
            w.WriteBE(0u);          // PtrN-fixed to a bin string below
            w.WriteBE(0u);          // pad to 8B alignment
        }));
        binFixups.Add((famPreloadAssetsOff + 8u, emptyPathStr));

        // Hash_598056F37D38D27A: tCompetitorInfo[3]. Each entry is 8B —
        //   { u32 NameStrPtr, u32 pad }
        // NameStrPtr is a bin-pool offset to an `ai_characters` row key string.
        // The race state-graph (race_shared.vlt → triggerentercollision Lua)
        // iterates this array and calls `sub_F23380` (strcmp) on each name
        // against rows in the `ai_characters` collection (classKey
        // 0xE57478F796EBC28C) to assemble the AI competitor roster for
        // online death races.
        //
        // Earlier this builder wrote the LITERAL offsets `0x927 / 0x937 /
        // 0x944` from a stock retail dump — those happen to be the offsets
        // of "dennis_busenitz" / "mike_carroll" / "lucas_puig" in stock
        // dlc_dwgh.bin (133 KB). Our challengebanks bin is ~2 KB so those
        // offsets are past EOF; the engine reads raw 0x927 as a char* and
        // strcmp's it against "chris_boykin", "m_street", ... → AV reading
        // 0x927 the moment a player picks the race online (PPU
        // load_thread crash). That's the crash signature we chased through
        // the race-state-graph bytecode.
        //
        // Fix: add the three skater row-key strings to our bin pool and
        // PtrN-fix each tCompetitorInfo.NameStrPtr slot to its offset.
        // ai_characters/dennis_busenitz, mike_carroll, lucas_puig all exist
        // in stock skatercollections — they don't need shipping with DLC.
        uint comp1NameOff = bin.AddString("dennis_busenitz");
        uint comp2NameOff = bin.AddString("mike_carroll");
        uint comp3NameOff = bin.AddString("lucas_puig");
        uint famCompetitorsOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)3);
            w.WriteBE((ushort)3);
            w.WriteBE((ushort)8);   // tCompetitorInfo stride = 8B
            w.WriteBE((ushort)0);
            w.WriteBE(comp1NameOff); w.WriteBE(0u);
            w.WriteBE(comp2NameOff); w.WriteBE(0u);
            w.WriteBE(comp3NameOff); w.WriteBE(0u);
        }));
        // PtrN fixups for each NameStrPtr slot. Array header is 8B; element
        // 0's NameStrPtr is at +8, element 1's at +16, element 2's at +24.
        binFixups.Add((famCompetitorsOff +  8u, comp1NameOff));
        binFixups.Add((famCompetitorsOff + 16u, comp2NameOff));
        binFixups.Add((famCompetitorsOff + 24u, comp3NameOff));

        // MapStartLocation tLocationID — retail value 0x8 (emptyPathStr).
        // Location tLocationID — retail value 0x8E0 (a specific DW bin string).
        // Best-effort: point both at emptyPathStr for now; the per-instance
        // race row's `Location` / `MapStartLocation` overrides this anyway.
        // (Stock retail's specific 0x8E0 pointer would matter for FE preview
        //  when no per-instance override exists — non-issue while we always
        //  ship per-instance rows.)

        var famGlobalDataTypes = new[]
        {
            "EA::Reflection::Bool",
            "Sk8::Camera::eChallengeCameraOverride",
            "Sk8::Challenge::eChallengeAssetLoadType",
            "Sk8::Challenge::tChallengeIconDefinition",
            "EA::Reflection::UInt8",
            "Sk8::Challenge::eChallengeTypes",
            "EA::Reflection::Text",
            "Sk8::Challenge::eGlobalType",
            "EA::Reflection::Float",
            "Sk8::Challenge::tLocationID",
            "Attrib::Gen::ClassRefSpec_map_category",
            "Sk8::HUD::eHUDComponent",
            "Sk8::Challenge::tAttributeLink",
            "Attrib::RefSpec",
            "Sk8::Challenge::tCompetitorInfo",
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_global_data", familyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline       ("AvailableOnline",             "EA::Reflection::Bool",                     0x01000000u),
                VltAttribute.Inline       ("CameraOverride",              "Sk8::Camera::eChallengeCameraOverride",    0u),
                VltAttribute.Inline       ("ChallengeAssetLoadType",      "Sk8::Challenge::eChallengeAssetLoadType",  0u),
                VltAttribute.PointerNoFixup("ChallengeIcon",              "Sk8::Challenge::tChallengeIconDefinition", famChallengeIconOff, 0x08),
                VltAttribute.Inline       ("ChallengeIndex",              "EA::Reflection::UInt8",                    0x01000000u),
                VltAttribute.Inline       ("ChallengeInfoShowObjectives", "EA::Reflection::Bool",                     0x01000000u),
                VltAttribute.Inline       ("ChallengeType",               "Sk8::Challenge::eChallengeTypes",          RaceChallengeType),
                VltAttribute.InlineRawHash("Hash_B7D4C1528E49806D",       "EA::Reflection::Bool",                     0x01000000u, RawHash_B7D4C152),
                VltAttribute.Inline       ("DebugOnly",                   "EA::Reflection::Bool",                     0u),
                VltAttribute.Inline       ("Description",                 "EA::Reflection::Text",                     emptyPathStr),
                VltAttribute.InlineRawHash("Hash_713D193371A2B4E6",       "EA::Reflection::Text",                     famDeathRaceDescPtr, RawHash_713D1933),
                VltAttribute.Inline       ("DmoTreatAsStatic",            "EA::Reflection::Bool",                     0x01000000u),
                VltAttribute.Inline       ("EndTeleport",                 "EA::Reflection::Bool",                     0u),
                VltAttribute.Inline       ("Global",                      "Sk8::Challenge::eGlobalType",              RaceGlobalType),
                VltAttribute.Inline       ("HudTitle",                    "EA::Reflection::Text",                     famDeathRaceTitlePtr),
                // Hash_59D3D499 is a float (stock 0x42F00000 = 120.0f);
                // semantically race-related (heat KilledIt threshold default).
                VltAttribute.InlineRawHash("Hash_59D3D4996319A190",       "EA::Reflection::Float",                    0x42F00000u, RawHash_59D3D499),
                VltAttribute.Inline       ("Location",                    "Sk8::Challenge::tLocationID",              emptyPathStr),
                VltAttribute.PointerNoFixup("MapCategory",                "Attrib::Gen::ClassRefSpec_map_category",   famMapCategoryOff,   0x08),
                VltAttribute.Inline       ("MapStartLocation",            "Sk8::Challenge::tLocationID",              emptyPathStr),
                VltAttribute.PointerNoFixup("OnlineHUDPreloads",          "Sk8::HUD::eHUDComponent",                  famOnlineHudPreloadsOff, 0x02),
                VltAttribute.PointerNoFixup("PreloadAssets",              "Sk8::Challenge::tAttributeLink",           famPreloadAssetsOff,     0x02),
                VltAttribute.PointerNoFixup("SignUpIndicator",            "Attrib::RefSpec",                          famSignUpIndicatorOff,   0x08),
                VltAttribute.PointerNoFixupRawHash("Hash_598056F37D38D27A","Sk8::Challenge::tCompetitorInfo",         famCompetitorsOff,       0x02, RawHash_598056F3),
                VltAttribute.Inline       ("Teams",                       "EA::Reflection::Bool",                     0u),
                VltAttribute.Inline       ("Title",                       "EA::Reflection::Text",                     famDeathRaceTitlePtr),
            },
            explicitTypes: famGlobalDataTypes));
    }
}
