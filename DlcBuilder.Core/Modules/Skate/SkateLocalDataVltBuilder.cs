using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest.Vlt;
using DlcBuilder.Modules.DlcManifest.Vlt.Templates;

namespace DlcBuilder.Modules.Skate;

/// `db/challenge_local_data/skate_&lt;key&gt;.vlt` — per-spot S.K.A.T.E. VLT.
///
/// Audited against all 10 base-game retail instances:
/// `StockGameData/AttribDumpOut/skate_{dwtn_01..04,indu_01..03,univ_01..03}`.
///
/// Two rows:
///   1. challenge_local_data/default (parent=Hash_0) — shared 45-attr default.
///   2. challenge_local_data/skate_&lt;key&gt; (parent=`s_k_a_t_e`) — 7 attrs / 5 types:
///        ChallengeBoundary        (tTriggerVolumeInstanceID)
///        IntroPresentationEvents  (tChallengePresentationEvent, count=1)
///        SpotVolumes              (tTriggerVolumeInstanceID, count=spec.SpotVolumes.Count)
///        TurnBasedAttemptSpawnPoint (tLocationID)
///        TurnBasedStartVolume     (tTriggerVolumeInstanceID)
///        TurnBasedWaitingLocation (tLocationID)
///        VisualIndicators         (tVisualEditorData, count=spec.VisualIndicators.Count)
///
/// Parent on base-game `s_k_a_t_e` framework row (lives in base-game
/// challenge_local_data, always loaded). All boilerplate (HALIDs,
/// HighlightDefinition, TimeLimit, etc.) inherits from it.
public static class SkateLocalDataVltBuilder
{
    public sealed record SkateLocalDataArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static SkateLocalDataArtifacts Build(SkateChallengeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        const string parentKey = "s_k_a_t_e";
        string fileName = spec.ChallengeKey;
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        var collections = new List<CollectionBlob>();
        var binFixups = new List<(uint, uint)>();

        FreeskateChallengeLocalDataTemplate.AppendDefaultHash0Row(bin, collections);

        // ── Bin pool: trigger volume names + locator names ────────────────────
        string distName = spec.Map.WorldStreamName;

        uint challengeBoundaryNameOff = bin.AddString(
            $"{distName}|{spec.ChallengeKey}_challengeboundary_01|0x{spec.ChallengeBoundary.Guid:x16}");
        uint startVolumeNameOff = bin.AddString(
            $"{distName}|{spec.ChallengeKey}_turnbasedstartvolume|0x{spec.TurnBasedStartVolume.Guid:x16}");

        var spotVolumeNameOffs = new uint[spec.SpotVolumes.Count];
        for (int i = 0; i < spec.SpotVolumes.Count; i++)
        {
            spotVolumeNameOffs[i] = bin.AddString(
                $"{distName}|{spec.ChallengeKey}_spotvolume_{i + 1:D2}|0x{spec.SpotVolumes[i].Guid:x16}");
        }

        uint startLocatorNameOff = bin.AddString(spec.StartLocatorName);
        uint waitLocatorNameOff  = bin.AddString(spec.WaitLocatorName);

        var viNameOffs = new uint[spec.VisualIndicators.Count];
        for (int i = 0; i < spec.VisualIndicators.Count; i++)
            viNameOffs[i] = bin.AddString(spec.VisualIndicatorName(i + 1));

        // ── ChallengeBoundary single struct (16 B) ────────────────────────────
        uint challengeBoundaryStub = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(spec.ChallengeBoundary.Guid));
        uint startVolumeStub       = bin.AddBlob(VltBinHelpers.BuildTriggerVolumeIdStruct(spec.TurnBasedStartVolume.Guid));
        binFixups.Add((challengeBoundaryStub, challengeBoundaryNameOff));
        binFixups.Add((startVolumeStub,       startVolumeNameOff));

