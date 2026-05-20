using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// `dlc_<framework>_local_data_framework.vlt` — pack-wide framework VLT
/// declaring the parent slots that per-challenge files (freeskate stub +
/// future OTS instances) chain through. Verified against retail Danny Way
/// `dlc_dwgh_local_data_framework.vlt`.
///
/// Eight rows, all `challenge_local_data` class:
///   1. &lt;framework&gt;                                 — pack root, parent=default
///   2. &lt;framework&gt;_freeskate_locations              — parent=&lt;framework&gt;
///   3. &lt;framework&gt;_freeskate_activities             — 11 attrs (the activity defaults)
///   4. &lt;framework&gt;_freeskate_activities_gap_tag     — 1 attr override
///   5. &lt;framework&gt;_freeskate_activities_accumulation — 3 attrs
///   6. &lt;framework&gt;_freeskate_activities_simultrick   — 3 attrs
///   7. &lt;framework&gt;_freeskate_activities_tricklist    — 11 attrs
///   8. &lt;framework&gt;_freeskate_activities_survival     — 4 attrs
///
/// Every row gets a 48-byte zero-filled layout block. Earlier ports used 4
/// bytes which made the engine read 44 bytes of adjacent strings as
/// schema-mapped layout data → crash at runtime during inheritance walks.
public static class LocalDataFrameworkVltBuilder
{
    public sealed record FrameworkArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static FrameworkArtifacts Build(string frameworkKey, bool includeOtsRow = false, bool includeRaceRow = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);

        string fileName = frameworkKey + "_local_data_framework";
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        var binFixups = new List<(uint, uint)>();

        // 48-byte zero layout per row (matches DW's PtrN-patched layout shape).
        uint rootLayoutOff = bin.AddBlob(new byte[48]);
        uint fsLocLayoutOff = bin.AddBlob(new byte[48]);
        uint fsActLayoutOff = bin.AddBlob(new byte[48]);
        uint fsActGapTagLayoutOff = bin.AddBlob(new byte[48]);
        uint fsActAccuLayoutOff = bin.AddBlob(new byte[48]);
        uint fsActSimulLayoutOff = bin.AddBlob(new byte[48]);
        uint fsActTrickLayoutOff = bin.AddBlob(new byte[48]);
        uint fsActSurvLayoutOff = bin.AddBlob(new byte[48]);

        // Shared HAL string used by activities + tricklist
        // ModifiedCentralMessageHALDIDObjectiveComplete.
        uint objCompleteHalIdOff = bin.AddString("ID_CHALLENGE_CENTRAL_MESSAGE_OBJECTIVE_OBJCOMPLETE");

        // Root + freeskate_locations: a single Bool attribute. Forensics show
        // every retail framework row has at least one attribute — bare 0-attr
        // rows leave the engine's attribute-table pointer uninitialized
        // (stack garbage 0xd14f4c58 in past crashes). Hash_9DDC567540219ACC
        // is what every shipping DLC declares.
        string[] minimalTypes = { "Sk8::Audio::eSk8Characters", "EA::Reflection::Bool" };
        var oneBoolAttr = new[]
        {
            VltAttribute.InlineRawHash("Hash_9DDC567540219ACC", "EA::Reflection::Bool", 0u, 0x9DDC567540219ACCUL),
        };

