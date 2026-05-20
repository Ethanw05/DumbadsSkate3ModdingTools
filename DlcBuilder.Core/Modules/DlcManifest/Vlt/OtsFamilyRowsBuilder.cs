using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Appends the OTS family rows to the challengebanks vault. Per-instance OTS
/// challenges chain through these rows — the family is the inheritance anchor
/// the engine walks during challenge construction.
///
/// 4 rows added (DW byte-for-byte verified):
///   challenges/&lt;framework&gt;_own_the_spots                4 attrs
///   challenge_global_data/&lt;framework&gt;_own_the_spots     38 attrs (the OTS template)
///   challenge_objective/&lt;framework&gt;_own_the_spots        6 attrs
///   challenge_objectives_group/&lt;framework&gt;_own_the_spots 0 attrs (anchor)
///
/// GlobalData / LocalData / StateGraph refspecs route to base-game stock rows
/// (always loaded via challengebanks/main.vlt) — gives the engine guaranteed-
/// resolved references during construction. Per-DLC rows still ship as the
/// parent chain that per-instance OTS rows climb.
public static class OtsFamilyRowsBuilder
{
    private const uint OtsChallengeType = 9;
    private const ulong RawHashC1FC = 0xC1FC992A38DB8136UL;

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

        string familyKey = $"{frameworkKey}_own_the_spots";