        // ── SpotVolumes[N] — N×16 B elements ─────────────────────────────────
        int nSpot = spec.SpotVolumes.Count;
        uint spotVolumesArr = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)nSpot);
            w.WriteBE((ushort)nSpot);
            w.WriteBE((ushort)16);
            w.WriteBE((ushort)0);
            for (int i = 0; i < nSpot; i++)
            {
                w.WriteBE(0u);                       // VolumeName PtrN — fixed below
                w.WriteBE(0u);                       // padding
                w.WriteBE(spec.SpotVolumes[i].Guid); // VolumeID
            }
        }));
        for (int i = 0; i < nSpot; i++)
            binFixups.Add((spotVolumesArr + 8u + (uint)(i * 16), spotVolumeNameOffs[i]));

        // ── IntroPresentationEvents[1] ───────────────────────────────────────
        // All 10 base instances ship count=1. Element value matches the
        // s_k_a_t_e framework row's default element shape (5 B header + 4 B
        // event id + 4 B trailing pad). Friend wrote empty (count=0), which
        // diverges from base — fix.
        uint introPresArr = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);   // count = 1
            w.WriteBE((ushort)1);   // capacity = 1
            w.WriteBE((ushort)16);  // typeSize = 16
            w.WriteBE((ushort)0);   // padding
            // 16 B element — type=5 (default), arg1/arg2/pad mirror the bytes
            // visible in base s_k_a_t_e framework row's IntroPresentationEvents[0].
            w.WriteBE(0x00000005U);
            w.WriteBE(0x000001C6U);
            w.WriteBE(0x0000003CU);
            w.WriteBE(0x00000000U);
        }));

        // ── VisualIndicators[N] — N × 80 B tVisualEditorData ─────────────────
        ulong ribbonIndicatorClassHash    = Lookup8Hashing.Hash("ribbon_indicator");
        ulong ribbonIndicatorArrowHash    = Lookup8Hashing.Hash("arrow");
        ulong ribbonSplineColourClassHash = Lookup8Hashing.Hash("ribbon_spline_colour");
        ulong ribbonSplineColourDefault   = Lookup8Hashing.Hash("default");

        int nVi = spec.VisualIndicators.Count;
        uint visualIndicatorsArr = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE((ushort)nVi);
            w.WriteBE((ushort)nVi);
            w.WriteBE((ushort)80);
            w.WriteBE((ushort)0);
            for (int i = 0; i < nVi; i++)
            {
                w.WriteBE(0u);  // +0x00
                w.WriteBE(0u);  // +0x04
                w.WriteBE(0u);  // +0x08 location-kind = 0 (Skate)
                w.WriteBE(0u);  // +0x0C
                w.WriteBE(0UL); // +0x10
                w.WriteBE(0u);  // +0x18 Location PtrN — fixed below
                w.WriteBE(0u);  // +0x1C
                w.Write(VltBinHelpers.BuildRefSpec24(ribbonIndicatorClassHash,    ribbonIndicatorArrowHash));    // +0x20
                w.Write(VltBinHelpers.BuildRefSpec24(ribbonSplineColourClassHash, ribbonSplineColourDefault));   // +0x38
            }
        }));
        for (int i = 0; i < nVi; i++)
            binFixups.Add((visualIndicatorsArr + 8u + (uint)(i * 80) + 0x18u, viNameOffs[i]));

        // ── Per-instance layout blob ─────────────────────────────────────────
        uint layoutBlobOff = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(0x00000068U);                            // +0x00 HostCharacter
            w.WriteBE(0u);                                     // +0x04 reserved
            w.WriteBE(0u);                                     // +0x08 ChallengeBoundary name ptr (PtrN)
            w.WriteBE(0u);                                     // +0x0C reserved
            w.WriteBE(spec.ChallengeBoundary.Guid);            // +0x10 ChallengeBoundary GUID
            w.WriteBE(0u);                                     // +0x18 SpotVolume[0] name ptr slot (PtrN)
            w.WriteBE(0u);                                     // +0x1C reserved
            w.WriteBE(nSpot > 0 ? spec.SpotVolumes[0].Guid : 0UL); // +0x20 first SpotVolume GUID
            w.WriteBE((ushort)nSpot);                          // +0x28 array header — count
            w.WriteBE((ushort)nSpot);                          // capacity
            w.WriteBE((ushort)0x10);
            w.WriteBE((ushort)0);
        }));
        binFixups.Add((layoutBlobOff + 0x08u, challengeBoundaryNameOff));
        if (nSpot > 0)
            binFixups.Add((layoutBlobOff + 0x18u, spotVolumeNameOffs[0]));

        string[] rowTypes =
        {
            "Sk8::Audio::eSk8Characters",                  // 0 sentinel
            "Sk8::Challenge::tTriggerVolumeInstanceID",    // 1
            "Sk8::Challenge::tChallengePresentationEvent", // 2
            "Sk8::Challenge::tLocationID",                 // 3
            "Sk8::Challenge::tVisualEditorData",           // 4
        };

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_local_data", fileName, parentKey, layoutBlobOff,
            new[]
            {
                VltAttribute.PointerNoFixup("ChallengeBoundary",          "Sk8::Challenge::tTriggerVolumeInstanceID",    challengeBoundaryStub, 0x00),
                VltAttribute.PointerNoFixup("IntroPresentationEvents",    "Sk8::Challenge::tChallengePresentationEvent", introPresArr,          0x02),
                VltAttribute.PointerNoFixup("SpotVolumes",                "Sk8::Challenge::tTriggerVolumeInstanceID",    spotVolumesArr,        0x02),
                VltAttribute.Inline        ("TurnBasedAttemptSpawnPoint", "Sk8::Challenge::tLocationID",                 startLocatorNameOff),
                VltAttribute.PointerNoFixup("TurnBasedStartVolume",       "Sk8::Challenge::tTriggerVolumeInstanceID",    startVolumeStub,       0x00),
                VltAttribute.Inline        ("TurnBasedWaitingLocation",   "Sk8::Challenge::tLocationID",                 waitLocatorNameOff),
                VltAttribute.PointerNoFixup("VisualIndicators",           "Sk8::Challenge::tVisualEditorData",           visualIndicatorsArr,   0x0A),
            },
            explicitTypes: rowTypes,
            numTypesDup: 6));

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, binFixups);
        byte[] binBytes = bin.BuildBinFile();
        return new SkateLocalDataArtifacts(fileName, vltBytes, binBytes);
    }
}