        // <framework>_freeskate_activities — 11 attrs.
        string[] fsActTypes =
        {
            "Sk8::Audio::eSk8Characters",
            "EA::Reflection::Bool",
            "Sk8::Challenge::tSpeechInfo",
            "EA::Reflection::Int16",
            "Sk8::Challenge::tHighlightDefinition",
            "EA::Reflection::Text",
            "EA::Reflection::Float",
        };
        var fsActAttrs = new[]
        {
            VltAttribute.Inline("AllowSessionMarkersToBeSet",  "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.Inline("AllowSessionMarkersToBeUsed", "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.PointerNoFixup("AudioPlayerQuitChallenge", "Sk8::Challenge::tSpeechInfo", 0u, 0x00),
            VltAttribute.Inline("DisableHOM",                  "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.Inline("FreeskateGoalAdjustment",     "EA::Reflection::Int16", 0x00050000u),
            VltAttribute.PointerNoFixup("HighlightDefinition", "Sk8::Challenge::tHighlightDefinition", 0u, 0x00),
            VltAttribute.Inline("ModifiedCentralMessageHALDIDObjectiveComplete", "EA::Reflection::Text", objCompleteHalIdOff),
            VltAttribute.Inline("OnlineLocationImageNumber",   "EA::Reflection::Int16", 0u),
            VltAttribute.Inline("ScoringMultiplier",           "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.Inline("TimeLimit",                   "EA::Reflection::Float", 0x43340000u),
            VltAttribute.Inline("TimeToWaitBeforeReplay",      "EA::Reflection::Float", 0x3F800000u),
        };

        // _gap_tag — 1 attr.
        string[] gapTagTypes = { "Sk8::Audio::eSk8Characters", "EA::Reflection::Int16" };
        var gapTagAttrs = new[]
        {
            VltAttribute.Inline("FreeskateGoalAdjustment", "EA::Reflection::Int16", 0x000F0000u),
        };

        // _accumulation — 3 attrs.
        string[] accuTypes =
        {
            "Sk8::Audio::eSk8Characters",
            "Sk8::Challenge::eFreeskateAccumulationType",
            "EA::Reflection::Int16",
            "EA::Reflection::Int32",
        };
        var accuAttrs = new[]
        {
            VltAttribute.Inline("FreeskateAccumulationType", "Sk8::Challenge::eFreeskateAccumulationType", 0u),
            VltAttribute.Inline("FreeskateGoalAdjustment",   "EA::Reflection::Int16", 0x00320000u),
            VltAttribute.Inline("FreeskateRequiredPoints",   "EA::Reflection::Int32", 0u),
        };

        // _simultrick — 3 attrs.
        string[] simulTypes =
        {
            "Sk8::Audio::eSk8Characters",
            "EA::Reflection::Int16",
            "EA::Reflection::Float",
            "Sk8::Challenge::tHighlightDefinition",
        };
        var simulAttrs = new[]
        {
            VltAttribute.Inline("FreeskateGoalAdjustment", "EA::Reflection::Int16", 0x000F0000u),
            VltAttribute.Inline("FreeskateTolerance",      "EA::Reflection::Float", 0x40000000u),
            VltAttribute.PointerNoFixup("HighlightDefinition", "Sk8::Challenge::tHighlightDefinition", 0u, 0x00),
        };

        // _tricklist — 11 attrs.
        string[] trickTypes =
        {
            "Sk8::Audio::eSk8Characters",
            "EA::Reflection::Bool",
            "EA::Reflection::Int16",
            "Sk8::Challenge::tHighlightDefinition",
            "EA::Reflection::Text",
            "Sk8::Challenge::tObjectiveStatusField",
            "EA::Reflection::Float",
        };
        var trickAttrs = new[]
        {
            VltAttribute.Inline("AllowSessionMarkersToBeSet",  "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.Inline("AllowSessionMarkersToBeUsed", "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.Inline("DisableHOM",                  "EA::Reflection::Bool",  0x01000000u),
            VltAttribute.Inline("FreeskateGoalAdjustment",     "EA::Reflection::Int16", 0x000A0000u),
            VltAttribute.PointerNoFixup("HighlightDefinition", "Sk8::Challenge::tHighlightDefinition", 0u, 0x00),
            VltAttribute.Inline("ModifiedCentralMessageHALDIDObjectiveComplete", "EA::Reflection::Text", objCompleteHalIdOff),
            VltAttribute.Inline("ObjectiveNotificationShownOnHUD",     "Sk8::Challenge::tObjectiveStatusField", 0x08u),
            VltAttribute.Inline("ObjectiveShowActiveWithNotification", "EA::Reflection::Bool", 0x01000000u),
            VltAttribute.Inline("ObjectiveStatusShownOnHUD",           "Sk8::Challenge::tObjectiveStatusField", 0x06u),
            VltAttribute.Inline("OnlineLocationImageNumber",   "EA::Reflection::Int16", 0u),
            VltAttribute.Inline("TimeLimit",                   "EA::Reflection::Float", 0x43960000u),
        };

        // _survival — 4 attrs.
        string[] survTypes =
        {
            "Sk8::Audio::eSk8Characters",
            "EA::Reflection::Int16",
            "EA::Reflection::Int32",
            "EA::Reflection::Float",
        };
        var survAttrs = new[]
        {
            VltAttribute.Inline("FreeskateGoalAdjustment", "EA::Reflection::Int16", 0x000A0000u),
            VltAttribute.Inline("FreeskateRequiredPoints", "EA::Reflection::Int32", 0u),
            VltAttribute.Inline("FreeskateRequiredTime",   "EA::Reflection::Int32", 0u),
            VltAttribute.Inline("FreeskateTolerance",      "EA::Reflection::Float", 0x40000000u),
        };

        var collections = new List<CollectionBlob>
        {
            // 1. Pack root
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey, "default",
                rootLayoutOff, oneBoolAttr,
                explicitTypes: minimalTypes),
            // 2. <framework>_freeskate_locations
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_locations", frameworkKey,
                fsLocLayoutOff, oneBoolAttr,
                explicitTypes: minimalTypes),
            // 3. <framework>_freeskate_activities
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_activities", frameworkKey,
                fsActLayoutOff, fsActAttrs,
                explicitTypes: fsActTypes),
            // 4..8. five subtype rows
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_activities_gap_tag",
                frameworkKey + "_freeskate_activities",
                fsActGapTagLayoutOff, gapTagAttrs,
                explicitTypes: gapTagTypes),
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_activities_accumulation",
                frameworkKey + "_freeskate_activities",
                fsActAccuLayoutOff, accuAttrs,
                explicitTypes: accuTypes),
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_activities_simultrick",
                frameworkKey + "_freeskate_activities",
                fsActSimulLayoutOff, simulAttrs,
                explicitTypes: simulTypes),
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_activities_tricklist",
                frameworkKey + "_freeskate_activities",
                fsActTrickLayoutOff, trickAttrs,
                explicitTypes: trickTypes),
            VltCollectionBuilder.BuildCollection(
                "challenge_local_data", frameworkKey + "_freeskate_activities_survival",
                frameworkKey + "_freeskate_activities",
                fsActSurvLayoutOff, survAttrs,
                explicitTypes: survTypes),
        };

        // Optional: 9th row, the OTS family template. Authored last so the
        // 8 freeskate rows above land at deterministic offsets even when
        // includeOtsRow toggles. Per-instance OTS rows in
        // db/challenge_local_data/<key>.vlt chain parent=<framework>_own_the_spots
        // through THIS row.
        if (includeOtsRow)
        {
            collections.Add(OtsFrameworkOwnTheSpotsRow.Build(frameworkKey, bin, binFixups));
        }

        // Optional: race family template. Mirrors stock retail
        // dlc_dwgh_local_data_framework's `dlc_dwgh_races` row byte-for-byte.
        // Per-instance race rows in db/challenge_local_data/race_<key>.vlt
        // chain parent=<framework>_races through THIS row — without it, the
        // engine's parent-chain walker NULL-derefs the moment "Start Race" is
        // pressed (sub_737790 → Vault_FindCollectionByHash returns NULL →
        // lwz r9, 0x20(r31) AV reading 0x20).
        if (includeRaceRow)
        {
            collections.Add(RaceFrameworkRacesRow.Build(frameworkKey, bin, binFixups));
        }

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, binFixups);
        byte[] binBytes = bin.BuildBinFile();
        return new FrameworkArtifacts(fileName, vltBytes, binBytes);
    }
}
