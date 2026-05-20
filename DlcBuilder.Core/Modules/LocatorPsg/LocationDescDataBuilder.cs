using System.Buffers.Binary;
using System.Text;
using DlcBuilder.Builders;

namespace DlcBuilder.Modules.LocatorPsg;

/// Builds the byte payload of a `pegasus::tLocationDescData` (RW type 0x00EB0009)
/// containing one `tLocationDesc` + N `tSubLocationDesc` + a string blob.
///
/// Layout (header is 24 bytes; first descriptor placed at offset 0x20 to match
/// stock Skate 3 DLC layout, leaving 8 zero pad bytes):
///   0x00  tLocationDescData header (24 bytes)
///   0x18  pad (8 bytes)
///   0x20  tLocationDesc[0]              (128 bytes; m_iNumSubLocations = N)
///   0xA0  tSubLocationDesc[0..N-1]      (112 bytes each)
///   ...   string blob (cstrings, contiguous)
///
/// All multi-byte fields big-endian. Floats IEEE754 BE.
///
/// String offsets stored in `m_Name` / `m_Description` are RELATIVE TO THE
/// START OF THE LocationdescData payload (i.e. relative to the header start).
/// `m_uiNumStrings` is intentionally written as 0 to match stock files (the
/// strings are still emitted; runtime resolves them via the offset fields, not
/// via count).
public static class LocationDescDataBuilder
{
    public const int HeaderSize = 24;
    /// Byte offset (within the payload) of the first `tLocationDesc`. The
    /// LocatorPsgBuilder uses this to point its lone subref record at the
    /// descriptor.
    public const int FirstDescOffset = 0x20;
    public const int LocationDescSize = 128;        // 0x80
    public const int SubLocationDescSize = 112;     // 0x70

    /// <param name="Name">Sub-locator name (<c>{key}_chev_*</c>, <c>{key}_vis_*</c>, …).</param>
    /// <param name="Transform">World transform (Y-up, metres).</param>
    /// <param name="RibbonIndicatorCollectionKey">
    /// Optional Lookup8 string for <c>ribbon_indicator</c> <c>mCollectionKey</c> on this sub's
    /// <c>challenge_local_data.VisualIndicators</c> row when that sub is listed there
    /// (<see langword="null"/> = <c>OtsLocalDataVltBuilder</c> defaults: <c>_chev_*</c> → secondary key hash;
    /// <c>_vis_2..</c> → <c>arrow</c>). <c>{key}_vis_1</c> is signup-only and is not emitted in that array.
    /// </param>
    /// <param name="OmitFromChallengeLocalVisualIndicators">
    /// When <see langword="true"/>, <c>OtsLocalDataVltBuilder</c> skips this sub for
    /// <c>challenge_local_data.VisualIndicators</c> while still emitting it in the mission
    /// PSG / <c>_Sim.loc</c> (structural parity with retail ordering / TOC identity).
    /// </param>
    public sealed record SubLocSpec(
        string Name,
        Transform44 Transform,
        string? RibbonIndicatorCollectionKey = null,
        bool OmitFromChallengeLocalVisualIndicators = false);

    public sealed record LocSpec(
        string Name,
        string Description,
        Transform44 Transform,
        ulong Guid,
        IReadOnlyList<SubLocSpec> SubLocations);

    /// Build the LocationDescData payload bytes for a single locator with N
    /// sublocations. Returned bytes are ready to embed as an RW object body.
    public static byte[] Build(LocSpec loc) => BuildMultiple(new[] { loc });

    /// Build a LocationDescData payload that exposes N TOP-LEVEL tLocationDesc
    /// entries. Each LocSpec becomes its own tLocationDesc; its SubLocations
    /// (if any) are appended into the flat tSubLocationDesc array and linked
    /// from that locator's `m_SubLocations` offset.
    ///
    /// **WHY THIS MATTERS** (verified against retail DW
    /// content/missions/ots_dwmc_01/cSim_Global/5822CECF4EF38F6C.psg, which
    /// declares numLocs=6, numSub=12, descs=0x20, subDescs=0x320,
    /// strings=0x860 and string-blob names: ots_dwmc_01_chev_1, _chev_2,
    /// _chev_3, _vis_1, _startlocator (+6 spawn subs), _waitlocator (+6 wait
    /// subs)):
    ///
    ///   `cLocationManager::RegArena` (Skate 2 sub_8287c5b0; same shape in
    ///   Skate 3) iterates every asset of type 21 (LOCATIONDESCDATA) in the
    ///   freshly streamed cArena, and for each it walks the **top-level**
    ///   `m_LocationDescs` array — registering each entry's name into
    ///   `LocationMapping` (rbtree keyed by GetHashValue32(name)).
    ///   Sub-locations are NOT registered. They're only reachable as `[index]`
    ///   from a parent that's already in the map.
    ///
    ///   So every name the engine resolves through a `tLocationID` field
    ///   (e.g. challenge_local_data.StartLocation = `<key>_startlocator`)
    ///   MUST be a top-level entry. If you ship `_startlocator` as a
    ///   sub-locator, `cLocationManager::GetSpawnLocation(name, 0)` falls
    ///   into the "not found" branch (sub_8287c2c0) and writes an identity
    ///   matrix → player teleported to world origin facing yaw 0.
    public static byte[] BuildMultiple(IReadOnlyList<LocSpec> locators)
    {
        ArgumentNullException.ThrowIfNull(locators);
        if (locators.Count == 0)
            throw new ArgumentException("Need at least one locator.", nameof(locators));

        int numLocs = locators.Count;
        int totalSub = 0;
        foreach (var l in locators)
        {
            ArgumentNullException.ThrowIfNull(l);
            ArgumentNullException.ThrowIfNull(l.SubLocations);
            totalSub += l.SubLocations.Count;
        }

        int descsStart = FirstDescOffset;
        int subDescsStart = descsStart + numLocs * LocationDescSize;
        int stringBlobStart = subDescsStart + totalSub * SubLocationDescSize;

        var blob = new List<byte>();
        var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);

