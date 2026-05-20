using DlcBuilder.Builders;
using DlcBuilder.Modules.OtsPsg;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Appends the 7 challengebanks rows for one OTS challenge. Per-instance
/// challenge rows climb the OTS family chain via parent=&lt;framework&gt;_own_the_spots
/// (added once via `OtsFamilyRowsBuilder`). Verified row-for-row against
/// retail Danny Way `dwmc_01` byte dump.
///
/// Rows added (in DW row order):
///   1. challenges/&lt;key&gt;                      parent = &lt;family&gt;
///   2. challenge_global_data/&lt;key&gt;           parent = &lt;family&gt;  — main scoring/UI row
///   3. challenge_objective/&lt;key&gt;             parent = &lt;family&gt;  — anchor
///   4. challenge_objectives_group/&lt;key&gt;      parent = &lt;family&gt;  — discovery anchor (0 attrs)
///   5. challenge_objectives_group/Hash_&lt;owned&gt;     parent = &lt;key&gt; (PointRequirement = OwnedPoints)
///   6. challenge_objectives_group/Hash_&lt;killedit&gt;  parent = &lt;key&gt; (PointRequirement = KilledItPoints)
///   7. challenge_objective/Hash_&lt;def&gt;        parent = &lt;key&gt;  (the objective definition)
public static class OtsChallengeRowsBuilder
{
    private static string HashRowKey(ulong h) => "Hash_" + h.ToString("X16");

    public static void AppendChallengeRows(
        OtsChallengeSpec spec,
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

        string parentKey = $"{frameworkKey}_own_the_spots";

        // ── Row 1: challenges/<key> — 3 attrs ──────────────────────────────
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

        // ── Row 2: challenge_global_data/<key> — full scoring/UI row ───────
        // Keep Location bound to the OTS anchor locator for signup/signpost UI
        // flows, while MapStartLocation points to the authored challenge start
        // locator for challenge launch spawn.
        uint rowGlobalDescPtr    = bin.AddString(spec.DescHalId);
        uint rowGlobalStartLocatorPtr = bin.AddString($"{spec.ChallengeKey}_startlocator");
        uint rowGlobalLocatorPtr = bin.AddString(spec.AnchorName);
        uint rowGlobalTitlePtr   = bin.AddString(spec.TitleHalId);

        // AttribXMLDump `ots_dwmc_01.xml`: MapCategory + World are TypeSize="16"
        // (BuildClassRefSpec — Lookup8(key), 0). Extended24 was 8 bytes past schema;
        // the resolver read into the next bin blob → undefined binds / crashes.
        uint rowGlobalMapCatRef = bin.AddBlob(VltBinHelpers.BuildClassRefSpec(mapCategoryKey));
        uint rowGlobalWorldRef = bin.AddBlob(VltBinHelpers.BuildClassRefSpec(spec.Map.DistKey));

        // ObjectivesOwned / ObjectivesKilledIt — RefSpec[1] arrays linking
        // the row to its per-tier objectives_group rows. Without this the
        // engine never fires Owned/KilledIt state transitions.
        uint ownedObjectivesArr = bin.AddBlob(
            VltBinHelpers.BuildSingleRefSpecArray("challenge_objectives_group",
                HashRowKey(spec.OwnedTierHash)));
        uint killedItObjectivesArr = bin.AddBlob(
            VltBinHelpers.BuildSingleRefSpecArray("challenge_objectives_group",
                HashRowKey(spec.KilledItTierHash)));

        // OTSTriggerBoundary — 16B tTriggerVolumeInstanceID. Verified
        // against retail DW `ots_dwmc_01` (challenge_global_data row,
        // OTSTriggerBoundary GUID = 0x2C701706003D0BAC, identical to
        // ChallengeBoundary GUID on the same row's challenge_local_data —
        // dw_xml/.../ots_dwmc_01.xml + dw_ots_local/.../ots_dwmc_01.xml).
        OtsTriggerVolume? scoringBoundary = spec.Triggers.FirstOrDefault(
            t => t.Name != null && t.Name.Contains("scoringboundary", StringComparison.OrdinalIgnoreCase));
        OtsTriggerVolume? challengeBoundary = spec.Triggers.FirstOrDefault(
            t => t.Name != null && t.Name.Contains("challengeboundary", StringComparison.OrdinalIgnoreCase));
        ulong otsScoringGuid   = scoringBoundary?.GuidLocal   ?? 0UL;
        ulong otsTriggerGuid = challengeBoundary?.GuidLocal ?? 0UL;
        uint otsTriggerBoundaryStub = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(otsTriggerGuid));
        // PtrN patch VolumeName @ +0 (same as challenge_local_data + OtsFamilyRowsBuilder OTSTriggerBoundary).
        // Without this, attrib dump shows 00000000.. + GUID — retail carries the full DIST|…|0x pipe string here.
        uint otsTrigVolNameOff = bin.AddString(challengeBoundary?.Name ?? "");
        binFixups.Add((otsTriggerBoundaryStub, otsTrigVolNameOff));

