using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest.Vlt.Templates;
using DlcBuilder.Modules.OtsPsg;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// `challengebanks/dlc_<pkg>.vlt` — registers the DLC's freeskate area as an
/// online-discoverable freeskate location. Without this VLT the area appears
/// in the offline DLC menu but the Online → Freeskate menu doesn't list it.
///
/// Row map (verified against retail Danny Way `db/challengebanks/dlc_dwgh.vlt`):
///
///   ── Class-default rows (parent=Hash_0) ──
///   challenges/default                 4 attrs  (Name, DLCMask, GlobalData, LocalData)
///   challenge_objective/default        16 attrs  (root for challenge_objective inheritance)
///
///   ── Per-DLC anchor rows ──
///   challenges/dlc_&lt;pkg&gt;             4 attrs  (Name, DLCMask, GlobalData, LocalData)
///   challenge_global_data/dlc_&lt;pkg&gt;   0 attrs  (parent slot for row B)
///   challenge_failure_objective/dlc_&lt;pkg&gt;   1 attr (Name)
///   challenge_objective/dlc_&lt;pkg&gt;             1 attr (Name)
///   challenge_objectives_group/dlc_&lt;pkg&gt;      0 attrs
///
///   ── Freeskate location chain ──
///   challenges/dlc_&lt;pkg&gt;_freeskate_locations               4 attrs
///   challenge_global_data/dlc_&lt;pkg&gt;_freeskate_locations    13 attrs (template row)
///
///   ── Freeskate activities chain ──
///   challenges/dlc_&lt;pkg&gt;_freeskate_activities              3 attrs
///   challenge_global_data/dlc_&lt;pkg&gt;_freeskate_activities   19 attrs (ChallengeType=0x2A)
///   challenge_failure_objective/dlc_&lt;pkg&gt;_freeskate_activities  1 attr
///   ... five subtype rows (gap_tag/accumulation/simultrick/tricklist/survival),
///       each emitting challenges + challenge_global_data + challenge_failure_objective.
///
///   ── Per-map rows ──
///   challenges/freeskate_dlc_&lt;short&gt;             3 attrs
///   challenge_global_data/freeskate_dlc_&lt;short&gt;  6 attrs (the LOCATION row)
///
/// Each tVaultedRefSpec gets a PtrN fixup to an empty path string so the
/// engine's `sub_A6D400` path-string dispatch falls through without
/// dereferencing zero (which used to trip a CIA 0xA6D4D0 access violation).
public static class ChallengeBankFreeskateVltBuilder
{
    public sealed record VltArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static VltArtifacts Build(
        string frameworkKey,
        IReadOnlyList<DlcManifest.DlcSpec> maps,
        string? firstMapMapCategoryKey = null,
        IReadOnlyList<(OtsChallengeSpec Ots, string MapCategoryKey)>? otsChallenges = null,
        IReadOnlyList<(RaceChallengeSpec Race, string MapCategoryKey)>? raceChallenges = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);
        ArgumentNullException.ThrowIfNull(maps);

        string fileName = frameworkKey;          // e.g. "dlc_washingtondc"
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        var binFixups = new List<(uint fixupOffset, uint ptrValue)>();
        uint emptyPathStr = bin.AddString("");

        // Adds a 32B tVaultedRefSpec blob and registers a PtrN fixup so the
        // engine's path-string dispatch finds zero terminator (no-op).
        uint AddVRef(byte[] blobBytes)
        {
            uint off = bin.AddBlob(blobBytes);
            binFixups.Add((off + 0x18u, emptyPathStr));
            return off;
        }
        uint AddChallengeLocalDataLink(string rowName) => AddVRef(FreeskateConstants.BuildLocalDataVRef(rowName));

        // Shared zero blobs for empty-pointer attrs (NF=0x08 RefSpec, NF=0x0A RefSpec[]).
        // Pointing data=0 makes the engine read the StrE chunk header bytes
        // and treat them as a cache-slot pointer → access violation.
        uint zeroRefSpec24 = bin.AddBlob(new byte[24]);
        uint zeroArray8 = bin.AddBlob(new byte[24]);  // bumped to 24 to cover largest consumer