        uint AddString(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0u;
            if (stringOffsets.TryGetValue(s, out var existing)) return existing;
            uint o = (uint)(stringBlobStart + blob.Count);
            byte[] ascii = Encoding.ASCII.GetBytes(s);
            blob.AddRange(ascii);
            blob.Add(0);
            stringOffsets[s] = o;
            return o;
        }

        // Pre-resolve every string offset in the order DW emits them: trigger
        // names already live earlier in the cSim_Global PSG; here we only
        // own the locator-side block. Top-level names first (in order),
        // then per-locator sub-names in declared order.
        var locNameOffs = new uint[numLocs];
        var locDescOffs = new uint[numLocs];
        var subNameOffs = new uint[totalSub];

        // Sub-blocks live in the flat tSubLocationDesc array; walk locators
        // in order and assign each a contiguous run.
        var locSubStart = new int[numLocs];
        int subCursor = 0;
        for (int i = 0; i < numLocs; i++)
        {
            locSubStart[i] = subCursor;
            subCursor += locators[i].SubLocations.Count;
        }

        for (int i = 0; i < numLocs; i++)
        {
            locNameOffs[i] = AddString(locators[i].Name);
            locDescOffs[i] = AddString(locators[i].Description);
            int run = locators[i].SubLocations.Count;
            for (int j = 0; j < run; j++)
                subNameOffs[locSubStart[i] + j] = AddString(locators[i].SubLocations[j].Name);
        }

        int total = stringBlobStart + blob.Count;
        var bytes = new byte[total];
        Span<byte> span = bytes;

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x00, 4), (uint)numLocs);                  // m_uiNumLocationDescs
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x04, 4), (uint)totalSub);                 // m_uiNumSubLocationDescs
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x08, 4), 0u);                             // m_uiNumStrings (stock writes 0)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x0C, 4), (uint)descsStart);               // m_LocationDescs
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x10, 4), (uint)subDescsStart);            // m_SubLocationDescs
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x14, 4), (uint)stringBlobStart);          // m_StringList
        // 0x18..0x1F: 8 byte pad zero.

        for (int i = 0; i < numLocs; i++)
        {
            int off = descsStart + i * LocationDescSize;
            var loc = locators[i];
            int subStartByte = subDescsStart + locSubStart[i] * SubLocationDescSize;

            WriteTransform(span, off + 0x00, loc.Transform);
            WriteAabbForPoint(span, off + 0x40, loc.Transform.Tx, loc.Transform.Ty, loc.Transform.Tz);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x60, 4), (uint)loc.SubLocations.Count);  // m_iNumSubLocations
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x64, 4), (uint)subStartByte);             // m_SubLocations
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x68, 4), locNameOffs[i]);                 // m_Name
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x6C, 4), locDescOffs[i]);                 // m_Description
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(off + 0x70, 8), loc.Guid);                       // m_uiGuid
            // 0x78..0x7F: zero.
        }

        for (int i = 0; i < numLocs; i++)
        {
            var loc = locators[i];
            for (int j = 0; j < loc.SubLocations.Count; j++)
            {
                int off = subDescsStart + (locSubStart[i] + j) * SubLocationDescSize;
                var sub = loc.SubLocations[j];
                WriteTransform(span, off + 0x00, sub.Transform);
                WriteAabbForPoint(span, off + 0x40, sub.Transform.Tx, sub.Transform.Ty, sub.Transform.Tz);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x60, 4), subNameOffs[locSubStart[i] + j]);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x64, 4), 0u);
                // 0x68..0x6F: zero.
            }
        }

        if (blob.Count > 0)
            blob.CopyTo(bytes, stringBlobStart);

        return bytes;
    }

    private static void WriteTransform(Span<byte> dst, int offset, Transform44 t)
    {
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + i * 4, 4), t.Rows[i]);
    }

    /// Emits a `pegasus::tAABB` covering a 1m cube centered at (x,y,z):
    /// m_Min = pos - 1, m_Max = pos + 1. Vector4 m_Min then Vector4 m_Max,
    /// each 4 BE floats; w = 0.
    private static void WriteAabbForPoint(Span<byte> dst, int offset, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x00, 4), x - 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x04, 4), y - 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x08, 4), z - 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x0C, 4), 0f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x10, 4), x + 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x14, 4), y + 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x18, 4), z + 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(offset + 0x1C, 4), 0f);
    }
}