        // RequiredChallengeHull — IDA: tRequiredChallengeHull = { const char *HullName } sizeof 4.
        // Not tTriggerVolumeInstanceID (0x10: VolumeName + VolumeID). Per-instance DW uses count=1;
        // emit HullName as 0 on disk + PtrN bin fixup → NUL-terminated string (RequiredChallengeHullStringRef).
        uint instHullArr;
        if (!string.IsNullOrWhiteSpace(spec.RequiredChallengeHullStringRef))
        {
            uint hullStrOff = bin.AddString(spec.RequiredChallengeHullStringRef.Trim());
            instHullArr = bin.AddBlob(VltPayload.Build(w =>
            {
                w.WriteBE((ushort)1);
                w.WriteBE((ushort)1);
                w.WriteBE((ushort)4);
                w.WriteBE((ushort)0);
                w.WriteBE(0u);
            }));
            binFixups.Add((instHullArr + 8u, hullStrOff));
        }
        else
            instHullArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(4));

        // Observatory::tObservatoryProgressionReward — TypeSize=16 (AttribCLI schema).
        // Tail is u32 chain pool offset + u32 RefSpec count (not one u64). ots_dwmc_01
        // chains count=1 to progression_rewards; vert_dwgh_01 uses (0,0). We ship (0,0)
        // on OwnedIt to skip follow-up RefSpec iteration (trial for progression crashes).
        uint instKilledItReward = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x0000000000000C35UL); // amount = 3125
            w.WriteBE(0u);                   // chain offset = 0
            w.WriteBE(0u);                   // RefSpec follow-up count = 0
        }));
        uint instOwnedItReward = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x00000000000030D4UL); // amount = 12500
            w.WriteBE(0u);
            w.WriteBE(0u);
        }));

        string[] rowGlobalTypes =
        {
            "EA::Reflection::UInt32",
            "EA::Reflection::Text",
            "Observatory::tObservatoryProgressionReward",
            "Sk8::Challenge::tLocationID",
            "Attrib::Gen::ClassRefSpec_map_category",
            "Attrib::RefSpec",
            "Sk8::Challenge::tTriggerVolumeInstanceID",
            "Sk8::Challenge::tRequiredChallengeHull",
            "EA::Reflection::Bool",
            "Attrib::Gen::ClassRefSpec_world",
            "EA::Reflection::Float",
        };

        // Suppress unused warning for the future-use scoringGuid (kept here so
        // the Triggers.FirstOrDefault search above stays paired with the
        // OTSTriggerBoundary computation — easy to swap if DW shape changes).
        _ = otsScoringGuid;

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_global_data", spec.ChallengeKey, parentKey, 0u,
            new[]
            {
                VltAttribute.InlineRawHash ("Hash_2E4824C81FDAE87C", "EA::Reflection::UInt32",                       0x00000C35u, 0x2E4824C81FDAE87CUL),
                VltAttribute.Inline        ("Description",          "EA::Reflection::Text",                         rowGlobalDescPtr),
                VltAttribute.PointerNoFixup("KilledItReward",       "Observatory::tObservatoryProgressionReward",   instKilledItReward, 0x08),
                VltAttribute.Inline        ("Location",             "Sk8::Challenge::tLocationID",                  rowGlobalLocatorPtr),
                VltAttribute.PointerNoFixup("MapCategory",          "Attrib::Gen::ClassRefSpec_map_category",       rowGlobalMapCatRef,    0x08),
                VltAttribute.Inline        ("MapStartLocation",     "Sk8::Challenge::tLocationID",                  rowGlobalStartLocatorPtr),
                VltAttribute.PointerNoFixup("ObjectivesKilledIt",   "Attrib::RefSpec",                              killedItObjectivesArr, 0x0A),
                VltAttribute.PointerNoFixup("ObjectivesOwned",      "Attrib::RefSpec",                              ownedObjectivesArr,    0x0A),
                VltAttribute.PointerNoFixup("OTSTriggerBoundary",   "Sk8::Challenge::tTriggerVolumeInstanceID",     otsTriggerBoundaryStub, 0x00),
                VltAttribute.PointerNoFixup("OwnedItReward",        "Observatory::tObservatoryProgressionReward",   instOwnedItReward,   0x08),
                VltAttribute.PointerNoFixup("RequiredChallengeHull","Sk8::Challenge::tRequiredChallengeHull",       instHullArr,      0x02),
                VltAttribute.Inline        ("SetManualAndWalkAsConnector","EA::Reflection::Bool",                   0x01000000u),
                VltAttribute.Inline        ("Title",                "EA::Reflection::Text",                         rowGlobalTitlePtr),
                VltAttribute.PointerNoFixup("World",                "Attrib::Gen::ClassRefSpec_world",              rowGlobalWorldRef,     0x08),
            },
            explicitTypes: rowGlobalTypes));
        // ── Row 3: challenge_objective/<key> — anchor (1 attr Name) ────────
        uint rowObjAnchorNamePtr = bin.AddString(spec.ChallengeKey);
        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_objective", spec.ChallengeKey, parentKey, 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", rowObjAnchorNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2));

        // ── Row 4: challenge_objectives_group/<key> — discovery anchor ─────
        collections.Add(VltCollectionBuilder.BuildBareCollection(
            "challenge_objectives_group", spec.ChallengeKey, parentKey));

        // ── Row 5: challenge_objectives_group/Hash_<owned> — Owned tier ────
        AppendObjectivesGroupTier(bin, collections,
            parentChallengeKey: spec.ChallengeKey,
            tierKeyHash: spec.OwnedTierHash,
            pointRequirement: spec.OwnedPoints,
            objectiveDefHash: spec.ObjectiveDefHash);

        // ── Row 6: challenge_objectives_group/Hash_<killedit> — KilledIt ───
        AppendObjectivesGroupTier(bin, collections,
            parentChallengeKey: spec.ChallengeKey,
            tierKeyHash: spec.KilledItTierHash,
            pointRequirement: spec.KilledItPoints,
            objectiveDefHash: spec.ObjectiveDefHash);

        // ── Row 7: challenge_objective/Hash_<def> — objective definition ───
        // tObjectiveDefinition is 20 bytes (verified vs Skate 3 schema +
        // retail DW dlc_dwgh.bin per-OTS Hash_<def>):
        //   +0x00 SourceLua (char* — Lua source string, PtrN-fixed)
        //   +0x04 reserved (zero)
        //   +0x08 Data       (char* — compiled bytecode, here empty string)
        //   +0x0C HALString  (char* — hint HAL ID, here empty)
        //   +0x10 HALStringArguments (char* — args, here empty)
        //
        // **THIS LUA IS WHAT GATES SCORING.** When the OTS challenge becomes
        // active, the engine pumps `PointsValid` (sequence-completion test)
        // every frame; if PointsValid returns true AND the per-objective
        // ObjectiveDefinition Lua also returns true, points accumulate.
        // The ObjectiveDefinition Lua is the GEOMETRIC GATE — by checking
        // `ObjectiveTrigger.EnteredVolume(<guidLocal>, <name>)` against the
        // user-authored scoring boundary, scoring is restricted to the box
        // the user drew.
        //
        // Retail DW shape (for reference):
        //   challenge_objective/Hash_6CDDA8C12C0AFD19 (parent=ots_dwmc_01)
        //   ObjectiveDefinition @ 0x19944 = 00 00 35 B5 00 00 00 00 ...
        //   bin[0x35B5] = `do return ObjectiveTrigger.EnteredVolume(
        //                    "3202084649601665974",
        //                    "DLC_DW_MegaCompund|ots_dwmc_01_spotvolume_1") end`
        // — DW pointed the gate at a third `spotvolume_1` trigger that sat
        // inside its scoringboundary. We collapse that down to a single
        // scoringboundary trigger (see OtsLayout) and rebind the gate here.
        //
        // We bind the Lua to the per-OTS `scoringboundary` trigger by name.
        // OtsLayout sizes that trigger from the user's authored ScoringVolume
        // (centre + half-extents), so EnteredVolume fires exactly when the
        // player crosses INTO the box the user drew as "scoring counts
        // here." Retail DW used a separate `spotvolume_1` trigger as the
        // gate; we collapsed that to scoringboundary because our authoring
        // model only exposes one scoring volume per challenge — a separate
        // gate would either be a duplicate of the same box or a magic-number
        // drift from it.
        uint emptyPathStr = bin.AddString("");
        uint rowObjDefNamePtr = bin.AddString($"{spec.ChallengeKey}_0");

        // Lua name format is `<world>|<canonical>` — NO `|0x<hex>` suffix
        // (we bake the suffix into the PSG-side name; engine matches via the
        // GuidLocal first arg, the name is for debug).
        //
        // Decimal form for the GuidLocal because Lua numbers are 64-bit
        // doubles (max precise integer 2^53). The u64 wouldn't survive as a
        // numeric literal; engine parses the quoted string into a u64
        // internally before comparing against the registered trigger volume.
        OtsTriggerVolume? gateVolume = spec.Triggers.FirstOrDefault(
            t => t.Name != null && t.Name.Contains("scoringboundary", StringComparison.OrdinalIgnoreCase));
        ulong gateGuid = gateVolume?.GuidLocal ?? 0UL;
        string fullName = gateVolume?.Name ?? string.Empty;
        string gateName = fullName;
        int suffixCut = fullName.LastIndexOf("|0x", StringComparison.Ordinal);
        if (suffixCut > 0)
            gateName = fullName.Substring(0, suffixCut);

        string objectiveLua =
            "do return ObjectiveTrigger.EnteredVolume(\""
            + gateGuid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\", \"" + gateName + "\") end";
        uint objectiveLuaStr = bin.AddString(objectiveLua);

        uint rowObjDefBlob = bin.AddBlob(new byte[20]);
        binFixups.Add((rowObjDefBlob + 0x00u, objectiveLuaStr));  // SourceLua
        binFixups.Add((rowObjDefBlob + 0x08u, emptyPathStr));      // Data
        binFixups.Add((rowObjDefBlob + 0x0Cu, emptyPathStr));      // HALString
        binFixups.Add((rowObjDefBlob + 0x10u, emptyPathStr));      // HALStringArguments

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_objective", HashRowKey(spec.ObjectiveDefHash), spec.ChallengeKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Name",                "EA::Reflection::Text",                   rowObjDefNamePtr),
                VltAttribute.Inline        ("ActivationMode",      "Sk8::Challenge::eTriggerActivationMode", 0x05u),
                VltAttribute.PointerNoFixup("ObjectiveDefinition", "Sk8::Challenge::tObjectiveDefinition",   rowObjDefBlob, 0x00),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Sk8::Challenge::eTriggerActivationMode",
                "Sk8::Challenge::tObjectiveDefinition",
            },
            numTypesDup: 4));
    }

    private static void AppendObjectivesGroupTier(
        BinPoolBuilder bin,
        List<CollectionBlob> collections,
        string parentChallengeKey,
        ulong tierKeyHash,
        int pointRequirement,
        ulong objectiveDefHash)
    {
        uint objectiveRefArr = bin.AddBlob(
            VltBinHelpers.BuildSingleRefSpecArray("challenge_objective", HashRowKey(objectiveDefHash)));

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_objectives_group",
            HashRowKey(tierKeyHash),
            parentChallengeKey,
            0u,
            new[]
            {
                VltAttribute.PointerNoFixup("Objectives",       "Attrib::RefSpec",       objectiveRefArr, 0x0A),
                VltAttribute.Inline        ("PointRequirement", "EA::Reflection::Int32", (uint)pointRequirement),
            },
            explicitTypes: new[]
            {
                "Attrib::RefSpec",
                "EA::Reflection::Int32",
            }));
    }
}