        // Row B template strings.
        uint emptyStrPtr   = bin.AddString("");
        uint defaultImgPtr = bin.AddString("default");
        uint hudTitlePtr   = bin.AddString("ID_MISSION_TEMPLATE_ONLINE_FREESKATE_TITLE");
        uint rowBTitlePtr  = maps.Count > 0
            ? bin.AddString(maps[0].LocationHalName)
            : bin.AddString("ID_LOCATION_FREESKATE");
        uint hudPreloadsOff = bin.AddBlob(FreeskateConstants.OnlineHUDPreloadsBlob);
        uint hullOff        = bin.AddBlob(FreeskateConstants.RequiredChallengeHullEmptyBlob);

        string rowBKey = frameworkKey + "_freeskate_locations";

        // BIN POOL ORDERING — matches MinimalDlcBuilder/DlcBuilder.cs:1738-2089
        // exactly. Per-DLC rows (Row 0/1 → Row A/B → Row Aa/Bb → Row Fr) come
        // FIRST; class-default rows ("challenges/default" and
        // "challenge_objective/default") are emitted LATER, between Row Fr
        // and Row Or. Earlier shape pulled the class-defaults to the top of
        // the pool, which shifted every subsequent string offset by enough
        // to make every cross-reference lookup miss.

        // ── Row 0: challenges/dlc_<pkg> ────────────────────────────────
        uint row0NamePtr = bin.AddString(frameworkKey);
        // DLCMask = bit 11 (0x800), matching DLC_SLOT_INDEX in dlc_mapping/pack_<key>.
        uint dlcMaskOff = bin.AddBlob(VltPayload.Build(w => w.WriteBE(0x0000000000000800UL)));
        uint row0GlobalDataOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_global_data", frameworkKey));
        uint row0LocalDataOff = AddChallengeLocalDataLink(frameworkKey);

        var row0 = VltCollectionBuilder.BuildCollection(
            "challenges", frameworkKey, "default", 0u,
            new[]
            {
                VltAttribute.Inline("Name", "EA::Reflection::Text", row0NamePtr),
                VltAttribute.PointerNoFixupRawHash("DLCMask", "EA::Reflection::UInt64", dlcMaskOff, 0x00, 0x8C5669E5A6864831UL),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec", row0GlobalDataOff, 0x08),
                VltAttribute.PointerNoFixup("LocalData", "AttribSysUtils::tVaultedRefSpec", row0LocalDataOff, 0x08),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "EA::Reflection::UInt64",
                "Attrib::RefSpec",
                "AttribSysUtils::tVaultedRefSpec",
            });

        // ── Row 1: challenge_global_data/dlc_<pkg> — empty ─────────────
        var row1 = VltCollectionBuilder.BuildBareCollection("challenge_global_data", frameworkKey, "default");

        // ── Row A: challenges/<pkg>_freeskate_locations ────────────────
        uint rowANamePtr = bin.AddString(rowBKey);
        uint rowAGlobalDataOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_global_data", "freeskate_locations"));
        uint rowALocalDataOff = AddVRef(FreeskateConstants.FreeskateLocalDataVRef);
        uint rowAStateGraphOff = AddVRef(FreeskateConstants.FreeskateStateGraphVRef);

