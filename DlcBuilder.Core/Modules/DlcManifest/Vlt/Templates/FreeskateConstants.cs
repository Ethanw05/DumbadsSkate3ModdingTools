using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt.Templates;

/// Verbatim byte-templated constants pulled from retail Danny Way + Maloof
/// challengebanks vlts. Used by the freeskate challengebanks builder so its
/// per-DLC `freeskate_locations` row chains correctly through the base-game
/// freeskate template + state graph.
public static class FreeskateConstants
{
    /// 32-byte tVaultedRefSpec → base-game `challenge_local_data/freeskate_locations`.
    /// First 16 bytes = (classHash || keyHash), trailing 16 = zero cache slot.
    /// Verified identical between DW (`challengebanks/dlc_dwgh.vlt`) and Maloof
    /// (`challengebanks/dlc_mmcn.vlt`). Hardcoded so we don't have to recompute
    /// the per-DLC hashes the engine expects.
    public static byte[] FreeskateLocalDataVRef => (byte[])_freeskateLocalDataVRef.Clone();
    private static readonly byte[] _freeskateLocalDataVRef =
    {
        0x46, 0xA5, 0x6A, 0xDD, 0xF2, 0x84, 0xC3, 0x81,
        0x1D, 0x63, 0xB5, 0xB4, 0xCA, 0xD1, 0x2B, 0xF7,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// 32-byte tVaultedRefSpec → base-game freeskate state graph row. Same
    /// shape as the LocalData ref above.
    public static byte[] FreeskateStateGraphVRef => (byte[])_freeskateStateGraphVRef.Clone();
    private static readonly byte[] _freeskateStateGraphVRef =
    {
        0x3E, 0xAC, 0xD8, 0xF6, 0xF7, 0xB3, 0x1F, 0x63,
        0xF1, 0x7B, 0x65, 0x09, 0x25, 0x6E, 0xE5, 0x8B,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// 12-byte OnlineHUDPreloads array blob. Layout: 8-byte ArrayData header
    /// (count=1, capacity=1, typeSize=4, align=0) + 1 × UInt32 element
    /// `0x00000009`. Copied verbatim from retail DW `dlc_dwgh.bin` @ 0x16388.
    public static byte[] OnlineHUDPreloadsBlob => (byte[])_onlineHUDPreloadsBlob.Clone();
    private static readonly byte[] _onlineHUDPreloadsBlob =
    {
        0x00, 0x01, 0x00, 0x01, 0x00, 0x04, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x09,
    };

    /// 24-byte tRequiredChallengeHull empty-array blob. Header (count=0,
    /// capacity=0, typeSize=4, align=0) + 16 zero bytes (the type's inline
    /// default). Copied from retail DW `dlc_dwgh.bin` @ 0x16394.
    public static byte[] RequiredChallengeHullEmptyBlob => (byte[])_requiredChallengeHullEmptyBlob.Clone();
    private static readonly byte[] _requiredChallengeHullEmptyBlob =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// 24-byte empty `RefSpec[]` blob with three repeating header words.
    /// Used for `dlc_<framework>_own_the_spots.Objectives` matching DW's
    /// `dlc_dwgh_own_the_spots` shape (vlt_rows: 24 bytes hex at bin 0x167F0).
    public static byte[] EmptyObjectivesRefSpecArray => (byte[])_emptyObjectivesRefSpecArray.Clone();
    private static readonly byte[] _emptyObjectivesRefSpecArray =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00,
    };

    /// 32-byte tVaultedRefSpec for `challenge_local_data/<perAreaKey>` — used
    /// by row C's `LocalData` to bind the per-area row to the freeskate stub
    /// vlt. First 16 bytes = (Hash("challenge_local_data") || Hash(perAreaKey)),
    /// trailing 16 = zero cache slot.
    public static byte[] BuildLocalDataVRef(string perAreaKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(perAreaKey);
        return VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash("challenge_local_data"));
            w.WriteBE(Lookup8Hashing.Hash(perAreaKey));
            w.WriteBE(0UL);
            w.WriteBE(0UL);
        });
    }
}
