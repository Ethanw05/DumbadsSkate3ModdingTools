using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest.Vlt.Templates;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// `db/challenge_local_data/&lt;challengeKey&gt;.vlt` — per-instance Race
/// challenge VLT. Mirrors <see cref="OtsLocalDataVltBuilder"/> for the OTS
/// pipeline but emits the four-class row family that race challenges need:
///
///   challenge_local_data — 2 rows
///     (1) default / Hash_0           45-attr inline-default override (shared
///                                     with OTS / freeskate; same template)
///     (2) &lt;challengeKey&gt; / dlc_&lt;key&gt;_races
///                                     race instance row: HostCharacter +
///                                     RaceHeats[] + RaceType + StartLocation
///                                     + VisualIndicators[per-gate] +
///                                     OnlineEndCameraLocation
///
///   challenge_race_gates — 4 + N_gates rows
///     (1) default                     class-default with placeholder
///                                     GateVolume / IndicatorType / RibbonInstance
///                                     / SoundType / Time_Bonus
///     (2) dlc_&lt;key&gt;                  bare DLC namespace anchor
///     (3) dlc_&lt;key&gt;_races            bare DLC race-family anchor
///     (4) &lt;challengeKey&gt;             bare race-instance parent (the row
///                                     individual gates child under)
///     (5..) &lt;challengeKey&gt;_&lt;i&gt;     one per gate, with GateVolume +
///                                     Time_Bonus
///
///   challenge_race_legs — 4 + N_legs rows  (same shape as gates, leg-specific
///     attrs: Gates[] + SplitTimeTriggers[] on instance rows;
///     dlc_&lt;key&gt;_races overrides EndTimeBonus)
///
///   challenge_race_heats — 4 + N_heats rows  (same shape; the heavy lifting
///     happens on `dlc_&lt;key&gt;_races` which carries the engine-side defaults
///     mirrored from stock `races` family row: AIPaths blob, RaceSpeech ids,
///     NIS playback definitions, TimeLimit/Warning/SplitTimeDivisor, etc.)
///
/// Byte-level target: `StockGameData/DannyWayDLC/db/challenge_local_data/race_dwgh_01.vlt`
/// (8000 B), XML dump under `AttribDumpOut/dlc_race_dwgh_01/`. We emit one row
/// set per race (the offline-keyed VLT only — the engine surfaces an
/// `IsDeathRace=true` race in both menus off the family-row chain, so a
/// separate `_ol` companion would just duplicate the menu entry). The
/// `OnlineOutroNIS` / `OnlineOutroSoloNIS` / `OutroNIS` audio fields live on
/// the `&lt;framework&gt;_races` family row in
/// `&lt;framework&gt;_local_data_framework.vlt`
/// (<see cref="RaceFrameworkRacesRow"/>), inherited by the per-race instance.
public static class RaceLocalDataVltBuilder
{
    public sealed record RaceLocalDataArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    /// Build the per-race VLT + companion .bin. `frameworkKey` is the DLC's
    /// 4-char-clamped slug (e.g. "dlc_dwgh"). The race-family anchor key is
    /// `&lt;frameworkKey&gt;_races` (e.g. "dlc_dwgh_races").
    ///
    /// Spec is taken whole; the number of gates / legs / heats is derived
    /// from <see cref="RaceChallengeSpec.Heats"/>. Validation has already
    /// run upstream (PackageInputValidator.ValidateRaceChallenge), so we
    /// don't re-check structure here.
    public static RaceLocalDataArtifacts Build(RaceChallengeSpec spec, string frameworkKey)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);

        string raceFamilyKey = $"{frameworkKey}_races";   // e.g. "dlc_dwgh_races"
        string challengeKey = spec.ChallengeKey;          // e.g. "race_dwgh_01"
        string fileName = challengeKey;
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        var collections = new List<CollectionBlob>();
        var binFixups = new List<(uint, uint)>();

        // ── Row A1: challenge_local_data/default (parent=Hash_0) — 45-attr
        //    template shared with OTS / freeskate. MUST be appended first so
        //    the template's bin-offset assertions (tail blob at 0x60) hold.
        FreeskateChallengeLocalDataTemplate.AppendDefaultHash0Row(bin, collections);

        // After the default row's bin layout is locked we can append
        // per-instance strings + blobs freely.

        // Flatten the spec's heats / legs / gates into linear arrays so we
        // can emit one row per element in stable order. Stock `race_dwgh_01`
        // numbers gates flat across the race (race_dwgh_01_0 .. race_dwgh_01_2)
        // — we follow that convention.
        var heatList = spec.Heats.ToList();
        var legList = new List<(int HeatIndex, RaceLegSpec Leg)>();
        var gateList = new List<(int LegIndex, RaceGateSpec Gate)>();
        for (int hi = 0; hi < heatList.Count; hi++)
        {
            foreach (var leg in heatList[hi].Legs)
            {
                int legIdx = legList.Count;
                legList.Add((hi, leg));
                foreach (var gate in leg.Gates)
                    gateList.Add((legIdx, gate));
            }
        }
        int totalGates = gateList.Count;
        int totalLegs = legList.Count;
        int totalHeats = heatList.Count;

        // ── Per-instance bin strings used by the local_data row ───────────
        // Trigger-volume names: emit the CANONICAL DLC-format gate name
        // (e.g. `DIST_DownTown|race_user_01_racegate_01|0x<hexId>`) — this
        // MUST match the name the mission-folder PSG writer emits in
        // cSim_Global/<hash>.psg so engine's cTriggerVolumeManager can bind
        // by name + VolumeID. See <see cref="RaceVolumeNaming.CanonicalGateName"/>.
        var gateNameOffsets = new uint[totalGates];
        var gateCanonicalNames = new string[totalGates];
        for (int i = 0; i < totalGates; i++)
        {
            // Gate canonical names must match the name the mission-folder PSG
            // writer emits in cSim_Global/<hash>.psg so the engine's
            // cTriggerVolumeManager can bind by name + VolumeID.
            gateCanonicalNames[i] = RaceVolumeNaming.CanonicalGateName(
                spec.Map.WorldStreamName, challengeKey, i, totalGates, spec.Map.DistKey);
            gateNameOffsets[i] = bin.AddString(gateCanonicalNames[i]);
        }

        // Race gates get their archway visual from the per-gate
        // RibbonInstance (standardrace) on the challenge_race_gates row,
        // oriented by the trigger volume's m_TransformMatrix. No per-gate
        // VisualIndicator entries needed — stock DLC ships an empty VI array.

        // Split-time trigger names per leg (one bin string each). These DO
        // use the authored volume name from the spec — split-time triggers
        // reference existing world / mission volumes by name, they aren't
        // re-emitted in the per-race PSG.
        var legSplitNameOffsets = new uint[totalLegs][];
        for (int i = 0; i < totalLegs; i++)
        {
            var splits = legList[i].Leg.SplitTimeTriggers;
            legSplitNameOffsets[i] = new uint[splits.Count];
            for (int j = 0; j < splits.Count; j++)
                legSplitNameOffsets[i][j] = bin.AddString(splits[j].Name);
        }

        // ── Row A2: challenge_local_data/<challengeKey> instance row ──────
        // Layout (HostCharacter only, 4 bytes at row+0x28). Stock offline
        // `race_dwtn_01` uses 0x0000000F; we emit a single row set per race
        // (death races appear in both menus off the family-row chain, not via
        // a separate _ol companion VLT).
        uint localDataLayoutOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x0000000FU);   // HostCharacter (Sk8::Audio::eSk8Characters)
        }));

        // RaceHeats array — 1 element per heat. Each tRaceHeatDefinition is
        // 24 B: 8B classKey hash (challenge_race_heats) + 8B collectionKey
        // hash (heat row key) + 8B null cache slot.
        ulong heatsClassHash = Lookup8Hashing.Hash("challenge_race_heats");
        uint raceHeatsArrayOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)totalHeats);          // count
            w.WriteBE((ushort)totalHeats);          // capacity
            w.WriteBE((ushort)24);                  // typeSize (tRaceHeatDefinition)
            w.WriteBE((ushort)0);                   // align
            for (int hi = 0; hi < totalHeats; hi++)
            {
                w.WriteBE(heatsClassHash);
                w.WriteBE(Lookup8Hashing.Hash(spec.HeatKey(hi)));
                w.WriteBE(0UL);
            }
        }));

        // VisualIndicators — empty array. Race gates get their archway from
        // the per-gate RibbonInstance (standardrace) on challenge_race_gates,
        // oriented by the trigger volume's m_TransformMatrix. No per-gate
        // chevron VisualIndicator entries needed.
        uint visualIndicatorsArrayOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(80));

        // Start-location / end-camera strings — `<challengeKey>_startlocator`
        // and `<challengeKey>_endcamera` resolve to world-level locators that
        // RaceMissionFolderWriter registers in the per-mission PSG.
        uint startLocationStrOff = bin.AddString($"{challengeKey}_startlocator");
        uint onlineEndCamStrOff = bin.AddString($"{challengeKey}_endcamera");

        // AudioPlayerQuitChallenge — 8B tSpeechInfo. Stock retail
        // `race_dwtn_01.xml` ships `0000000F06A90000` (host=0x0F,
        // speech-id=0x06A9). Stock-shipped audio asset that exists for all
        // races.
        uint audioPlayerQuitOff = bin.AddBlob(Convert.FromHexString("0000000F06A90000"));

        // IntroPresentationEvents — 1-element tChallengePresentationEvent
        // array. Layout per IDA (struct is 12 B but vault stride is 16 B):
        //   +0x00 eventType        eChallengePresentationEventType u32  — stock = 5
        //   +0x04 eventTypeArg1    const char*                    u32  — PtrN-fixed
        //   +0x08 eventTypeArg2    const char*                    u32  — PtrN-fixed
        //   +0x0C pad              4 B zero
        //
        // CRITICAL: arg1 and arg2 are CHAR* fields — they MUST get PtrN
        // fixups. Earlier this builder wrote stock retail's literal bytes
        // (`00000005 000000D0 00000008 00000000`) without any fixups, so at
        // runtime arg1 stayed as raw integer 0xD0 and arg2 as raw 0x8. The
        // race-start state-graph parser then read arg2 as a string pointer
        // and dereferenced 0x8 → AV. Stock dwtn has explicit PtrN entries
        // (e.g. `fixupOff=0x0B38 ptr=0xD0` for arg1, `fixupOff=0x0B3C
        // ptr=0x8` for arg2) so the engine sees absolute pointers at
        // runtime, not raw bin offsets.
        //
        // We mirror stock's targets — arg1 → bin offset 0xD0 (whatever
        // string lives there in our bin), arg2 → bin offset 0x8. Both are
        // valid C strings in our pool (template HALID at 0x8 in particular),
        // so the engine reads them as opaque text without crashing.
        uint introPresentationEventsArrOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);       // count
            w.WriteBE((ushort)1);       // capacity
            w.WriteBE((ushort)16);      // typeSize (tChallengePresentationEvent vault stride)
            w.WriteBE((ushort)0);       // align
            w.WriteBE(0x00000005u);     // +0x00 eventType
            w.WriteBE(0x000000D0u);     // +0x04 eventTypeArg1 — PtrN-fixed below
            w.WriteBE(0x00000008u);     // +0x08 eventTypeArg2 — PtrN-fixed below
            w.WriteBE(0u);              // +0x0C pad
        }));
        // PtrN fixups for the two char* fields in element 0. Element starts
        // at `+8` past the array header; arg1 is at element+4, arg2 at
        // element+8.
        binFixups.Add((introPresentationEventsArrOff + 8u + 4u, 0xD0u));
        binFixups.Add((introPresentationEventsArrOff + 8u + 8u, 0x08u));

        var instanceAttrs = new List<CollectionAttributeSpec>(10);
        instanceAttrs.Add(VltAttribute.PointerNoFixup("AudioPlayerQuitChallenge", "Sk8::Challenge::tSpeechInfo", audioPlayerQuitOff, 0x00));
        // Challenge_Index = 0x01 → registers the race in the FE challenge rotation.
        instanceAttrs.Add(VltAttribute.Inline("Challenge_Index", "EA::Reflection::UInt8", 0x01000000u));
        // IntroPresentationEvents: non-typed array → NF=0x02. Element struct
        // tChallengePresentationEvent is 12 B (eventType + 2 char*) with 4 B
        // vault-stride pad — array stride = 16 (matches retail).
        instanceAttrs.Add(VltAttribute.PointerNoFixup("IntroPresentationEvents", "Sk8::Challenge::tChallengePresentationEvent", introPresentationEventsArrOff, 0x02));
        instanceAttrs.Add(VltAttribute.Inline("OnlineEndCameraLocation", "Sk8::Challenge::tLocationID", onlineEndCamStrOff));
        // RaceGateSkipable — `0x01000000` true on stock retail instance rows.
        instanceAttrs.Add(VltAttribute.Inline("RaceGateSkipable", "EA::Reflection::Bool", spec.RaceGateSkipable ? 0x01000000u : 0u));
        // RaceHeats: tRaceHeatDefinition is a single-member Attrib::RefSpec
        // wrapper (24 B per element) → typed-refspec ARRAY → NF=0x0A.
        // NF=0x02 (non-typed array) makes the engine misread the classKey slot
        // on each element and is the immediate cause of the Lua-VM 0x8-pointer
        // crash chased in this branch.
        instanceAttrs.Add(VltAttribute.PointerNoFixup("RaceHeats", "Sk8::Challenge::tRaceHeatDefinition", raceHeatsArrayOff, 0x0A));
        // RaceType: stock retail ships `0x00000000`. (`1` means a different race
        // mode entirely, not "death race" — verified against retail dumps.)
        instanceAttrs.Add(VltAttribute.Inline("RaceType", "Sk8::Challenge::eRaceType", 0u));
        instanceAttrs.Add(VltAttribute.Inline("StartLocation", "Sk8::Challenge::tLocationID", startLocationStrOff));
        instanceAttrs.Add(VltAttribute.PointerNoFixup("VisualIndicators", "Sk8::Challenge::tVisualEditorData", visualIndicatorsArrayOff, 0x0A));

        // Type table — every TypeName we use in this row's attrs.
        var typeSet = new[]
        {
            "Sk8::Audio::eSk8Characters",
            "Sk8::Challenge::tLocationID",
            "EA::Reflection::Bool",
            "EA::Reflection::UInt8",
            "Sk8::Challenge::tRaceHeatDefinition",
            "Sk8::Challenge::eRaceType",
            "Sk8::Challenge::tVisualEditorData",
            "Sk8::Challenge::tSpeechInfo",
            "Sk8::Challenge::tChallengePresentationEvent",
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_local_data", challengeKey, raceFamilyKey, localDataLayoutOff,
            instanceAttrs.ToArray(),
            explicitTypes: typeSet));

        // ─────────────────────────────────────────────────────────────────
        // challenge_race_gates rows
        // ─────────────────────────────────────────────────────────────────
        AppendRaceGatesRows(spec, frameworkKey, raceFamilyKey, challengeKey,
            gateList, gateNameOffsets, bin, collections, binFixups);

        // ─────────────────────────────────────────────────────────────────
        // challenge_race_legs rows
        // ─────────────────────────────────────────────────────────────────
        AppendRaceLegsRows(spec, frameworkKey, raceFamilyKey, challengeKey,
            legList, gateList, legSplitNameOffsets, bin, collections, binFixups);

        // ─────────────────────────────────────────────────────────────────
        // challenge_race_heats rows
        // ─────────────────────────────────────────────────────────────────
        AppendRaceHeatsRows(spec, frameworkKey, raceFamilyKey, challengeKey,
            heatList, legList, bin, collections, binFixups);

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, binFixups);
        byte[] binBytes = bin.BuildBinFile();
        return new RaceLocalDataArtifacts(fileName, vltBytes, binBytes);
    }

    // ─────────────────────────────────────────────────────────────────────
    // challenge_race_gates: 4 inheritance rows + N gate instances
    //
    //   default            class default: 5 attrs incl. placeholder GateVolume,
    //                       IndicatorType=2, RibbonInstance, SoundType=0,
    //                       Time_Bonus=0 (retail-verified from race_dwgh_01)
    //   dlc_<key>          bare anchor under default
    //   dlc_<key>_races    bare anchor under dlc_<key>
    //   <challengeKey>     bare anchor under dlc_<key>_races (gate-instance parent)
    //   <challengeKey>_<i> per-gate row with GateVolume + Time_Bonus
    // ─────────────────────────────────────────────────────────────────────
    private static void AppendRaceGatesRows(
        RaceChallengeSpec spec, string frameworkKey, string raceFamilyKey, string challengeKey,
        List<(int LegIndex, RaceGateSpec Gate)> gateList,
        uint[] gateNameOffsets,
        BinPoolBuilder bin,
        List<CollectionBlob> collections,
        List<(uint, uint)> binFixups)
    {
        const string Cls = "challenge_race_gates";

        // Class default — race_dwgh_01 ships this with 5 placeholder defaults.
        // The class default row is keyed `default` with parent="Hash_0".
        // GateVolume is a zero-extent placeholder tTriggerVolumeInstanceID;
        // IndicatorType=2 (eSk8ChallengeIndicators::Chevron-style); SoundType=0.
        // RibbonInstance: 48-byte tSplineBankObject — stock placeholder spline.
        uint gateDefaultGateVolume = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(0UL));
        // RibbonInstance default (placeholder spline bank) — 48 B.
        // Retail race_dwgh_01.xml challenge_race_gates/default RibbonInstance:
        //   3297556BECB605E2 624E29026A97C02E 0000000000000000
        //   F0DB8767D611AF94 D7EDBD362D7D2152 0000000000000000
        uint gateDefaultRibbon = bin.AddBlob(Convert.FromHexString(
            "3297556BECB605E2624E29026A97C02E0000000000000000" +
            "F0DB8767D611AF94D7EDBD362D7D21520000000000000000"));

        var gateDefaultTypes = new[]
        {
            "Sk8::Challenge::tTriggerVolumeInstanceID",
            "Sk8::Challenge::eSk8ChallengeIndicators",
            "Sk8::Challenge::tSplineBankObject",
            "Sk8::Challenge::eRaceGateAudio",
            "EA::Reflection::Int32",
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            Cls, "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.PointerNoFixup("GateVolume",     "Sk8::Challenge::tTriggerVolumeInstanceID", gateDefaultGateVolume, 0x00),
                VltAttribute.Inline        ("IndicatorType",  "Sk8::Challenge::eSk8ChallengeIndicators",   2u),
                // tSplineBankObject is two back-to-back typed RefSpec24s →
                // NF=0x08 (typed-refspec single). NF=0x00 makes the engine
                // skip the class-key slot during inheritance walks.
                VltAttribute.PointerNoFixup("RibbonInstance", "Sk8::Challenge::tSplineBankObject",         gateDefaultRibbon,     0x08),
                VltAttribute.Inline        ("SoundType",      "Sk8::Challenge::eRaceGateAudio",            0u),
                VltAttribute.Inline        ("Time_Bonus",     "EA::Reflection::Int32",                     0u),
            },
            explicitTypes: gateDefaultTypes));

        // Bare anchors — no attrs. Engine just registers the rows so the
        // inheritance chain resolves: instance → race-instance-parent →
        // dlc_<key>_races → dlc_<key> → default.
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, frameworkKey,    "default"));
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, raceFamilyKey,   frameworkKey));
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, challengeKey,    raceFamilyKey));

        // Per-gate instance rows — GateVolume tTriggerVolumeInstanceID +
        // Time_Bonus inline int32.
        for (int gi = 0; gi < gateList.Count; gi++)
        {
            var (_, gate) = gateList[gi];
            // Per-gate VolumeID — must match the GuidLocal of the matching
            // tTriggerInstance the mission-folder PSG writer emits in
            // cSim_Global/<hash>.psg. RaceVolumeNaming centralises the formula
            // (including the `_FINISH` last-gate convention) so both writers
            // stay in lockstep.
            ulong gateVolumeId = RaceVolumeNaming.GateVolumeId(
                challengeKey, gi, gateList.Count, spec.Map.DistKey);
            uint gateVolumeStub = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(gateVolumeId));
            // PtrN fixup: VolumeName ptr at +0 inside the 16B struct → gate name string in bin.
            binFixups.Add((gateVolumeStub, gateNameOffsets[gi]));

            collections.Add(VltCollectionBuilder.BuildCollection(
                Cls, spec.GateKey(gi), challengeKey, 0u,
                new[]
                {
                    VltAttribute.PointerNoFixup("GateVolume", "Sk8::Challenge::tTriggerVolumeInstanceID", gateVolumeStub, 0x00),
                    VltAttribute.Inline        ("Time_Bonus", "EA::Reflection::Int32",                     (uint)gate.TimeBonusSeconds),
                }));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // challenge_race_legs: 4 inheritance rows + N leg instances
    //
    //   default              class default with empty SplitTimeTriggers[]
    //                         (retail race_dwgh_01: 1 attr)
    //   dlc_<key>            bare anchor under default
    //   dlc_<key>_races      EndTimeBonus=0 + empty Gates[] + empty
    //                         SplitTimeTriggers[] (retail mirrors stock `races`
    //                         in challenge_race_legs)
    //   <challengeKey>       bare instance parent
    //   <challengeKey>_<i>   per-leg row: Gates[] + SplitTimeTriggers[]
    // ─────────────────────────────────────────────────────────────────────
    private static void AppendRaceLegsRows(
        RaceChallengeSpec spec, string frameworkKey, string raceFamilyKey, string challengeKey,
        List<(int HeatIndex, RaceLegSpec Leg)> legList,
        List<(int LegIndex, RaceGateSpec Gate)> gateList,
        uint[][] legSplitNameOffsets,
        BinPoolBuilder bin,
        List<CollectionBlob> collections,
        List<(uint, uint)> binFixups)
    {
        const string Cls = "challenge_race_legs";

        // Class default — single empty SplitTimeTriggers[] array.
        uint legDefaultSplitArray = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        collections.Add(VltCollectionBuilder.BuildCollection(
            Cls, "default", "Hash_0", 0u,
            new[]
            {
                VltAttribute.PointerNoFixup("SplitTimeTriggers", "Sk8::Challenge::tTriggerVolumeInstanceID", legDefaultSplitArray, 0x02),
            },
            explicitTypes: new[] { "Sk8::Challenge::tTriggerVolumeInstanceID" }));

        // dlc_<key>: bare anchor.
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, frameworkKey, "default"));

        // dlc_<key>_races: race-family defaults — EndTimeBonus=0 + empty arrays.
        uint legFamilyEmptyGates  = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        uint legFamilyEmptySplits = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        collections.Add(VltCollectionBuilder.BuildCollection(
            Cls, raceFamilyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("EndTimeBonus",      "EA::Reflection::Int32",                     0u),
                // Gates: tRaceGateDefinition is a single-member RefSpec wrapper
                // → typed-refspec ARRAY → NF=0x0A.
                VltAttribute.PointerNoFixup("Gates",             "Sk8::Challenge::tRaceGateDefinition",       legFamilyEmptyGates,  0x0A),
                // SplitTimeTriggers: tTriggerVolumeInstanceID is name + ID
                // (NOT a typed RefSpec) → non-typed ARRAY → NF=0x02.
                VltAttribute.PointerNoFixup("SplitTimeTriggers", "Sk8::Challenge::tTriggerVolumeInstanceID",  legFamilyEmptySplits, 0x02),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Int32",
                "Sk8::Challenge::tRaceGateDefinition",
                "Sk8::Challenge::tTriggerVolumeInstanceID",
            }));

        // Bare instance parent.
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, challengeKey, raceFamilyKey));

        // Per-leg instance rows — Gates[] (tRaceGateDefinition[]) +
        // SplitTimeTriggers[] (tTriggerVolumeInstanceID[]).
        ulong gatesClassHash = Lookup8Hashing.Hash("challenge_race_gates");
        for (int li = 0; li < legList.Count; li++)
        {
            var (_, leg) = legList[li];

            // Flat list of gate-row keys that belong to this leg, in order.
            var legGateRowIndices = new List<int>();
            for (int gi = 0; gi < gateList.Count; gi++)
                if (gateList[gi].LegIndex == li) legGateRowIndices.Add(gi);

            // Build the Gates array: { class=challenge_race_gates,
            //                          key=Lookup8(challengeKey_<n>),
            //                          0 } × count.
            uint gatesArrayOff = bin.AddBlob(VltPayload.Build(w =>
            {
                w.WriteBE((ushort)legGateRowIndices.Count);
                w.WriteBE((ushort)legGateRowIndices.Count);
                w.WriteBE((ushort)24);
                w.WriteBE((ushort)0);
                foreach (int gi in legGateRowIndices)
                {
                    w.WriteBE(gatesClassHash);
                    w.WriteBE(Lookup8Hashing.Hash(spec.GateKey(gi)));
                    w.WriteBE(0UL);
                }
            }));

            // SplitTimeTriggers[] — one per authored split trigger on this leg.
            uint splitsArrayOff;
            var splits = leg.SplitTimeTriggers;
            if (splits.Count == 0)
            {
                splitsArrayOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
            }
            else
            {
                uint arrOff = bin.AddBlob(VltPayload.Build(w =>
                {
                    w.WriteBE((ushort)splits.Count);
                    w.WriteBE((ushort)splits.Count);
                    w.WriteBE((ushort)16);
                    w.WriteBE((ushort)0);
                    for (int j = 0; j < splits.Count; j++)
                    {
                        w.WriteBE(0u);    // VolumeName ptr (PtrN-fixed)
                        w.WriteBE(0u);    // padding
                        w.WriteBE(ResolveVolumeId(splits[j]));
                    }
                }));
                // PtrN fixups for each split-trigger VolumeName pointer.
                for (int j = 0; j < splits.Count; j++)
                    binFixups.Add((arrOff + 8u + (uint)j * 16u, legSplitNameOffsets[li][j]));
                splitsArrayOff = arrOff;
            }

            collections.Add(VltCollectionBuilder.BuildCollection(
                Cls, spec.LegKey(li), challengeKey, 0u,
                new[]
                {
                    // Gates: typed-refspec ARRAY (24 B RefSpec elements) → NF=0x0A.
                    VltAttribute.PointerNoFixup("Gates",             "Sk8::Challenge::tRaceGateDefinition",      gatesArrayOff,  0x0A),
                    // SplitTimeTriggers: non-typed ARRAY (16 B name+ID elements) → NF=0x02.
                    VltAttribute.PointerNoFixup("SplitTimeTriggers", "Sk8::Challenge::tTriggerVolumeInstanceID", splitsArrayOff, 0x02),
                }));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // challenge_race_heats: 4 inheritance rows + N heat instances
    //
    //   default              class default — bare (no attrs)
    //   dlc_<key>            bare anchor under default
    //   dlc_<key>_races      heavy default row mirroring stock `races`
    //                         family. Includes: AIPaths (808-byte
    //                         8-blob blob), RaceSpeech* speech-info IDs,
    //                         NIS playback defaults, TimeLimit/Warning,
    //                         SplitTimeDivisor, Title / Description text refs,
    //                         and a single placeholder Legs[1] referencing
    //                         the stock default-leg key hash.
    //   <challengeKey>       bare instance parent
    //   <challengeKey>_<i>   per-heat row: KilledItTime + Legs[] +
    //                         NISFlythroughDefinitionOnline +
    //                         NISOutroDefinition + StartLocation + TimeLimit
    // ─────────────────────────────────────────────────────────────────────
    private static void AppendRaceHeatsRows(
        RaceChallengeSpec spec, string frameworkKey, string raceFamilyKey, string challengeKey,
        List<RaceHeatSpec> heatList,
        List<(int HeatIndex, RaceLegSpec Leg)> legList,
        BinPoolBuilder bin,
        List<CollectionBlob> collections,
        List<(uint, uint)> binFixups)
    {
        const string Cls = "challenge_race_heats";

        // Class default — bare (stock race_dwgh_01 default.xml has no attrs).
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, "default", "Hash_0"));

        // dlc_<key>: bare.
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, frameworkKey, "default"));

        // dlc_<key>_races: heavy defaults. Stock race_dwgh_01 ships this
        // mirroring stock `races` family-row attribute values. The 808-byte
        // AIPaths blob is reproduced byte-for-byte from the retail dump.
        // Title / Description point at "#First Heat Title" / "#First Heat
        // Description" — universal stock strings used by every race family
        // row in the schema, regardless of DLC.
        uint heatFamilyDescription = bin.AddString("#First Heat Description");
        uint heatFamilyTitle       = bin.AddString("#First Heat Title");

        // AIPaths — 808 bytes. `Sk8::Challenge::tRoundPaths` is
        //   char*  PathSubset[200];    // 800 B  — 200 char* slots
        //   int    intFilterDisabled;  //   4 B
        //   int    intAllowDynamic;    //   4 B
        // Retail race_dwgh_01 writes 0x00000008 in every char* slot and
        // emits a PtrN DepRelative fixup for each so the runtime patches
        // the slot to dep.data + 0x8 — an empty placeholder path string
        // at the head of the bin pool. The final two u32s are real ints
        // and stay as literal 0x00000008 (their semantic value is "8").
        uint aiPathPlaceholder = bin.AddString(string.Empty);
        uint heatFamilyAiPaths = bin.AddBlob(VltPayload.Build(w =>
        {
            for (int i = 0; i < 202; i++) w.WriteBE(0x00000008U);
        }));
        for (uint i = 0; i < 200; i++)
        {
            binFixups.Add((heatFamilyAiPaths + i * 4u, aiPathPlaceholder));
        }

        // FollowTheLeaderWarningInfo — empty tFollowTheLeaderWarningInfo[].
        uint ftlEmpty = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        // MusicPace — empty tRaceMusicPaceDefinition[].
        uint musicPaceEmpty = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));
        // RaceTerminateVolumes — empty.
        uint raceTermVolEmpty = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        // RandomBranches — empty.
        uint randomBranchesEmpty = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(24));

        // NIS playback definitions — 64 B each. Retail stock `races` ships
        // these with the leading u32 at +0x00 pointing into the bin pool
        // (NIS-name string ref). Placeholder strings keep the engine's NIS
        // resolver from crashing on cold start; the actual NIS playback only
        // fires when the heat's instance row overrides the field.
        uint flythroughName       = bin.AddString("default_flythrough");
        uint flythroughOnlineName = bin.AddString("default_flythrough_online");
        uint outroName            = bin.AddString("default_outro");

        uint nisFlythrough       = bin.AddBlob(BuildNisPlaybackDef64(flythroughName));
        uint nisFlythroughOnline = bin.AddBlob(BuildNisPlaybackDef64(flythroughOnlineName));
        uint nisOutro            = bin.AddBlob(BuildNisPlaybackDef64(outroName));
        binFixups.Add((nisFlythrough,       flythroughName));
        binFixups.Add((nisFlythroughOnline, flythroughOnlineName));
        binFixups.Add((nisOutro,            outroName));

        // OnlineNISs — 5 text refs (each a 4-byte pointer to a bin string).
        uint onlineNis0 = bin.AddString("default_online_nis_0");
        uint onlineNis1 = bin.AddString("default_online_nis_1");
        uint onlineNis2 = bin.AddString("default_online_nis_2");
        uint onlineNis3 = bin.AddString("default_online_nis_3");
        uint onlineNis4 = bin.AddString("default_online_nis_4");
        uint onlineNisArray = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)5);
            w.WriteBE((ushort)5);
            w.WriteBE((ushort)4);
            w.WriteBE((ushort)0);
            w.WriteBE(onlineNis0);
            w.WriteBE(onlineNis1);
            w.WriteBE(onlineNis2);
            w.WriteBE(onlineNis3);
            w.WriteBE(onlineNis4);
            w.WriteBE(0u);   // pad to 8B alignment after 5×4 = 20B → +4
        }));
        // PtrN fixups for each text-ref slot (after 8B header).
        binFixups.Add((onlineNisArray + 8u +  0u, onlineNis0));
        binFixups.Add((onlineNisArray + 8u +  4u, onlineNis1));
        binFixups.Add((onlineNisArray + 8u +  8u, onlineNis2));
        binFixups.Add((onlineNisArray + 8u + 12u, onlineNis3));
        binFixups.Add((onlineNisArray + 8u + 16u, onlineNis4));

        // RaceSpeechCountdown / RaceSpeechStart — 8B tSpeechInfo each. Retail
        // values from stock `races`: countdown=0000002703850000, start=0000002703860000.
        uint heatFamilySpeechCountdown = bin.AddBlob(Convert.FromHexString("0000002703850000"));
        uint heatFamilySpeechStart     = bin.AddBlob(Convert.FromHexString("0000002703860000"));

        // Default-leg placeholder Legs[1] — references the stock-shipped
        // default-leg row (`7CBD399D98BB0D37` is Lookup8 of stock's default
        // leg parent class hash; `2AC6DEE24A3B381B` is the default leg key).
        uint heatFamilyLegsArray = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)24);
            w.WriteBE((ushort)0);
            w.WriteBE(0x7CBD399D98BB0D37UL);   // class hash (tRaceLegDefinition class anchor)
            w.WriteBE(0x2AC6DEE24A3B381BUL);   // default-leg key hash (stock)
            w.WriteBE(0UL);
        }));

        var heatFamilyTypes = new[]
        {
            "EA::Reflection::Int32",
            "EA::Reflection::Text",
            "Sk8::Challenge::tFollowTheLeaderWarningInfo",
            "EA::Reflection::Bool",
            "EA::Reflection::Float",
            "EA::Reflection::Int16",
            "Sk8::Challenge::tRaceLegDefinition",
            "Sk8::Challenge::tRaceMusicPaceDefinition",
            "Sk8::tNISPlaybackDefinition",
            "Sk8::Challenge::tSpeechInfo",
            "Sk8::Challenge::tTriggerVolumeInstanceID",
            "Sk8::Challenge::tRaceBranchDefinition",
            "Sk8::Challenge::tLocationID",
            "EA::Reflection::Int8",
            "Sk8::Challenge::tRoundPaths",
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            Cls, raceFamilyKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("AIPathIndexOverride",         "EA::Reflection::Int32",                       0xFFFFFFFFu),
                VltAttribute.PointerNoFixup("AIPaths",                     "Sk8::Challenge::tRoundPaths",                 heatFamilyAiPaths,    0x00),
                VltAttribute.Inline        ("AirUnderRequirement",         "EA::Reflection::Int32",                       0u),
                VltAttribute.Inline        ("Description",                 "EA::Reflection::Text",                        heatFamilyDescription),
                VltAttribute.Inline        ("FollowMeLimit",               "EA::Reflection::Int32",                       0u),
                VltAttribute.Inline        ("FollowMeRemainingTimelimit",  "EA::Reflection::Int32",                       0u),
                VltAttribute.PointerNoFixup("FollowTheLeaderWarningInfo",  "Sk8::Challenge::tFollowTheLeaderWarningInfo", ftlEmpty,             0x02),
                VltAttribute.Inline        ("Include321",                  "EA::Reflection::Bool",                        0u),
                VltAttribute.Inline        ("KilledItTime",                "EA::Reflection::Float",                       0u),
                VltAttribute.Inline        ("KnockoutPerLeg",              "EA::Reflection::Int16",                       0u),
                // Legs: tRaceLegDefinition = single-member RefSpec wrapper →
                // typed-refspec ARRAY → NF=0x0A.
                VltAttribute.PointerNoFixup("Legs",                        "Sk8::Challenge::tRaceLegDefinition",          heatFamilyLegsArray,  0x0A),
                VltAttribute.Inline        ("LegTimeRequired",             "EA::Reflection::Int32",                       0u),
                // MusicPace: same shape as Legs/Gates → typed-refspec ARRAY → NF=0x0A.
                VltAttribute.PointerNoFixup("MusicPace",                   "Sk8::Challenge::tRaceMusicPaceDefinition",    musicPaceEmpty,       0x0A),
                VltAttribute.PointerNoFixup("NISFlythroughDefinition",     "Sk8::tNISPlaybackDefinition",                 nisFlythrough,        0x00),
                VltAttribute.PointerNoFixup("NISFlythroughDefinitionOnline","Sk8::tNISPlaybackDefinition",                nisFlythroughOnline,  0x00),
                VltAttribute.PointerNoFixup("NISOutroDefinition",          "Sk8::tNISPlaybackDefinition",                 nisOutro,             0x00),
                VltAttribute.Inline        ("NoPushing",                   "EA::Reflection::Bool",                        0u),
                VltAttribute.PointerNoFixup("OnlineNISs",                  "EA::Reflection::Text",                        onlineNisArray,       0x02),
                VltAttribute.Inline        ("RaceSpeechAILosesCode",       "EA::Reflection::Int32",                       0x0000038Eu),
                VltAttribute.Inline        ("RaceSpeechAIPassCode",        "EA::Reflection::Int32",                       0x0000038Cu),
                VltAttribute.Inline        ("RaceSpeechAIPassDelay",       "EA::Reflection::Int32",                       1u),
                VltAttribute.Inline        ("RaceSpeechAIWinsCode",        "EA::Reflection::Int32",                       0x0000038Du),
                VltAttribute.PointerNoFixup("RaceSpeechCountdown",         "Sk8::Challenge::tSpeechInfo",                 heatFamilySpeechCountdown, 0x00),
                VltAttribute.PointerNoFixup("RaceSpeechStart",             "Sk8::Challenge::tSpeechInfo",                 heatFamilySpeechStart,     0x00),
                VltAttribute.PointerNoFixup("RaceTerminateVolumes",        "Sk8::Challenge::tTriggerVolumeInstanceID",    raceTermVolEmpty,     0x02),
                // RandomBranches: tRaceBranchDefinition = single-member RefSpec
                // wrapper → typed-refspec ARRAY → NF=0x0A.
                VltAttribute.PointerNoFixup("RandomBranches",              "Sk8::Challenge::tRaceBranchDefinition",       randomBranchesEmpty,  0x0A),
                VltAttribute.Inline        ("SplitTimeDivisor",            "EA::Reflection::Float",                       0x41100000u),
                VltAttribute.Inline        ("StartLocation",               "Sk8::Challenge::tLocationID",                 0u),
                VltAttribute.Inline        ("TimeLimit",                   "EA::Reflection::Int32",                       0x0000003Cu),
                VltAttribute.Inline        ("TimeLimitWarning",            "EA::Reflection::Int32",                       0x0000001Eu),
                VltAttribute.Inline        ("Title",                       "EA::Reflection::Text",                        heatFamilyTitle),
                VltAttribute.Inline        ("WipeOutLimit",                "EA::Reflection::Int8",                        0u),
            },
            explicitTypes: heatFamilyTypes));

        // Bare instance parent.
        collections.Add(VltCollectionBuilder.BuildBareCollection(Cls, challengeKey, raceFamilyKey));

        // Per-heat instance rows.
        ulong legsClassHash = Lookup8Hashing.Hash("challenge_race_legs");
        for (int hi = 0; hi < heatList.Count; hi++)
        {
            var heat = heatList[hi];

            // Build Legs[] array: { class=challenge_race_legs,
            //                       key=Lookup8(<challengeKey>_<legIdx>),
            //                       0 } × legs-in-this-heat.
            var heatLegIndices = new List<int>();
            for (int li = 0; li < legList.Count; li++)
                if (legList[li].HeatIndex == hi) heatLegIndices.Add(li);

            uint heatLegsArray = bin.AddBlob(VltPayload.Build(w =>
            {
                w.WriteBE((ushort)heatLegIndices.Count);
                w.WriteBE((ushort)heatLegIndices.Count);
                w.WriteBE((ushort)24);
                w.WriteBE((ushort)0);
                foreach (int li in heatLegIndices)
                {
                    w.WriteBE(legsClassHash);
                    w.WriteBE(Lookup8Hashing.Hash(spec.LegKey(li)));
                    w.WriteBE(0UL);
                }
            }));

            // Per-heat NIS placeholders. Retail race_dwgh_01 ships these as
            // zero-filled 64B blobs with the leading u32 pointing at a bin
            // string. We mirror the shape using per-heat default names.
            uint heatFlythroughOnlineName = bin.AddString($"{challengeKey}_{hi}_flythrough_online");
            uint heatOutroName            = bin.AddString($"{challengeKey}_{hi}_outro");
            uint heatNisFlythroughOnline  = bin.AddBlob(BuildNisPlaybackDef64(heatFlythroughOnlineName));
            uint heatNisOutro             = bin.AddBlob(BuildNisPlaybackDef64(heatOutroName));
            binFixups.Add((heatNisFlythroughOnline, heatFlythroughOnlineName));
            binFixups.Add((heatNisOutro,            heatOutroName));

            uint heatStartLocStr = bin.AddString(
                heat.StartPosition != null
                    ? $"{challengeKey}_{hi}_startlocator"
                    : $"{challengeKey}_startlocator");

            collections.Add(VltCollectionBuilder.BuildCollection(
                Cls, spec.HeatKey(hi), challengeKey, 0u,
                new[]
                {
                    VltAttribute.Inline        ("KilledItTime",               "EA::Reflection::Float",                BitConverter.SingleToUInt32Bits(heat.KilledItSeconds)),
                    // Legs: tRaceLegDefinition = typed-refspec ARRAY → NF=0x0A.
                    VltAttribute.PointerNoFixup("Legs",                       "Sk8::Challenge::tRaceLegDefinition",   heatLegsArray,             0x0A),
                    VltAttribute.PointerNoFixup("NISFlythroughDefinitionOnline","Sk8::tNISPlaybackDefinition",        heatNisFlythroughOnline,   0x00),
                    VltAttribute.PointerNoFixup("NISOutroDefinition",         "Sk8::tNISPlaybackDefinition",          heatNisOutro,              0x00),
                    VltAttribute.Inline        ("StartLocation",              "Sk8::Challenge::tLocationID",          heatStartLocStr),
                    VltAttribute.Inline        ("TimeLimit",                  "EA::Reflection::Int32",                (uint)heat.TimeLimitSeconds),
                }));
        }
    }

    /// 64-byte tNISPlaybackDefinition. The leading u32 is a bin-pool pointer
    /// to the NIS-name string (PtrN-fixed by the caller — the offset of THIS
    /// blob + 0 is the fixup site, target is the name string offset). Bytes
    /// [4..63] are zero-filled — retail per-heat NIS playback definitions
    /// leave the camera params zero when the engine should fall back to the
    /// schema-shipped default NIS curves.
    private static byte[] BuildNisPlaybackDef64(uint namePtrOnDisk) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(namePtrOnDisk);
            for (int i = 0; i < 60; i++) w.Write((byte)0);
        });

    /// VolumeID lookup for **split-time triggers** (NOT per-race gates).
    /// Split-time triggers reference authored volumes in the world / mission
    /// PSG — we use the authored name directly. Per-race gates use the
    /// synthesised canonical name via <see cref="RaceVolumeNaming.GateVolumeId"/>.
    ///
    /// TODO: IDA-verify the engine's cTriggerVolumeManager bind path. Stock
    /// VolumeIDs (`2c7017060022xxxx`) suggest the engine derives them from
    /// `(world-stream-id, volume-index)` not from `Lookup8(name)`. If a
    /// byte-diff against retail shows mismatches, switch to the engine's
    /// formula here.
    private static ulong ResolveVolumeId(TriggerVolumeRef volume) =>
        Lookup8Hashing.Hash(volume.Name);
}