        var rowA = VltCollectionBuilder.BuildCollection(
            "challenges", rowBKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline("Name", "EA::Reflection::Text", rowANamePtr),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec", rowAGlobalDataOff, 0x08),
                VltAttribute.PointerNoFixup("LocalData", "AttribSysUtils::tVaultedRefSpec", rowALocalDataOff, 0x08),
                VltAttribute.PointerNoFixup("StateGraph", "AttribSysUtils::tVaultedRefSpec", rowAStateGraphOff, 0x08),
            });

        // ── Row B: challenge_global_data/<pkg>_freeskate_locations — 13-attr template ──
        var rowB = VltCollectionBuilder.BuildCollection(
            "challenge_global_data", rowBKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline("AvailableOnline", "EA::Reflection::Bool", 0x01000000u),
                VltAttribute.Inline("ChallengeType", "Sk8::Challenge::eChallengeTypes", 22u),
                VltAttribute.Inline("DebugOnly", "EA::Reflection::Bool", 0u),
                VltAttribute.Inline("Description", "EA::Reflection::Text", emptyStrPtr),
                VltAttribute.Inline("FELargeImage", "Sk8::FE::tFEScreenShot", defaultImgPtr),
                VltAttribute.Inline("Global", "Sk8::Challenge::eGlobalType", 0u),
                VltAttribute.Inline("GlobalActivateRadius", "EA::Reflection::Float", 0x40400000u),  // 3.0f
                VltAttribute.Inline("HudTitle", "EA::Reflection::Text", hudTitlePtr),
                VltAttribute.Inline("Location", "Sk8::Challenge::tLocationID", emptyStrPtr),
                VltAttribute.Inline("MapStartLocation", "Sk8::Challenge::tLocationID", emptyStrPtr),
                VltAttribute.PointerNoFixup("OnlineHUDPreloads", "Sk8::HUD::eHUDComponent", hudPreloadsOff, 0x02),
                VltAttribute.PointerNoFixup("RequiredChallengeHull", "Sk8::Challenge::tRequiredChallengeHull", hullOff, 0x02),
                VltAttribute.Inline("Title", "EA::Reflection::Text", rowBTitlePtr),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Bool",
                "Sk8::Challenge::eChallengeTypes",
                "EA::Reflection::Text",
                "Sk8::FE::tFEScreenShot",
                "Sk8::Challenge::eGlobalType",
                "EA::Reflection::Float",
                "Sk8::Challenge::tLocationID",
                "Sk8::HUD::eHUDComponent",
                "Sk8::Challenge::tRequiredChallengeHull",
            });

        // ── Activities chain ──
        string activitiesKey = frameworkKey + "_freeskate_activities";
        uint rowAaNamePtr = bin.AddString(activitiesKey);
        uint rowAaGlobalDataOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_global_data", activitiesKey));
        uint rowAaLocalDataOff = AddChallengeLocalDataLink(activitiesKey);

        string[] activitiesBindingTypes =
        {
            "EA::Reflection::Text",
            "Attrib::RefSpec",
            "AttribSysUtils::tVaultedRefSpec",
        };
        var rowAa = VltCollectionBuilder.BuildCollection(
            "challenges", activitiesKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline("Name", "EA::Reflection::Text", rowAaNamePtr),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec", rowAaGlobalDataOff, 0x08),
                VltAttribute.PointerNoFixup("LocalData", "AttribSysUtils::tVaultedRefSpec", rowAaLocalDataOff, 0x08),
            },
            explicitTypes: activitiesBindingTypes);

        var rowBb = VltCollectionBuilder.BuildCollection(
            "challenge_global_data", activitiesKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline("AvailableOnline", "EA::Reflection::Bool", 0x01000000u),
                VltAttribute.Inline("ChallengeInfoShowObjectives", "EA::Reflection::Bool", 0x01000000u),
                VltAttribute.Inline("ChallengeType", "Sk8::Challenge::eChallengeTypes", 0x2Au),
                VltAttribute.Inline("DebugOnly", "EA::Reflection::Bool", 0u),
                VltAttribute.Inline("Description", "EA::Reflection::Text", emptyStrPtr),
                VltAttribute.Inline("DmoTreatAsStatic", "EA::Reflection::Bool", 0x01000000u),
                VltAttribute.PointerNoFixup("EndLocation", "Sk8::Challenge::tWorldLocationID", zeroRefSpec24, 0x08),
                VltAttribute.Inline("EndTeleport", "EA::Reflection::Bool", 0u),
                VltAttribute.Inline("FreeskateActivityType", "Sk8::Challenge::eFreeskateType", 0x01u),
                VltAttribute.Inline("Global", "Sk8::Challenge::eGlobalType", 0u),
                VltAttribute.Inline("GlobalActivateRadius", "EA::Reflection::Float", 0x40400000u),
                VltAttribute.Inline("Location", "Sk8::Challenge::tLocationID", defaultImgPtr),
                VltAttribute.InlineRawHash("Hash_C95DCFB60ACF8393", "EA::Reflection::Text", emptyStrPtr, 0xC95DCFB60ACF8393UL),
                VltAttribute.Inline("MapStartLocation", "Sk8::Challenge::tLocationID", defaultImgPtr),
                VltAttribute.PointerNoFixup("OnlineHUDPreloads", "Sk8::HUD::eHUDComponent", hudPreloadsOff, 0x02),
                VltAttribute.PointerNoFixup("ParentChallenge", "Attrib::Gen::ClassRefSpec_challenges", zeroRefSpec24, 0x08),
                VltAttribute.PointerNoFixup("RequiredChallengeHull", "Sk8::Challenge::tRequiredChallengeHull", hullOff, 0x02),
                VltAttribute.InlineRawHash("Hash_4C807B9C7F6C6F47", "EA::Reflection::Bool", 0x01000000u, 0x4C807B9C7F6C6F47UL),
                VltAttribute.Inline("Title", "EA::Reflection::Text", emptyStrPtr),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Bool",
                "Sk8::Challenge::eChallengeTypes",
                "EA::Reflection::Text",
                "Sk8::Challenge::tWorldLocationID",
                "Sk8::Challenge::eFreeskateType",
                "Sk8::Challenge::eGlobalType",
                "EA::Reflection::Float",
                "Sk8::Challenge::tLocationID",
                "Sk8::HUD::eHUDComponent",
                "Attrib::Gen::ClassRefSpec_challenges",
                "Sk8::Challenge::tRequiredChallengeHull",
            });

        // ── Row Fr: challenge_failure_objective/<framework> (parent=default) ──
        // Anchor row added BEFORE class-defaults — matches retail BIN ordering.
        uint rowFrNamePtr = bin.AddString(frameworkKey);
        var rowFr = VltCollectionBuilder.BuildCollection(
            "challenge_failure_objective", frameworkKey, "default", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", rowFrNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" });

        // ── Class-default rows (parent=Hash_0) ──
        // Emitted AFTER all the per-DLC anchor rows so the BIN string offsets
        // stay in lockstep with retail — DW ships these as the inheritance
        // roots for `challenges` / `challenge_objective`. Without them in our
        // challengebanks vlt, our per-instance OTS challenge_objective rows
        // inherit through stock `default` and yield NULL ObjectiveDefinition
        // at runtime (crash @ PPU 0x797f54 with r5=Hash("ObjectiveDefinition")).
        uint defNamePtr = bin.AddString("default");
        uint chlDefDLCMask24 = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0UL);
            w.WriteBE(Lookup8Hashing.Hash("challenge_global_data"));
            w.WriteBE(Lookup8Hashing.Hash("default"));
        }));
        uint chlDefGlobalRef = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_global_data", "default"));
        uint chlDefLocalRef = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_local_data", "default"));
        binFixups.Add((chlDefLocalRef + 0x18u, emptyPathStr));

        var rowChallengesDefault = VltCollectionBuilder.BuildCollection(
            "challenges", "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.Inline("Name", "EA::Reflection::Text", defNamePtr),
                VltAttribute.PointerNoFixup("DLCMask", "EA::Reflection::UInt64", chlDefDLCMask24, 0x00),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec", chlDefGlobalRef, 0x08),
                VltAttribute.PointerNoFixup("LocalData", "AttribSysUtils::tVaultedRefSpec", chlDefLocalRef, 0x08),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "EA::Reflection::UInt64",
                "Attrib::RefSpec",
                "AttribSysUtils::tVaultedRefSpec",
            });

        // challenge_objective/default — 16-attr root.
        uint objDefHighlightDef16 = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x4040000000000000UL);
            w.WriteBE(0UL);
        }));
        uint objDefObjDef16 = bin.AddBlob(new byte[16]);
        uint objDefObjTrg16 = bin.AddBlob(new byte[16]);
        uint objDefPointsValid16 = bin.AddBlob(new byte[16]);
        uint objDefActEvents8 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        uint objDefCompEvents8 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        uint objDefFailEvents8 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        uint objDefFailObj8 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(20));
        uint objDefFailObjEvt8 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        uint objDefVisInd8 = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(80));

        var rowObjDefault = VltCollectionBuilder.BuildCollection(
            "challenge_objective", "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.Inline("Name", "EA::Reflection::Text", defNamePtr),
                VltAttribute.PointerNoFixup("ActivationEvents", "Sk8::Challenge::tChallengePresentationEvent", objDefActEvents8, 0x02),
                VltAttribute.Inline("ActivationMode", "Sk8::Challenge::eTriggerActivationMode", 0u),
                VltAttribute.PointerNoFixup("CompletionEvents", "Sk8::Challenge::tChallengePresentationEvent", objDefCompEvents8, 0x02),
                VltAttribute.Inline("EvaluateFailureBeforeCompletionObjectives", "EA::Reflection::Bool", 0u),
                VltAttribute.PointerNoFixup("FailureEvents", "Sk8::Challenge::tChallengePresentationEvent", objDefFailEvents8, 0x02),
                VltAttribute.PointerNoFixup("FailureObjectives", "Sk8::Challenge::tObjectiveDefinition", objDefFailObj8, 0x02),
                VltAttribute.PointerNoFixup("FailureObjectivesWithEvents", "Attrib::RefSpec", objDefFailObjEvt8, 0x0A),
                VltAttribute.PointerNoFixup("HighlightDefinition", "Sk8::Challenge::tHighlightDefinition", objDefHighlightDef16, 0x00),
                VltAttribute.Inline("MasterObjective", "EA::Reflection::Bool", 0u),
                VltAttribute.PointerNoFixup("ObjectiveDefinition", "Sk8::Challenge::tObjectiveDefinition", objDefObjDef16, 0x00),
                VltAttribute.PointerNoFixup("ObjectiveTriggers", "Sk8::Challenge::tObjectiveTriggers", objDefObjTrg16, 0x00),
                VltAttribute.PointerNoFixup("PointsValid", "LuaState::tCompiledLua", objDefPointsValid16, 0x00),
                VltAttribute.Inline("RequiresHighlight", "EA::Reflection::Bool", 0u),
                VltAttribute.Inline("VisualIndicatorMode", "Sk8::Challenge::eVisualIndicatorMode", 0u),
                VltAttribute.PointerNoFixup("VisualIndicators", "Sk8::Challenge::tVisualEditorData", objDefVisInd8, 0x0A),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Sk8::Challenge::tChallengePresentationEvent",
                "Sk8::Challenge::eTriggerActivationMode",
                "EA::Reflection::Bool",
                "Sk8::Challenge::tObjectiveDefinition",
                "Attrib::RefSpec",
                "Sk8::Challenge::tHighlightDefinition",
                "Sk8::Challenge::tObjectiveTriggers",
                "LuaState::tCompiledLua",
                "Sk8::Challenge::eVisualIndicatorMode",
                "Sk8::Challenge::tVisualEditorData",
            },
            numTypesDup: 12);

        // ── Row Or / Og — anchor rows for objective + objectives_group ──
        // Retail emits these AFTER the class-default rows, with a fresh
        // AddString(frameworkKey) for Or's Name (separate from rowFrNamePtr).
        uint rowOrNamePtr = bin.AddString(frameworkKey);
        var rowOr = VltCollectionBuilder.BuildCollection(
            "challenge_objective", frameworkKey, "default", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", rowOrNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" });

        var rowOg = VltCollectionBuilder.BuildBareCollection("challenge_objectives_group", frameworkKey, "default");

        // Row Fa: challenge_failure_objective/<activities> — anchor for the
        // activities subtype rows. Lives after Or/Og to match retail order.
        uint rowFaNamePtr = bin.AddString(activitiesKey);
        var rowFa = VltCollectionBuilder.BuildCollection(
            "challenge_failure_objective", activitiesKey, frameworkKey, 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", rowFaNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" });

        var collections = new List<CollectionBlob> { rowChallengesDefault, rowObjDefault, row0, row1, rowFr, rowOr, rowOg, rowA, rowB, rowAa, rowBb, rowFa };

        // ── Subtype rows ──
        uint fsSubtypeChallengeIconOff = bin.AddBlob(VltBinHelpers.BuildChallengeIconDefinitionPadded(0xF788504A8D922CEDUL, 0xD4383831503D5608UL));
        // Subtype rows pin their MapCategory at the FIRST map's resolved
        // category key — same shape as MinimalDlcBuilder/DlcBuilder.cs:2096-2099
        // calling ResolveMapCategoryKeyForMap on the first map. Empty fallback
        // is "online" only for the (impossible-but-defensive) zero-map case.
        string fsSubtypeMapCatKey = maps.Count > 0
            ? ResolveMapCategoryKeyForMap(maps[0], maps, firstMapMapCategoryKey)
            : "online";
        uint fsSubtypeMapCategoryOff = bin.AddBlob(VltBinHelpers.BuildMapCategoryWorldRef24(fsSubtypeMapCatKey));
        uint fsSubtypeEmptyObjectivesArr = bin.AddBlob(FreeskateConstants.EmptyObjectivesRefSpecArray);

        string[] subGlobalDataTypes =
        {
            "Sk8::Challenge::tChallengeIconDefinition",
            "Sk8::Challenge::eFreeskateType",
            "Attrib::Gen::ClassRefSpec_map_category",
            "Attrib::RefSpec",
        };

        var subtypes = new (string Name, uint FreeskateType)[]
        {
            ("gap_tag", 0u),
            ("accumulation", 0u),
            ("simultrick", 3u),
            ("tricklist", 0u),
            ("survival", 0u),
        };
        foreach (var sub in subtypes)
        {
            string subKey = activitiesKey + "_" + sub.Name;
            uint subNamePtr = bin.AddString(subKey);
            uint subGlobalRefOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_global_data", subKey));
            uint subLocalRefOff = AddChallengeLocalDataLink(subKey);

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenges", subKey, activitiesKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", subNamePtr),
                    VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec", subGlobalRefOff, 0x08),
                    VltAttribute.PointerNoFixup("LocalData", "AttribSysUtils::tVaultedRefSpec", subLocalRefOff, 0x08),
                },
                explicitTypes: activitiesBindingTypes));

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenge_global_data", subKey, activitiesKey, 0u,
                new[]
                {
                    VltAttribute.PointerNoFixup("ChallengeIcon", "Sk8::Challenge::tChallengeIconDefinition", fsSubtypeChallengeIconOff, 0x08),
                    VltAttribute.Inline("FreeskateActivityType", "Sk8::Challenge::eFreeskateType", sub.FreeskateType),
                    VltAttribute.PointerNoFixup("MapCategory", "Attrib::Gen::ClassRefSpec_map_category", fsSubtypeMapCategoryOff, 0x08),
                    VltAttribute.PointerNoFixup("Objectives", "Attrib::RefSpec", fsSubtypeEmptyObjectivesArr, 0x0A),
                },
                explicitTypes: subGlobalDataTypes));

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenge_failure_objective", subKey, activitiesKey, 0u,
                new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", subNamePtr) },
                explicitTypes: new[] { "EA::Reflection::Text" }));
        }

        // ── Per-map rows C / D ──
        foreach (var map in maps)
        {
            string perAreaKey = "freeskate_dlc_" + map.Slug;
            string locatorName = "freeskate_" + map.Slug.ToLowerInvariant() + "_locator";

            uint rowCNamePtr = bin.AddString(perAreaKey);
            uint rowCGlobalDataOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24("challenge_global_data", perAreaKey));
            uint rowCLocalDataOff = AddChallengeLocalDataLink(perAreaKey);

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenges", perAreaKey, rowBKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", rowCNamePtr),
                    VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec", rowCGlobalDataOff, 0x08),
                    VltAttribute.PointerNoFixup("LocalData", "AttribSysUtils::tVaultedRefSpec", rowCLocalDataOff, 0x08),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Attrib::RefSpec",
                    "AttribSysUtils::tVaultedRefSpec",
                }));

            uint rowDDescPtr = bin.AddString(map.LocationDescHalName);
            uint rowDLocatorPtr = bin.AddString(locatorName);
            uint rowDTitlePtr = bin.AddString(map.LocationHalName);
            // Row D's MapCategory must hash to the actual map_category row
            // we emit — that's the package-level `<slug>dlc` (or
            // `<slug>dlc_<section>` per-section), NOT each DIST's
            // `MapCategoryKey`. Verified vs MinimalDlcBuilder/DlcBuilder.cs:2226.
            string rowDCatKey = ResolveMapCategoryKeyForMap(map, maps, firstMapMapCategoryKey);
            uint rowDMapCatRef16 = bin.AddBlob(VltBinHelpers.BuildClassRef16(rowDCatKey));
            uint rowDWorldRef16 = bin.AddBlob(VltBinHelpers.BuildClassRef16(map.DistKey));

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenge_global_data", perAreaKey, rowBKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Description", "EA::Reflection::Text", rowDDescPtr),
                    VltAttribute.Inline("Location", "Sk8::Challenge::tLocationID", rowDLocatorPtr),
                    VltAttribute.PointerNoFixup("MapCategory", "Attrib::Gen::ClassRefSpec_map_category", rowDMapCatRef16, 0x08),
                    VltAttribute.Inline("MapStartLocation", "Sk8::Challenge::tLocationID", rowDLocatorPtr),
                    VltAttribute.Inline("Title", "EA::Reflection::Text", rowDTitlePtr),
                    VltAttribute.PointerNoFixup("World", "Attrib::Gen::ClassRefSpec_world", rowDWorldRef16, 0x08),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Sk8::Challenge::tLocationID",
                    "Attrib::Gen::ClassRefSpec_map_category",
                    "Attrib::Gen::ClassRefSpec_world",
                }));
        }

        // ── OTS family + per-OTS rows ─────────────────────────────────────
        // Family rows (4) once per pack — the inheritance anchor every per-OTS
        // row chains through. Per-OTS rows (7) per challenge.
        if (otsChallenges != null && otsChallenges.Count > 0)
        {
            OtsFamilyRowsBuilder.AppendChallengeBanksFamilyRows(
                frameworkKey, bin, binFixups, emptyPathStr, collections);

            foreach (var (ots, mapCatKey) in otsChallenges)
            {
                OtsChallengeRowsBuilder.AppendChallengeRows(
                    ots, frameworkKey, mapCatKey, bin, binFixups, collections);
            }
        }

        // ── Race family + per-race instance rows ──────────────────────────
        // Family rows (2) once per pack: `challenges/<framework>_races` and
        // `challenge_global_data/<framework>_races`. These carry the
        // death-race UI strings and identity that every per-race instance row
        // inherits. Per-race rows (2 each: `challenges/<key>` +
        // `challenge_global_data/<key>`) follow, referencing the per-race
        // local_data VLT via tVaultedRefSpec.
        if (raceChallenges != null && raceChallenges.Count > 0)
        {
            RaceFamilyRowsBuilder.AppendChallengeBanksFamilyRows(
                frameworkKey, bin, binFixups, emptyPathStr, collections);

            foreach (var (race, mapCatKey) in raceChallenges)
            {
                RaceChallengeRowsBuilder.AppendChallengeRows(
                    race, frameworkKey, mapCatKey, bin, binFixups, collections);
            }
        }

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(vltFileName, binFileName, collections, binFixups);
        byte[] binBytes = bin.BuildBinFile();
        return new VltArtifacts(fileName, vltBytes, binBytes);
    }

    /// Mirrors MinimalDlcBuilder.ResolveMapCategoryKeyForMap: the freeskate
    /// row D's MapCategory ref hashes the PACKAGE category (or the per-section
    /// child for multi-section packs), NOT each DIST's per-DIST `<slug>dlc`.
    /// `firstMapMapCategoryKey` carries the package-level resolution from the
    /// orchestrator (e.g. `testpkgdlc`). When null we fall back to deriving
    /// it from the first map's slug — preserves single-DIST CLI behaviour.
    private static string ResolveMapCategoryKeyForMap(
        DlcManifest.DlcSpec map,
        IReadOnlyList<DlcManifest.DlcSpec> maps,
        string? packageCategoryKey)
    {
        string baseKey = packageCategoryKey ?? (maps.Count > 0 ? maps[0].MapCategoryKey : "online");
        bool useSubsections = maps.Any(m => !string.IsNullOrWhiteSpace(m.SectionLabel));
        if (!useSubsections) return baseKey;
        string sectionSlug = string.IsNullOrWhiteSpace(map.SectionLabel)
            ? "default"
            : DlcManifest.DlcSpec.ToSlug(map.SectionLabel);
        return $"{baseKey}_{sectionSlug}";
    }
}
