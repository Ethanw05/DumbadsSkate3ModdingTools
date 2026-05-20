using DlcBuilder.Builders;
using DlcBuilder.Modules.OtsPsg;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// `progressionbanks/dlc_&lt;framework&gt;.vlt` — registers the DLC's progression
/// classes globally and lays the OTS achievement state-machine that fires on
/// the KilledIt completion path. Schemas verified byte-for-byte against retail
/// Danny Way `dw_progbanks_FULL.txt`.
///
/// Three logical bands, in author order:
///
///   1. Six class-default rows (parent=Hash_0) — registers the progression_*
///      class hashes globally. Without these, every later row mis-resolves at
///      load-time (NULL parent class hash → NULL deref).
///         progression_state/default          1 attr  Events (empty array)
///         progression_handler/default        1 attr  ByteCode (24B Lua stub)
///         progression_rewards/default        7 attrs (full reward schema)
///         progression_stategraph/default     1 attr  Name="default"
///         progression_action/default         2 attrs Name + IsStartingAction
///         progression_group/default          1 attr  Name="default"
///
///   2. Six per-DLC anchor rows (parent=default) — provides our DLC's
///      namespace. Three classes ship as 0-attr bridges (their default-row
///      schemas don't define a Name attribute), three ship 1-attr Name
///      anchors. Verified from DW dlc_dwgh row dump.
///         progression_state/&lt;framework&gt;          0 attrs
///         progression_handler/&lt;framework&gt;        0 attrs
///         progression_rewards/&lt;framework&gt;        0 attrs
///         progression_stategraph/&lt;framework&gt;     1 attr Name=&lt;framework&gt;
///         progression_action/&lt;framework&gt;         1 attr Name=&lt;framework&gt;
///         progression_group/&lt;framework&gt;          1 attr Name=&lt;framework&gt;
///
///   3. OTS reward-chain + per-OTS state-machine + DLC-wide bridge rows. Only
///      emitted when OTS challenges exist in the package. Walks the same shape
///      DW ships — 4 reward-chain rows + per-OTS (complete state + stategraph)
///      + DLC-wide group/action/stategraph + 3-row achievement chain
///      (parent action / achievement stategraph / achievement action).
///
/// The per-OTS state's Handlers slot points at a single shared
/// progression_handler row whose chunk-6 Lua bytecode dispatches
/// `ChangeState("complete")` on stateenter — without this the engine has no
/// Lua to fire and falls back to a name+8 deref path that crashes mid-string.
public static class ProgressionBanksVltBuilder
{
    public sealed record ProgressionArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static ProgressionArtifacts Build(
        string frameworkKey,
        IReadOnlyList<OtsChallengeSpec>? otsChallenges = null,
        IReadOnlyList<RaceChallengeSpec>? raceChallenges = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);

        string fileName = frameworkKey;            // e.g. "dlc_washingtondc"
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();

        // ── Band 1: six class-default rows ─────────────────────────────────
        uint defNamePtr = bin.AddString("default");