        // ── Row 1: challenges/<family> — 4 attrs ───────────────────────────
        // GlobalData / LocalData / StateGraph route to base-game `own_the_spots`
        // rows, NOT our per-DLC rows. Without this routing the engine never
        // mounted our challenge_local_data .bin so trigger-volume strings
        // never reached memory and cMsgEnterDiscoveryVolume never fired.
        // StateGraph → challenge_stategraph/ownthespot_shared_mlui (the shared
        // OTS state machine, 39 challenge_messagehandlers). DW points at the
        // same row.
        uint famNamePtr = bin.AddString(familyKey);
        uint famGlobalDataOff = bin.AddBlob(
            VltBinHelpers.BuildRefSpec24("challenge_global_data", "own_the_spots"));
        uint famLocalDataOff = VaultedRefSpecHelper.AddVaultedRefSpecWithPath(
            bin, binFixups, "challenge_local_data", "own_the_spots");
        uint famStateGraphOff = VaultedRefSpecHelper.AddVaultedRefSpecWithPath(
            bin, binFixups, "challenge_stategraph", "ownthespot_shared_mlui");

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenges", familyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Name",       "EA::Reflection::Text",            famNamePtr),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec",                 famGlobalDataOff,  0x08),
                VltAttribute.PointerNoFixup("LocalData",  "AttribSysUtils::tVaultedRefSpec", famLocalDataOff,   0x08),
                VltAttribute.PointerNoFixup("StateGraph", "AttribSysUtils::tVaultedRefSpec", famStateGraphOff,  0x08),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Attrib::RefSpec",
                "AttribSysUtils::tVaultedRefSpec",
            },
            numTypesDup: 4));

        // ── Row 2: challenge_global_data/<family> — 38 attrs OTS template ──
        // Match Danny Way `dlc_dwgh_own_the_spots` AttribCLI dump (documentation/AttribXMLDump_Dannyway).
        uint famDescPtr     = bin.AddString("ID_CHALLENGE_OBJECTIVE_BEAT_OWNSPOT");
        uint famHudTitlePtr = bin.AddString("ID_MAP_OWN_THE_SPOT");

        // Danny Way XML: RefSpec attrs & typed arrays use an 8B ArrayData header;
        // typeSize in the header is the element stride (24 RefSpec, 24 tDMOResetInfo,
        // 4 tRequiredChallengeHull). Count=0 ⇒ header-only blob — same as
        // `BuildEmptyArrayHeader(typeSize)`.
        uint famDMOCensusRangeOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_objective", "dynamicobjects"));
        // IDA: Sk8::Challenge::tObjectiveFlow sizeof=0x8 { eObjectiveFlow FlowType; int MaxActiveObjectives; }
        // (xref cChallengeObjectiveGroup.ObjectiveFlow). One 8B BE chunk — not 16B/24B.
        uint famObjectiveFlowOff = bin.AddBlob(VltPayload.Build(w => w.WriteBE(1UL)));
        // IDA: tDMOResetInfo sizeof=0x18 (Type + pad + tTriggerVolumeInstanceID Volume). Empty array:
        // 8B header only, typeSize=0x18 per documentation/AttribXMLDump_Dannyway/.../dlc_dwgh_own_the_spots.xml.
        uint famDwDmoResetArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        // Empty Attrib::RefSpec[] — XML dump shows no elements for family row.
        uint famDwObjectivesArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        uint famDwObjectivesKilledItArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        uint famDwObjectivesOwnedArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        // IDA: Sk8::Challenge::tRequiredChallengeHull sizeof=0x4 { const char *HullName; }
        // (xref e.g. Sk8::Challenge::tDynamicHullMapping.RequiredHull). Vault stores the name slot as a
        // bin-pool-relative ref PtrN-patched like Text — not tTriggerVolumeInstanceID (0x10:
        // VolumeName + u64 VolumeID). Empty array: header typeSize=4 only.
        uint famDwRequiredHullArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(4));

        // Atlas hash 0xF788504A8D922CED is base-game (Art Gallery DLC ships
        // the same hash). Per-icon hashes mirror the 3 DW uses.
        // XML TypeSize=24 per icon: 16B + 8 zero (matches dlc_dwgh_own_the_spots dump).
        uint famChallengeIcon1Off = bin.AddBlob(VltBinHelpers.BuildChallengeIconDefinitionDw24(
            0xF788504A8D922CEDUL, 0xD4383831503D5608UL));
        uint famChallengeIcon2Off = bin.AddBlob(VltBinHelpers.BuildChallengeIconDefinitionDw24(
            0xF788504A8D922CEDUL, 0xDDA65DAE1676B348UL));
        uint famChallengeIcon3Off = bin.AddBlob(VltBinHelpers.BuildChallengeIconDefinitionDw24(
            0xF788504A8D922CEDUL, 0x05BC0053253C93C0UL));
        // XML TypeSize=16: key hash + zero (not 24B extended; third qword absent).
        uint famMapCategoryOtsOff = bin.AddBlob(VltBinHelpers.BuildClassRefSpec("ots"));
        uint famWorldBamFeLocOff = bin.AddBlob(VltBinHelpers.BuildClassRefSpec("bam"));
        uint famSignUpIndicatorOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("ribbon_indicator", "arrow"));
        uint famWorldLocationOff   = bin.AddBlob(VltBinHelpers.BuildRefSpec24("fe_locations", "default"));

        // ── challenge_objective/<family> payloads ──
        // KilledIt completion plays the post-tier "film" cinematic by reading
        // KilledItHighlightSceneName.SceneName at tHighlightDefinition+0x50,
        // treating it as const char*, and strlen-walking it. NULL there =>
        // page-fault. DW ships BOTH SceneName slots PtrN-fixed to
        // "highlight_generic" (verified vs DannyWayDLC challengebanks bin).
        uint famOtsScoringLua = bin.AddString("do\n\treturn ObjectivePoints.IsSequenceNewlyCompletePoints();\nend");
        uint famHighlightGenericStr = bin.AddString("highlight_generic");

        // tHighlightDefinition (144B). RewindDuration=3.0f at +0x00; 17 zero
        // qwords for the rest. PtrN-fixed at +0x10 and +0x50 for the two
        // SceneName slots.
        uint famHighlightDef = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x40400000U);   // +0x00 RewindDurationInSeconds = 3.0f
            w.WriteBE(0U);            // +0x04 FutureDurationInSeconds = 0
            for (int i = 0; i < 17; i++)
                w.WriteBE(0UL);
        }));
        binFixups.Add((famHighlightDef + 0x10U, famHighlightGenericStr));
        binFixups.Add((famHighlightDef + 0x50U, famHighlightGenericStr));

        // tObjectiveTriggers (20B). byte[1]=SequenceComplete=true.
        uint famObjectiveTriggers = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x0001000000000000UL);   // bytes 0..7 (SequenceComplete=1)
            w.WriteBE(0UL);                    // bytes 8..15
            w.WriteBE(0u);                     // bytes 16..19 (TriggerField enum=0)
        }));

        // tCompiledLua (12B). PtrN: SourceLua → Lua source, Data → empty string.
        uint famPointsValid = bin.AddBlob(new byte[12]);
        binFixups.Add((famPointsValid + 0x00u, famOtsScoringLua));
        binFixups.Add((famPointsValid + 0x08u, emptyPathStr));

        // tObjectiveDefinition (20B). PtrN: SourceLua, Data, HALString,
        // HALStringArguments. Without these the objective-tracker construction
        // path reads NULL pointers.
        uint famObjectiveDef = bin.AddBlob(new byte[20]);
        binFixups.Add((famObjectiveDef + 0x00u, famOtsScoringLua));
        binFixups.Add((famObjectiveDef + 0x08u, emptyPathStr));
        binFixups.Add((famObjectiveDef + 0x0Cu, emptyPathStr));
        binFixups.Add((famObjectiveDef + 0x10u, emptyPathStr));

        // ── Family-level world-coord references ──
        // Stock content always loaded — Location to fe_locations row
        // "f_1a_01_ots_pj01_books_observerlocator", OTSTriggerBoundary to the
        // stock DIST_Old_Town spotfilm volume registered globally in
        // cTriggerVolumeManager.LocalToInstanceMap when the world loads.
        // Per-instance OTS rows override these with their actual anchors.
        uint famStockLocatorPtr    = bin.AddString("f_1a_01_ots_pj01_books_observerlocator");
        uint famStockVolumeNamePtr = bin.AddString("DIST_Old_Town|corex_spotfilm01_challengeboundary01|0x0003fce603e38706");

        uint famOTSTriggerBoundary = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(0x0003FCE603E38706UL));
        binFixups.Add((famOTSTriggerBoundary + 0x00u, famStockVolumeNamePtr));

        uint famAILockOut = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(0UL));
        binFixups.Add((famAILockOut + 0x00u, emptyPathStr));

        // Type table: only types referenced by attrs (DW row has no CompetitorInfo / GlobalSpawn).
        string[] famGlobalDataTypes =
        {
            "Sk8::Challenge::tTriggerVolumeInstanceID",
            "EA::Reflection::Bool",
            "Sk8::Challenge::eChallengeAssetLoadType",
            "Sk8::Challenge::tChallengeIconDefinition",
            "EA::Reflection::UInt8",
            "Sk8::Challenge::eChallengeTypes",
            "EA::Reflection::Text",
            "EA::Reflection::Float",
            "Attrib::RefSpec",
            "Sk8::Challenge::tDMOResetInfo",
            "Sk8::Challenge::ActivityUtils::tActivityLoadMask",
            "Sk8::Challenge::eGlobalType",
            "Sk8::Challenge::tLivingWorldManagement",
            "Sk8::Challenge::tLocationID",
            "Attrib::Gen::ClassRefSpec_map_category",
            "Sk8::Challenge::tObjectiveFlow",
            "Sk8::Challenge::tRequiredChallengeHull",
            "Attrib::Gen::ClassRefSpec_world",
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_global_data", familyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.PointerNoFixup ("AILockOut",                    "Sk8::Challenge::tTriggerVolumeInstanceID",         famAILockOut,           0x00),
                VltAttribute.InlineRawHash  ("Hash_C1FC992A38DB8136",        "EA::Reflection::Bool",                             0x01000000u, RawHashC1FC),
                VltAttribute.Inline         ("AvailableOnline",              "EA::Reflection::Bool",                             0x01000000u),
                VltAttribute.Inline         ("ChallengeAssetLoadType",       "Sk8::Challenge::eChallengeAssetLoadType",          0x00000001u),
                VltAttribute.PointerNoFixup ("ChallengeIcon",                "Sk8::Challenge::tChallengeIconDefinition",         famChallengeIcon1Off,   0x08),
                VltAttribute.PointerNoFixup ("ChallengeIcon2",               "Sk8::Challenge::tChallengeIconDefinition",         famChallengeIcon2Off,   0x08),
                VltAttribute.PointerNoFixup ("ChallengeIcon3",               "Sk8::Challenge::tChallengeIconDefinition",         famChallengeIcon3Off,   0x08),
                VltAttribute.Inline         ("ChallengeIndex",               "EA::Reflection::UInt8",                            0x01000000u),
                VltAttribute.Inline         ("ChallengeInfoShowObjectives",  "EA::Reflection::Bool",                             0x01000000u),
                VltAttribute.Inline         ("ChallengeType",                "Sk8::Challenge::eChallengeTypes",                  OtsChallengeType),
                VltAttribute.Inline         ("DebugOnly",                    "EA::Reflection::Bool",                             0u),
                VltAttribute.Inline         ("Description",                  "EA::Reflection::Text",                             famDescPtr),
                VltAttribute.Inline         ("DiscoveryActivateRadius",      "EA::Reflection::Float",                            0x42C80000u /*100.0*/),
                VltAttribute.PointerNoFixup ("DMOCensusRange",               "Attrib::RefSpec",                                  famDMOCensusRangeOff,   0x08),
                VltAttribute.PointerNoFixup ("DmoResetOperations",           "Sk8::Challenge::tDMOResetInfo",                    famDwDmoResetArr,       0x02),
                VltAttribute.Inline         ("DynamicHullEssential",         "EA::Reflection::Bool",                             0u),
                VltAttribute.Inline         ("DynamicHullGameMode",          "Sk8::Challenge::ActivityUtils::tActivityLoadMask", 0x00000004u),
                VltAttribute.Inline         ("DynamicHullRadius",            "EA::Reflection::Float",                            0x437A0000u /*250.0*/),
                VltAttribute.Inline         ("Global",                       "Sk8::Challenge::eGlobalType",                      0x00000003u),
                VltAttribute.Inline         ("GlobalActivateRadius",         "EA::Reflection::Float",                            0x41200000u /*10.0*/),
                VltAttribute.Inline         ("HudTitle",                     "EA::Reflection::Text",                             famHudTitlePtr),
                VltAttribute.Inline         ("LivingWorldManagement",        "Sk8::Challenge::tLivingWorldManagement",           0x0000003Fu),
                VltAttribute.Inline         ("Location",                     "Sk8::Challenge::tLocationID",                      famStockLocatorPtr),
                VltAttribute.Inline         ("Lock30FPS",                    "EA::Reflection::Bool",                             0u),
                VltAttribute.PointerNoFixup ("MapCategory",                  "Attrib::Gen::ClassRefSpec_map_category",           famMapCategoryOtsOff,   0x08),
                VltAttribute.Inline         ("MapStartLocation",             "Sk8::Challenge::tLocationID",                      emptyPathStr),
                VltAttribute.Inline         ("ModeAvailability",             "Sk8::Challenge::ActivityUtils::tActivityLoadMask", 0x0000001Fu),
                VltAttribute.PointerNoFixup ("ObjectiveFlow",                "Sk8::Challenge::tObjectiveFlow",                   famObjectiveFlowOff,    0x00),
                VltAttribute.PointerNoFixup ("Objectives",                   "Attrib::RefSpec",                                  famDwObjectivesArr,     0x0A),
                VltAttribute.PointerNoFixup ("ObjectivesKilledIt",           "Attrib::RefSpec",                                  famDwObjectivesKilledItArr, 0x0A),
                VltAttribute.PointerNoFixup ("ObjectivesOwned",              "Attrib::RefSpec",                                  famDwObjectivesOwnedArr, 0x0A),
                VltAttribute.PointerNoFixup ("OTSTriggerBoundary",           "Sk8::Challenge::tTriggerVolumeInstanceID",         famOTSTriggerBoundary,  0x00),
                VltAttribute.PointerNoFixup ("RequiredChallengeHull",        "Sk8::Challenge::tRequiredChallengeHull",           famDwRequiredHullArr,   0x02),
                VltAttribute.Inline         ("SetManualAndWalkAsConnector",  "EA::Reflection::Bool",                             0x01000000u),
                VltAttribute.PointerNoFixup ("SignUpIndicator",              "Attrib::RefSpec",                                  famSignUpIndicatorOff,  0x08),
                // DW row points Title at bin "" (offset 0x8), not data=0 — PtrN would
                // otherwise fix attr Data to pool base and CAC/lang paths see NULL.
                VltAttribute.Inline         ("Title",                        "EA::Reflection::Text",                             emptyPathStr),
                VltAttribute.PointerNoFixup ("World",                        "Attrib::Gen::ClassRefSpec_world",                  famWorldBamFeLocOff,    0x08),
                VltAttribute.PointerNoFixup ("WorldLocation",                "Attrib::RefSpec",                                  famWorldLocationOff,    0x08),
            },
            explicitTypes: famGlobalDataTypes));

        // ── Row 3: challenge_objective/<family> — 6 attrs ──────────────────
        uint famObjAnchorPtr = bin.AddString(familyKey);
        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_objective", familyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Name",                "EA::Reflection::Text",                  famObjAnchorPtr),
                VltAttribute.PointerNoFixup("HighlightDefinition", "Sk8::Challenge::tHighlightDefinition",  famHighlightDef,      0x00),
                VltAttribute.PointerNoFixup("ObjectiveDefinition", "Sk8::Challenge::tObjectiveDefinition",  famObjectiveDef,      0x00),
                VltAttribute.PointerNoFixup("ObjectiveTriggers",   "Sk8::Challenge::tObjectiveTriggers",    famObjectiveTriggers, 0x00),
                VltAttribute.PointerNoFixup("PointsValid",         "LuaState::tCompiledLua",                famPointsValid,       0x00),
                VltAttribute.Inline        ("RequiresHighlight",   "EA::Reflection::Bool",                  0x01000000u),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Sk8::Challenge::tHighlightDefinition",
                "Sk8::Challenge::tObjectiveDefinition",
                "Sk8::Challenge::tObjectiveTriggers",
                "LuaState::tCompiledLua",
                "EA::Reflection::Bool",
            }));

        // ── Row 4: challenge_objectives_group/<family> — bare anchor ───────
        collections.Add(VltCollectionBuilder.BuildBareCollection(
            "challenge_objectives_group", familyKey, frameworkKey));
    }
}
