using DlcBuilder.Builders;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Appends race progression-state-machine rows to the DLC's
/// `progressionbanks/dlc_&lt;framework&gt;.vlt`. Mirrors the per-OTS portion of
/// <see cref="ProgressionBanksVltBuilder"/> (the `perOtsRows` band).
///
/// Per race (verified against retail Danny Way
/// `AttribDumpOut/dlc_dwgh_progressionbanks/Dump/Skate3_skater/Collections/`):
///
///   progression_state/&lt;key&gt;_complete       2 attrs: Handlers + Name="complete"
///   progression_stategraph/&lt;key&gt;_stategraph 2 attrs: Name + StateNodes[1]→complete
///
/// Plus one **shared** row emitted once per build when any races exist:
///
///   progression_handler/&lt;framework&gt;_race_handler
///       3 attrs: ByteCode + MessageID=0xF0D714B1 + Name="stateenter"
///
/// All per-race state rows reference this single shared handler via a 16-byte
/// `ClassRefSpec_progression_handler` array. The handler's bytecode dispatches
/// `ChangeState("complete")` on the `OnChallengeComplete` message, identical
/// to OTS — the message + state-transition semantics are challenge-type-agnostic
/// (the engine fires the same `0xF0D714B1` message regardless of challenge kind).
///
/// PtrN coverage: all attribute factories route through
/// <see cref="VltAttributeFlags.NeedsPtrN"/> — pointer-backed attrs
/// (Text / RefSpec / ClassRefSpec_* / arrays) auto-register PtrN entries. The
/// handler's `ByteCode` Attrib::Blob (NF=0x00) also auto-PtrN's. The bytecode
/// is patched at bytes [65..80] with the row's own key hash by
/// <see cref="OtsCompleteHandlerBytecode.Build"/> — engine binds `h_&lt;HASH&gt;`
/// global at Lua chunk load.
public static class RaceProgressionRowsBuilder
{
    /// Append race progression rows to <paramref name="collections"/>.
    /// Returns the per-race complete-state row hashes (one per race) so the
    /// caller can include them in DLC-wide stategraph / achievement-chain
    /// StateNodes arrays alongside OTS complete-state hashes (analogous to
    /// how <see cref="ProgressionBanksVltBuilder"/> builds the
    /// `&lt;halidStem&gt;_ots_challenges_stategraph` row from OTS hashes).
    public static IReadOnlyList<ulong> AppendProgressionRows(
        string frameworkKey,
        IReadOnlyList<RaceChallengeSpec> raceSpecs,
        BinPoolBuilder bin,
        List<CollectionBlob> collections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);
        ArgumentNullException.ThrowIfNull(raceSpecs);
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(collections);

        if (raceSpecs.Count == 0) return Array.Empty<ulong>();

        // ── Shared race stateenter handler ─────────────────────────────────
        // Same bytecode shape as OTS's handler — the `ChangeState("complete")`
        // chunk is challenge-type-agnostic. Distinct row key
        // `<framework>_race_handler` keeps race state rows from accidentally
        // dispatching through the OTS handler (their bytecode hash patches
        // differ by row key).
        string handlerRowKey = $"{frameworkKey}_race_handler";
        byte[] handlerByteCode = OtsCompleteHandlerBytecode.Build(handlerRowKey);
        uint handlerByteCodeOff = bin.AddBlob(handlerByteCode);
        uint handlerNameStateEnterPtr = bin.AddString("stateenter");
        collections.Add(VltCollectionBuilder.BuildCollection(
            "progression_handler", handlerRowKey, frameworkKey, 0u,
            new[]
            {
                VltAttribute.PointerNoFixup("ByteCode",  "Attrib::Blob",           handlerByteCodeOff,        0x00),
                VltAttribute.Inline        ("MessageID", "EA::Reflection::UInt32", 0xF0D714B1u),
                VltAttribute.Inline        ("Name",      "EA::Reflection::Text",   handlerNameStateEnterPtr),
            },
            explicitTypes: new[] { "Attrib::Blob", "EA::Reflection::UInt32", "EA::Reflection::Text" },
            numTypesDup: 4));

        // Single 1-element `ClassRefSpec_progression_handler` array referenced
        // by every per-race state row's `Handlers` slot. Reusing the same
        // blob offset across all states keeps the bin small (the engine
        // resolves the same handler chain every time).
        ulong handlerRowHash = Lookup8Hashing.Hash(handlerRowKey);
        uint sharedHandlersArr = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);   // count
            w.WriteBE((ushort)1);   // capacity
            w.WriteBE((ushort)16);  // typeSize (ClassRefSpec_progression_handler stride)
            w.WriteBE((ushort)0);   // align
            w.WriteBE(handlerRowHash);
            w.WriteBE(0UL);
        }));

        // ── Per-race rows ──────────────────────────────────────────────────
        var perRaceCompleteHashes = new List<ulong>(raceSpecs.Count);
        foreach (RaceChallengeSpec race in raceSpecs)
        {
            string completeStateKey = $"{race.ChallengeKey}_complete";
            string stategraphKey    = $"{race.ChallengeKey}_stategraph";
            ulong  completeStateHash = Lookup8Hashing.Hash(completeStateKey);
            perRaceCompleteHashes.Add(completeStateHash);

            // progression_state/<key>_complete — 2 attrs Handlers + Name.
            // Name="complete" (short, NOT the full row key). The achievement
            // dispatch reads u32 BE at name_ptr+8 mid-string and dereferences
            // as a pointer; long Names crash inside sub_609530. DW ships
            // generic short names ("initial"/"complete"/"achievements") for
            // exactly this reason.
            uint completeNamePtr = bin.AddString("complete");
            collections.Add(VltCollectionBuilder.BuildCollection(
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

            // progression_stategraph/<key>_stategraph — 2 attrs Name +
            // StateNodes RefSpec[1]→<key>_complete. The single StateNode
            // entry tells the engine which state the graph starts in (the
            // initial state is implicit / inherited from class defaults).
            uint stategraphNamePtr = bin.AddString(stategraphKey);
            uint stateNodesArr = bin.AddBlob(
                VltBinHelpers.BuildSingleProgressionStateRefArray(completeStateKey));
            collections.Add(VltCollectionBuilder.BuildCollection(
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

        return perRaceCompleteHashes;
    }
}
