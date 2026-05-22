using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Builds the race framework family row that lives inside
/// `dlc_&lt;framework&gt;_local_data_framework.vlt`. Content mirrors retail
/// `challenge_local_data/dlc_dwgh_races`: 30 race-related attributes plus
/// `HostCharacter` as an inline attribute (NF=0x40, data=0x68 — the death-race
/// co-host #1 value). Parent=&lt;framework&gt;.
///
/// Note: retail dwgh_races encodes `HostCharacter` into the row's layout block
/// at +0 instead of as a separate attribute. We use the attribute form so the
/// row matches `OtsFrameworkOwnTheSpotsRow`'s known-working shape (48-byte
/// zero layout). Functionally equivalent — `sub_733450`'s parent-chain
/// inline-record walk locates HostCharacter via either representation.
///
/// Per-instance race rows (`race_&lt;key&gt;`) chain through this row. Without
/// it, the engine's parent-chain walker (sub_737790 → j_Vault_FindCollectionByHash)
/// can't resolve `Parent="dlc_&lt;framework&gt;_races"` on the per-race
/// challenge_local_data instance row and derefs NULL trying to follow the chain
/// back to dlc_&lt;framework&gt;. Result: AV reading 0x20 the moment "Start Race"
/// is pressed.
///
/// Reference dump:
/// `AttribDumpOut/dlc_dwgh_framework/Dump/Skate3_skater/Collections/challenge_local_data/dlc_dwgh_races.xml`
/// Reference bin:
/// `StockGameData/DannyWayDLC/db/dlc_dwgh_local_data_framework.bin`
/// (HostCharacter encoded into the row's 48B layout block at +0; ChallengePart
/// pool string "a" at 0x2FF; NIS bin-offset references at 0x317/0x327/0x337
/// resolve to "Online_Celeb_02" / "Online_Celeb_03" / "Sk3_Win_Med_3_Stoked";
/// "highlight_film_killed" at 0x301; "race_gate_next" at 0x34C).
public static class RaceFrameworkRacesRow
{
    // ── Magic hashes (precomputed; keys not in our Lookup8 reverse corpus) ──
    private const ulong AudioRaceTuningKey       = 0xD7EDBD362D7D2152UL;   // aud_race row reference

    // RibbonIndicator class hash matches VltBinHelpers comment ("ribbon_indicator"
    // = 0x3297556BECB605E2). Three tSplineBankObject slots (RaceFinalGateRibbon,
    // RaceGateGreyRibbon, RaceLegFinishGateRibbon) all share the same
    // ribbon_indicator key (gates/standardrace variant) for their first 24B half
    // and differ only in the ribbon_spline_colour key on their second 24B half.
    private const ulong RibbonIndicatorClass     = 0x3297556BECB605E2UL;   // Lookup8("ribbon_indicator")
    private const ulong RibbonIndicatorRaceKey   = 0x624E29026A97C02EUL;   // shared race-gate indicator key

    private const ulong RibbonSplineColourClass  = 0xF0DB8767D611AF94UL;   // Lookup8("ribbon_spline_colour")
    private const ulong RibbonSplineFinalKey     = 0x5D7D090ED988F3D3UL;   // RaceFinalGateRibbon colour key
    private const ulong RibbonSplineGreyKey      = 0x54603FFC31D8DBF3UL;   // RaceGateGreyRibbon colour key
    private const ulong RibbonSplineLegFinishKey = 0x58778BCA1505DE75UL;   // RaceLegFinishGateRibbon colour key

    // Anonymous attribute key hash — appears as "Hash_xxxx" in retail dumps
    // because the canonical key isn't in AttribCLI's Keys.txt. Must round-trip
    // via *RawHash so FieldKeyHash matches the engine's attribute-table lookup.
    // (MaxMiniMapIcons and OnlineOutroSoloNIS both ARE in the dehash dictionary —
    // those go through the regular `Inline`/`PointerNoFixup` factories.)
    private const ulong RawHash_9DDC = 0x9DDC567540219ACCUL;   // Bool, every shipping DLC declares this on framework rows