        // progression_state/default — 1 attr Events (empty array of
        // tChallengePresentationEvent[], element size 16B per schema).
        uint psEventsArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        var rowProgStateDefault = VltCollectionBuilder.BuildCollection(
            "progression_state", "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.PointerNoFixup("Events",
                    "Sk8::Challenge::tChallengePresentationEvent",
                    psEventsArr, 0x02),
            },
            explicitTypes: new[] { "Sk8::Challenge::tChallengePresentationEvent" },
            numTypesDup: 2);

        // progression_handler/default — 1 attr ByteCode = DW's literal 24B
        // Lua-5.1 stub header. MUST match DW byte-for-byte: when both DLCs are
        // installed the engine sees two `default` rows with the same hash; a
        // different blob breaks DW's load (reproduced 2026-05-06).
        // First 8B = Sk8 wrapper header (00 00 00 FA + 4 zeros). Next 12B =
        // Lua chunk header `1B 4C 75 61 51 00 01 04 04 04 04 00`. Last 4B =
        // LE u32 = 2 (= source-string length placeholder).
        byte[] dwProgHandlerByteCode =
        {
            0x00, 0x00, 0x00, 0xFA, 0x00, 0x00, 0x00, 0x00,
            0x1B, 0x4C, 0x75, 0x61, 0x51, 0x00, 0x01, 0x04,
            0x04, 0x04, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00,
        };
        uint phByteCodeOff = bin.AddBlob(dwProgHandlerByteCode);
        var rowProgHandlerDefault = VltCollectionBuilder.BuildCollection(
            "progression_handler", "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.PointerNoFixup("ByteCode", "Attrib::Blob", phByteCodeOff, 0x00),
            },
            explicitTypes: new[] { "Attrib::Blob" },
            numTypesDup: 2);

        // progression_rewards/default — 7-attr full reward schema. Engine
        // looks up these slots by hash on the "Owned It" reward-display path
        // (PPU 0x6376F0). DW canonical defaults baked in.
        uint prAssetNamePtr = bin.AddString("none");
        uint prDescHalidPtr = bin.AddString("#Description HAL ID");
        uint prHalidPtr     = bin.AddString("#MISSING HALID");
        uint prIconPtr      = bin.AddString("missingtexture");
        var rowProgRewardsDefault = VltCollectionBuilder.BuildCollection(
            "progression_rewards", "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.Inline("AssetCategory",    "Sk8::BE::eUNLOCK_CATEGORY",  0x0Cu),
                VltAttribute.Inline("AssetName",        "EA::Reflection::Text",       prAssetNamePtr),
                VltAttribute.Inline("DescriptionHALID", "EA::Reflection::Text",       prDescHalidPtr),
                VltAttribute.Inline("HALID",            "EA::Reflection::Text",       prHalidPtr),
                VltAttribute.Inline("Icon",             "EA::Reflection::Text",       prIconPtr),
                VltAttribute.Inline("SpeechCharacter",  "Sk8::Audio::eSk8Characters", 0x68u),
                VltAttribute.Inline("SpeechID",         "EA::Reflection::Int32",      0u),
            },
            explicitTypes: new[]
            {
                "Sk8::BE::eUNLOCK_CATEGORY",
                "EA::Reflection::Text",
                "Sk8::Audio::eSk8Characters",
                "EA::Reflection::Int32",
            });

        // progression_stategraph/default — 1 attr Name="default".
        var rowProgStategraphDefault = VltCollectionBuilder.BuildCollection(
            "progression_stategraph", "default", "Hash_0", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", defNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2);

        // progression_action/default — 2 attrs Name + IsStartingAction.
        var rowProgActionDefault = VltCollectionBuilder.BuildCollection(
            "progression_action", "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.Inline("Name",             "EA::Reflection::Text", defNamePtr),
                VltAttribute.Inline("IsStartingAction", "EA::Reflection::Bool", 0u),
            },
            explicitTypes: new[] { "EA::Reflection::Text", "EA::Reflection::Bool" });

        // progression_group/default — 1 attr Name="default".
        var rowProgGroupDefault = VltCollectionBuilder.BuildCollection(
            "progression_group", "default", "Hash_0", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", defNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2);

        // ── Band 2: six per-DLC anchor rows (parent=default) ───────────────
        // state/handler/rewards: 0-attr bridge rows. stategraph/action/group:
        // 1-attr Name=<framework>. Engine's parent-chain resolver for the
        // achievement-state-graph traversal needs the Name when walking
        // class hashes (= Hash("dlc_<framework>")).
        var rowProgStateAnchor   = VltCollectionBuilder.BuildBareCollection("progression_state",   frameworkKey, "default");
        var rowProgHandlerAnchor = VltCollectionBuilder.BuildBareCollection("progression_handler", frameworkKey, "default");
        var rowProgRewardsAnchor = VltCollectionBuilder.BuildBareCollection("progression_rewards", frameworkKey, "default");

        uint anchorNamePtr = bin.AddString(frameworkKey);
        var rowProgStategraphAnchor = VltCollectionBuilder.BuildCollection(
            "progression_stategraph", frameworkKey, "default", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", anchorNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2);
        var rowProgActionAnchor = VltCollectionBuilder.BuildCollection(
            "progression_action", frameworkKey, "default", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", anchorNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2);
        var rowProgGroupAnchor = VltCollectionBuilder.BuildCollection(
            "progression_group", frameworkKey, "default", 0u,
            new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", anchorNamePtr) },
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2);

        // ── Band 3: OTS reward chain ───────────────────────────────────────
        // 4 reward rows shared across all per-pack OTS challenges. DW canonical
        // shape: <framework>_challenges anchors the unlock category, the
        // _ots_unlock row carries the actual HALID/Icon/PlayableOnline reward
        // attrs; same pattern for milestones/finished_milestone.
        // halidStem strips a leading "dlc_" so dlc_custom_maps → CUSTOM_MAPS
        // (not DLC_DLC_*) when forming HALID strings.
        string halidStem = frameworkKey.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)
            ? frameworkKey[4..]
            : frameworkKey;
        string halidUpper = halidStem.ToUpperInvariant();

        string otsChallengesKey        = $"{frameworkKey}_challenges";
        string otsUnlockKey            = $"{frameworkKey}_ots_unlock";
        string otsMilestonesKey        = $"{frameworkKey}_milestones";
        string otsFinishedMilestoneKey = $"{frameworkKey}_finished_milestone";

        var rowOtsChallenges = VltCollectionBuilder.BuildBareCollection(
            "progression_rewards", otsChallengesKey, frameworkKey);

        uint otsUnlockHalidPtr = bin.AddString($"ID_DLC_{halidUpper}_OTS_UNLOCK");
        uint otsUnlockIconPtr  = bin.AddString(@"fe\source\images\unlocks\unlock_challenge_spot");
        var rowOtsUnlock = VltCollectionBuilder.BuildCollection(
            "progression_rewards", otsUnlockKey, otsChallengesKey, 0u,
            new[]
            {
                VltAttribute.Inline("HALID",          "EA::Reflection::Text", otsUnlockHalidPtr),
                VltAttribute.Inline("Icon",           "EA::Reflection::Text", otsUnlockIconPtr),
                VltAttribute.Inline("PlayableOnline", "EA::Reflection::Bool", 0x01000000u),
            },
            explicitTypes: new[] { "EA::Reflection::Text", "EA::Reflection::Bool" });

        var rowOtsMilestones = VltCollectionBuilder.BuildBareCollection(
            "progression_rewards", otsMilestonesKey, frameworkKey);

        uint fmAssetNamePtr = bin.AddString("milestone_placeholder");
        uint fmDescHalidPtr = bin.AddString($"ID_DLC_{halidUpper}_MILESTONE_1_DESC");
        uint fmHalidPtr     = bin.AddString($"ID_DLC_{halidUpper}_MILESTONE_1_TITLE");
        uint fmIconPtr      = bin.AddString(@"fe\source\images\unlocks\unlock_milestone1");
        var rowOtsFinishedMilestone = VltCollectionBuilder.BuildCollection(
            "progression_rewards", otsFinishedMilestoneKey, otsMilestonesKey, 0u,
            new[]
            {
                VltAttribute.Inline("AssetCategory",    "Sk8::BE::eUNLOCK_CATEGORY", 0x0Cu),
                VltAttribute.Inline("AssetName",        "EA::Reflection::Text",      fmAssetNamePtr),
                VltAttribute.Inline("DescriptionHALID", "EA::Reflection::Text",      fmDescHalidPtr),
                VltAttribute.Inline("HALID",            "EA::Reflection::Text",      fmHalidPtr),
                VltAttribute.Inline("Icon",             "EA::Reflection::Text",      fmIconPtr),
                VltAttribute.Inline("PlayableOnline",   "EA::Reflection::Bool",      0u),
            },
            explicitTypes: new[]
            {
                "Sk8::BE::eUNLOCK_CATEGORY",
                "EA::Reflection::Text",
                "EA::Reflection::Bool",
            },
            numTypesDup: 4);

        // ── Band 3: Per-OTS state machine + DLC-wide bridge + achievement ──
        var perOtsRows = new List<CollectionBlob>();
        if (otsChallenges != null && otsChallenges.Count > 0)
        {
            // Shared OTS state-enter handler row. Every per-OTS state's
            // Handlers slot points here. Handler bytecode is patched at
            // bytes [65..80] with the 16-uppercase-hex-char hash of the row's
            // own key — engine binds `h_<HASH>` global at chunk load.
            string handlerRowKey = $"{frameworkKey}_ots_handler";
            byte[] handlerByteCode = OtsCompleteHandlerBytecode.Build(handlerRowKey);
            uint handlerByteCodeOff = bin.AddBlob(handlerByteCode);
            uint handlerNameStateEnterPtr = bin.AddString("stateenter");
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_handler", handlerRowKey, frameworkKey, 0u,
                new[]
                {
                    VltAttribute.PointerNoFixup("ByteCode",  "Attrib::Blob",           handlerByteCodeOff, 0x00),
                    VltAttribute.Inline        ("MessageID", "EA::Reflection::UInt32", 0xF0D714B1u),
                    VltAttribute.Inline        ("Name",      "EA::Reflection::Text",   handlerNameStateEnterPtr),
                },
                explicitTypes: new[] { "Attrib::Blob", "EA::Reflection::UInt32", "EA::Reflection::Text" },
                numTypesDup: 4));

            // Single-element ClassRefSpec_progression_handler array
            // shared across every per-OTS state row.
            ulong handlerRowHash = Lookup8Hashing.Hash(handlerRowKey);
            uint sharedHandlersArr = bin.AddBlob(VltPayload.Build(w =>
            {
                w.WriteBE((ushort)1);   // count
                w.WriteBE((ushort)1);   // capacity
                w.WriteBE((ushort)16);  // typeSize
                w.WriteBE((ushort)0);   // align
                w.WriteBE(handlerRowHash); // 8B key hash (handler row)
                w.WriteBE(0UL);            // 8B cache slot (zero on disk)
            }));

            // Two parallel hash arrays for the bridge rows below.
            //   perOtsCompleteHashes: per-OTS `<key>_complete` row hashes —
            //     used in StateNodes RefSpec[] arrays (typed
            //     ClassRefSpec_progression_state).
            //   perOtsChallengeHashes: per-OTS `<key>` row hashes — used in
            //     progression_group.Challenges (typed
            //     ClassRefSpec_challenges). Mixing the two crashes — the
            //     engine resolves Challenges entries in the challenges class.
            var perOtsCompleteHashes  = new List<ulong>(otsChallenges.Count);
            var perOtsChallengeHashes = new List<ulong>(otsChallenges.Count);

            foreach (OtsChallengeSpec ots in otsChallenges)
            {
                string completeStateKey = $"{ots.ChallengeKey}_complete";
                string stategraphKey    = $"{ots.ChallengeKey}_stategraph";
                ulong  completeStateHash = Lookup8Hashing.Hash(completeStateKey);
                perOtsCompleteHashes.Add(completeStateHash);
                perOtsChallengeHashes.Add(Lookup8Hashing.Hash(ots.ChallengeKey));

                // progression_state/<key>_complete — 2 attrs Handlers + Name.
                // Name="complete" (NOT the full row key). Long Names crash
                // sub_609530 when the achievement dispatch reads u32 BE at
                // name_ptr+8 mid-string and derefs as a pointer — DW ships
                // generic short names ("initial"/"complete"/"achievements")
                // for exactly this reason.
                uint completeNamePtr = bin.AddString("complete");
                perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                    "progression_state", completeStateKey, frameworkKey, 0u,
                    new[]
                    {
                        VltAttribute.PointerNoFixup("Handlers",
                            "Attrib::Gen::ClassRefSpec_progression_handler",
                            sharedHandlersArr, 0x0A),
                        VltAttribute.Inline("Name", "EA::Reflection::Text", completeNamePtr),
                    },
                    explicitTypes: new[]
                    {
                        "Attrib::Gen::ClassRefSpec_progression_handler",
                        "EA::Reflection::Text",
                    }));

                // progression_stategraph/<key>_stategraph — 2 attrs
                // Name + StateNodes RefSpec[1]→<key>_complete.
                uint stategraphNamePtr = bin.AddString(stategraphKey);
                uint stateNodesArr = bin.AddBlob(
                    VltBinHelpers.BuildSingleProgressionStateRefArray(completeStateKey));
                perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                    "progression_stategraph", stategraphKey, frameworkKey, 0u,
                    new[]
                    {
                        VltAttribute.Inline("Name", "EA::Reflection::Text", stategraphNamePtr),
                        VltAttribute.PointerNoFixup("StateNodes",
                            "Attrib::Gen::ClassRefSpec_progression_state",
                            stateNodesArr, 0x0A),
                    },
                    explicitTypes: new[]
                    {
                        "EA::Reflection::Text",
                        "Attrib::Gen::ClassRefSpec_progression_state",
                    }));
            }

            // ── DLC-wide OTS bridge rows ────────────────────────────────────
            string otsChallengesGroupKey = $"dlc_{halidStem}_ots_challenges";
            string otsCompleteActionKey  = $"dlc_{halidStem}_ots_complete";
            string otsChallengesSGKey    = $"{halidStem}_ots_challenges_stategraph";

            // progression_group/<otsChallengesGroupKey> — 2 attrs Name +
            // Challenges (typed ClassRefSpec_challenges). DW: refs go to the
            // CHALLENGE row hashes, NOT the per-OTS state-complete hashes.
            uint groupNamePtr = bin.AddString(otsChallengesGroupKey);
            uint groupChallengesArr = bin.AddBlob(
                VltBinHelpers.BuildProgressionStateRefArray(perOtsChallengeHashes));
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_group", otsChallengesGroupKey, frameworkKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", groupNamePtr),
                    VltAttribute.PointerNoFixup("Challenges",
                        "Attrib::Gen::ClassRefSpec_challenges",
                        groupChallengesArr, 0x0A),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Attrib::Gen::ClassRefSpec_challenges",
                }));

            // progression_action/<otsChallengesGroupKey> — 1 attr Name
            // (anchor row sharing key with the group it tracks).
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_action", otsChallengesGroupKey, frameworkKey, 0u,
                new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", groupNamePtr) },
                explicitTypes: new[] { "EA::Reflection::Text" },
                numTypesDup: 2));

            // progression_stategraph/<otsChallengesSGKey> — 2 attrs Name +
            // StateNodes (RefSpec[] over per-OTS complete states).
            uint otsChallengesSGNamePtr = bin.AddString(otsChallengesSGKey);
            uint otsChallengesSGStateNodesArr = bin.AddBlob(
                VltBinHelpers.BuildProgressionStateRefArray(perOtsCompleteHashes));
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_stategraph", otsChallengesSGKey, frameworkKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", otsChallengesSGNamePtr),
                    VltAttribute.PointerNoFixup("StateNodes",
                        "Attrib::Gen::ClassRefSpec_progression_state",
                        otsChallengesSGStateNodesArr, 0x0A),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Attrib::Gen::ClassRefSpec_progression_state",
                }));

            // progression_action/<otsCompleteActionKey> — 3 attrs Name +
            // StateGraph + Triggers. Triggers fires the ots_challenges group
            // hash on completion. Layout matches DW Hash_B66D8E456D9FBC4D.
            uint completeNamePtrDlc = bin.AddString(otsCompleteActionKey);
            uint completeStateGraphRef = bin.AddBlob(
                VltBinHelpers.BuildClassRefSpec(otsChallengesSGKey));
            uint completeTriggersArr = bin.AddBlob(
                VltBinHelpers.BuildSingleObservatoryTrigger(
                    triggerType: 1,
                    targetHash: Lookup8Hashing.Hash(otsChallengesGroupKey)));
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_action", otsCompleteActionKey, frameworkKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", completeNamePtrDlc),
                    VltAttribute.PointerNoFixup("StateGraph",
                        "Attrib::Gen::ClassRefSpec_progression_stategraph",
                        completeStateGraphRef, 0x08),
                    VltAttribute.PointerNoFixup("Triggers",
                        "Observatory::tObservatoryTrigger",
                        completeTriggersArr, 0x02),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Attrib::Gen::ClassRefSpec_progression_stategraph",
                    "Observatory::tObservatoryTrigger",
                },
                numTypesDup: 4));

            // ── Achievement chain (KilledIt completion path, 3 rows) ────────
            // Without these rows the achievement-state-graph dispatch fires on
            // KilledIt and finds nothing → NULL deref crash. Reverse-engineered
            // byte-for-byte from dw_progbanks_FULL.txt:
            //   Row A: progression_action/dlc_<framework>_achievements
            //          parent=<framework>, 1 attr {Name}
            //   Row B: progression_action/dlc_<framework>_ots_complete_achievement
            //          parent=Row A, 3 attrs {Name, StateGraph→C, Triggers→group}
            //   Row C: progression_stategraph/<framework>_ots_complete_achievement_stategraph
            //          parent=<framework>, 2 attrs {Name, StateNodes (per-OTS complete)}
            //
            // Row C is authored before Row B because B's StateGraph
            // ClassRefSpec needs C present at parent-chain resolution time.
            string achievementsParentKey      = $"dlc_{halidStem}_achievements";
            string otsCompleteAchievementKey  = $"dlc_{halidStem}_ots_complete_achievement";
            string otsCompleteAchievementSGKey = $"{halidStem}_ots_complete_achievement_stategraph";

            // Row A
            uint achievementsParentNamePtr = bin.AddString(achievementsParentKey);
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_action", achievementsParentKey, frameworkKey, 0u,
                new[] { VltAttribute.Inline("Name", "EA::Reflection::Text", achievementsParentNamePtr) },
                explicitTypes: new[] { "EA::Reflection::Text" },
                numTypesDup: 2));

            // Row C (before B — see comment above)
            uint achievementSGNamePtr = bin.AddString(otsCompleteAchievementSGKey);
            uint achievementSGStateNodesArr = bin.AddBlob(
                VltBinHelpers.BuildProgressionStateRefArray(perOtsCompleteHashes));
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_stategraph", otsCompleteAchievementSGKey, frameworkKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", achievementSGNamePtr),
                    VltAttribute.PointerNoFixup("StateNodes",
                        "Attrib::Gen::ClassRefSpec_progression_state",
                        achievementSGStateNodesArr, 0x0A),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Attrib::Gen::ClassRefSpec_progression_state",
                }));

            // Row B
            uint achievementActionNamePtr = bin.AddString(otsCompleteAchievementKey);
            uint achievementActionStateGraphRef = bin.AddBlob(
                VltBinHelpers.BuildClassRefSpec(otsCompleteAchievementSGKey));
            uint achievementActionTriggersArr = bin.AddBlob(
                VltBinHelpers.BuildSingleObservatoryTrigger(
                    triggerType: 1,
                    targetHash: Lookup8Hashing.Hash(otsChallengesGroupKey)));
            perOtsRows.Add(VltCollectionBuilder.BuildCollection(
                "progression_action", otsCompleteAchievementKey, achievementsParentKey, 0u,
                new[]
                {
                    VltAttribute.Inline("Name", "EA::Reflection::Text", achievementActionNamePtr),
                    VltAttribute.PointerNoFixup("StateGraph",
                        "Attrib::Gen::ClassRefSpec_progression_stategraph",
                        achievementActionStateGraphRef, 0x08),
                    VltAttribute.PointerNoFixup("Triggers",
                        "Observatory::tObservatoryTrigger",
                        achievementActionTriggersArr, 0x02),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Text",
                    "Attrib::Gen::ClassRefSpec_progression_stategraph",
                    "Observatory::tObservatoryTrigger",
                },
                numTypesDup: 4));
        }

        // ── Band 4: Per-race progression rows ──────────────────────────────
        // Per-race rows: one shared `<framework>_race_handler` row + per-race
        // (`<key>_complete` state, `<key>_stategraph`). Uses the same
        // class-default + DLC anchor rows from Bands 1 + 2 above (which are
        // always emitted regardless of challenge mix), so race progression
        // works whether or not OTS challenges are present.
        //
        // Returned hashes feed any DLC-wide achievement / group rows we add
        // later — not consumed yet.
        if (raceChallenges != null && raceChallenges.Count > 0)
        {
            _ = RaceProgressionRowsBuilder.AppendProgressionRows(
                frameworkKey, raceChallenges, bin, perOtsRows);
        }

        var collections = new List<CollectionBlob>
        {
            // Band 1 — class defaults
            rowProgStateDefault,
            rowProgHandlerDefault,
            rowProgRewardsDefault,
            rowProgStategraphDefault,
            rowProgActionDefault,
            rowProgGroupDefault,
            // Band 2 — per-DLC anchors
            rowProgStateAnchor,
            rowProgHandlerAnchor,
            rowProgRewardsAnchor,
            rowProgStategraphAnchor,
            rowProgActionAnchor,
            rowProgGroupAnchor,
            // Band 3 — OTS reward chain
            rowOtsChallenges,
            rowOtsUnlock,
            rowOtsMilestones,
            rowOtsFinishedMilestone,
        };
        // perOtsRows now holds OTS per-instance rows + race per-instance rows
        // appended above. Order: class defaults → anchors → OTS chain → OTS
        // per-instance → race per-instance (engine resolves by parent chain
        // not by row position; this matches Danny Way's DLC dump order).
        collections.AddRange(perOtsRows);

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, Array.Empty<(uint, uint)>());
        byte[] binBytes = bin.BuildBinFile();
        return new ProgressionArtifacts(fileName, vltBytes, binBytes);
    }
}