    public static CollectionBlob Build(string frameworkKey, BinPoolBuilder bin, List<(uint, uint)> binFixups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(binFixups);

        // ── Bin pool entries ───────────────────────────────────────────────
        // HALIDs + display strings — values mirror retail Danny Way bytes.
        uint halChalComplete = bin.AddString("ID_CHALLENGE_CENTRAL_MESSAGE_OBJECTIVE_CHALLENGECOMPLETE");
        uint halChalFailed   = bin.AddString("ID_CHALLENGE_CENTRAL_MESSAGE_OBJECTIVE_CHALLENGEFAILED");
        uint challengePartStr = bin.AddString("a");
        uint highlightFilmKilledStr = bin.AddString("highlight_film_killed");
        uint raceGateGreyIconStr = bin.AddString("race_gate_next");

        // tLocationID slots are tLocationName-string pointers patched by PtrN.
        // Family-row defaults are empty strings — per-instance rows override
        // both (StartLocation = "<challengeKey>_startlocator",
        // OnlineEndCameraLocation = "<challengeKey>_endcamera").
        uint emptyLocationStr = bin.AddString("");

        // NIS playback definition string slots — author requested NO cutscenes
        // on this DLC. Pointing all three at an empty string in the bin pool
        // makes the engine's NIS loader read "" and skip playback (this is the
        // same mechanism stock uses for races that ship no celebration NIS —
        // the heat-row default `NISOutroDefinition` points at bin offset 0x8
        // which is an empty string in the .bin).
        uint nisOnlineOutroStr = bin.AddString("");
        uint nisOnlineHash2Str = bin.AddString("");
        uint nisOutroStr       = bin.AddString("");

        // ── Inline blobs ───────────────────────────────────────────────────

        // AudioRaceTuning (Attrib::RefSpec, 24B). classKey=0 (typed by attribute
        // TypeName), collectionKey=D7EDBD36…, cache slot null.
        uint audioRaceTuningOff = bin.AddBlob(VltBinHelpers.BuildRefSpec24(0UL, AudioRaceTuningKey));

        // IntroPresentationEvents — 8B empty array header, stride=16
        // (tChallengePresentationEvent).
        uint introPresEvtArrOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));

        // Three tNISPlaybackDefinition blobs — 64B each, first 4B is a bin
        // pool offset to a string (PtrN-patched at runtime), rest zero.
        uint onlineOutroNisOff = bin.AddBlob(new byte[64]);
        binFixups.Add((onlineOutroNisOff, nisOnlineOutroStr));

        uint hash_C507_NisOff = bin.AddBlob(new byte[64]);
        binFixups.Add((hash_C507_NisOff, nisOnlineHash2Str));

        uint outroNisOff = bin.AddBlob(new byte[64]);
        binFixups.Add((outroNisOff, nisOutroStr));

        // Three tSplineBankObject blobs (48B each). Layout = two RefSpec24s
        // back-to-back. First RefSpec is the ribbon_indicator class+key (shared
        // across all three slots — standard-race gate indicator). Second RefSpec
        // is the ribbon_spline_colour class with a per-slot key (different
        // colour variant for the final gate / grey gate / leg-finish gate).
        byte[] BuildSplineBankObject(ulong colourKey)
        {
            byte[] a = VltBinHelpers.BuildRefSpec24(RibbonIndicatorClass, RibbonIndicatorRaceKey);
            byte[] b = VltBinHelpers.BuildRefSpec24(RibbonSplineColourClass, colourKey);
            byte[] o = new byte[48];
            Buffer.BlockCopy(a, 0, o, 0, 24);
            Buffer.BlockCopy(b, 0, o, 24, 24);
            return o;
        }
        uint raceFinalGateRibbonOff = bin.AddBlob(BuildSplineBankObject(RibbonSplineFinalKey));
        uint raceGateGreyRibbonOff  = bin.AddBlob(BuildSplineBankObject(RibbonSplineGreyKey));
        uint raceLegFinishGateRibbonOff = bin.AddBlob(BuildSplineBankObject(RibbonSplineLegFinishKey));

        // RaceHeats — 1-element tRaceHeatDefinition[] (24B per element). The
        // family row references the framework's per-class race-heats family row
        // (`challenge_race_heats/<framework>_races`) which the per-race VLT
        // emits. Per-instance challenge_local_data rows override RaceHeats so
        // this reference is the inheritance fallback only.
        ulong heatsClassHash = Lookup8Hashing.Hash("challenge_race_heats");
        ulong raceFamilyHeatKey = Lookup8Hashing.Hash($"{frameworkKey}_races");
        uint raceHeatsArrOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);    // count
            w.WriteBE((ushort)1);    // capacity
            w.WriteBE((ushort)24);   // typeSize (tRaceHeatDefinition)
            w.WriteBE((ushort)0);    // align
            w.WriteBE(heatsClassHash);
            w.WriteBE(raceFamilyHeatKey);
            w.WriteBE(0UL);          // cache slot
        }));

        // ── Row layout block ────────────────────────────────────────────────
        // 48B zero layout — matches OtsFrameworkOwnTheSpotsRow's shape. The
        // 4B stub layout that an earlier OTS attempt used was wrong (engine
        // read 44B of adjacent strings as schema fields), so 48B is the
        // verified-safe size. HostCharacter is carried as a separate inline
        // attribute below (NF=0x40) rather than encoded into the layout
        // bytes — the engine's schema-mode iteration (sub_733450) finds it
        // via the parent-chain inline-record walk either way, and the
        // attribute form is the pattern OTS uses successfully.
        uint layoutOff = bin.AddBlob(new byte[48]);

        // ── Attributes ─────────────────────────────────────────────────────
        var attrs = new[]
        {
            VltAttribute.PointerNoFixup("AudioRaceTuning",                       "Attrib::RefSpec",                              audioRaceTuningOff,         0x08),
            VltAttribute.InlineRawHash ("Hash_9DDC567540219ACC",                 "EA::Reflection::Bool",                         0u,                         RawHash_9DDC),
            // HostCharacter — inline NF=0x40 enum, data=0x68 = co-host #1
            // (the eSk8Characters value every shipping death-race family row
            // uses, including retail dwgh_races where it's encoded into the
            // layout block at +0). Carrying it as an inline attribute matches
            // OtsFrameworkOwnTheSpotsRow's pattern: sub_733450's parent-chain
            // inline-record walk finds it, the caller's `lbz r9, 0xf(r29)`
            // succeeds, and inheritance resolves whether downstream rows
            // override HostCharacter or fall through.
            VltAttribute.Inline        ("HostCharacter",                         "Sk8::Audio::eSk8Characters",                   0x00000068u),
            VltAttribute.Inline        ("CentralMessageHALIDChallengeComplete",  "EA::Reflection::Text",                         halChalComplete),
            VltAttribute.Inline        ("CentralMessageHALIDChallengeFailed",    "EA::Reflection::Text",                         halChalFailed),
            VltAttribute.Inline        ("Challenge_Index",                       "EA::Reflection::UInt8",                        0x01000000u),
            VltAttribute.Inline        ("ChallengeInfoShowDesc",                 "EA::Reflection::Bool",                         0u),
            VltAttribute.Inline        ("ChallengeMusicType",                    "Sk8::Audio::eChallengeMusicType",              0x00000002u),
            VltAttribute.Inline        ("ChallengePart",                         "EA::Reflection::Text",                         challengePartStr),
            VltAttribute.Inline        ("ChallengeStage",                        "EA::Reflection::UInt8",                        0x02000000u),
            VltAttribute.PointerNoFixup("IntroPresentationEvents",               "Sk8::Challenge::tChallengePresentationEvent",  introPresEvtArrOff,         0x02),
            VltAttribute.Inline        ("KilledItScore",                         "EA::Reflection::Int32",                        0u),
            VltAttribute.Inline        ("MaxMiniMapIcons",                       "EA::Reflection::UInt32",                       0x00000003u),
            VltAttribute.Inline        ("NISHighlightReelKilledIt",              "EA::Reflection::Text",                         highlightFilmKilledStr),
            VltAttribute.Inline        ("OnlineEndCameraLocation",               "Sk8::Challenge::tLocationID",                  emptyLocationStr),
            VltAttribute.PointerNoFixup("OnlineOutroNIS",                        "Sk8::tNISPlaybackDefinition",                  onlineOutroNisOff,          0x00),
            VltAttribute.PointerNoFixup("OnlineOutroSoloNIS",                    "Sk8::tNISPlaybackDefinition",                  hash_C507_NisOff,           0x00),
            VltAttribute.PointerNoFixup("OutroNIS",                              "Sk8::tNISPlaybackDefinition",                  outroNisOff,                0x00),
            VltAttribute.Inline        ("Persistent",                            "EA::Reflection::Bool",                         0u),
            VltAttribute.Inline        ("PlaceHigherToWin",                      "EA::Reflection::Int16",                        0x00010000u),
            // tSplineBankObject is two back-to-back typed RefSpec24s — engine
            // wants NF=0x08 (typed-refspec single). NF=0x00 made the engine read
            // it as a raw byte blob and skip the class-key slot during inheritance
            // walks; that's the new crash we just chased through Lua VM.
            VltAttribute.PointerNoFixup("RaceFinalGateRibbon",                   "Sk8::Challenge::tSplineBankObject",            raceFinalGateRibbonOff,     0x08),
            VltAttribute.Inline        ("RaceGateGreyIcon",                      "EA::Reflection::Text",                         raceGateGreyIconStr),
            VltAttribute.PointerNoFixup("RaceGateGreyRibbon",                    "Sk8::Challenge::tSplineBankObject",            raceGateGreyRibbonOff,      0x08),
            VltAttribute.Inline        ("RaceGateSkipable",                      "EA::Reflection::Bool",                         0x01000000u),
            // tRaceHeatDefinition[] elements are 24B typed RefSpecs (classKey +
            // collectionKey + cache). Engine wants NF=0x0A (typed-refspec array).
            // NF=0x02 (non-typed array) makes the array walker misread element
            // stride / skip the class-key field on each element.
            VltAttribute.PointerNoFixup("RaceHeats",                             "Sk8::Challenge::tRaceHeatDefinition",          raceHeatsArrOff,            0x0A),
            VltAttribute.PointerNoFixup("RaceLegFinishGateRibbon",               "Sk8::Challenge::tSplineBankObject",            raceLegFinishGateRibbonOff, 0x08),
            VltAttribute.Inline        ("RaceRefreshTime",                       "EA::Reflection::Float",                        0x3F800000u),
            VltAttribute.Inline        ("RaceTrickForTimeMultiplier",            "EA::Reflection::Int16",                        0x000A0000u),
            VltAttribute.Inline        ("RaceType",                              "Sk8::Challenge::eRaceType",                    0u),
            VltAttribute.Inline        ("StartLocation",                         "Sk8::Challenge::tLocationID",                  emptyLocationStr),
            VltAttribute.Inline        ("TimeToWaitBeforeReplay",                "EA::Reflection::Float",                        0x40000000u),
            VltAttribute.Inline        ("UseModifiedAIPathNames",                "EA::Reflection::Bool",                         0u),
        };

        // Type table — every TypeName used by the layout (HostCharacter) +
        // attribute records. Order mirrors retail dwgh_races so the type-index
        // assignment matches as closely as possible.
        string[] types =
        {
            "Sk8::Audio::eSk8Characters",                       // 0 (HostCharacter inline attr)
            "Attrib::RefSpec",                                  // 1 (AudioRaceTuning)
            "EA::Reflection::Bool",                             // 2 (multiple)
            "EA::Reflection::Text",                             // 3 (HALIDs / ChallengePart / NISHighlightReelKilledIt / RaceGateGreyIcon)
            "EA::Reflection::UInt8",                            // 4 (Challenge_Index, ChallengeStage)
            "Sk8::Audio::eChallengeMusicType",                  // 5
            "Sk8::Challenge::tChallengePresentationEvent",      // 6 (IntroPresentationEvents)
            "EA::Reflection::Int32",                            // 7 (KilledItScore)
            "EA::Reflection::UInt32",                           // 8 (Hash_F9B0…)
            "Sk8::Challenge::tLocationID",                      // 9 (OnlineEndCameraLocation, StartLocation)
            "Sk8::tNISPlaybackDefinition",                      // 10 (three NIS slots)
            "EA::Reflection::Int16",                            // 11 (PlaceHigherToWin, RaceTrickForTimeMultiplier)
            "Sk8::Challenge::tSplineBankObject",                // 12 (three ribbon slots)
            "Sk8::Challenge::tRaceHeatDefinition",              // 13 (RaceHeats)
            "EA::Reflection::Float",                            // 14 (RaceRefreshTime, TimeToWaitBeforeReplay)
            "Sk8::Challenge::eRaceType",                        // 15 (RaceType)
        };

        return VltCollectionBuilder.BuildCollection(
            "challenge_local_data", $"{frameworkKey}_races", frameworkKey,
            layoutOff, attrs,
            explicitTypes: types);
    }
}
