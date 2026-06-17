using System.Buffers.Binary;
using System.Text;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

/// <summary>
/// Composes the byte[] for a single Aipathdata (RWOBJECTTYPE_AIPATHDATA, 0x00EB0014)
/// PSG object. Layout is the recipe reverse-engineered from shipped DWMC DLC PSGs
/// (see DlcBuilder.Core/docs/AIPath_Node_Sk3_PhysOut_Offsets.md §7 and the corpus
/// dumps under AIPathRecorder/recordings).
///
/// Aipathdata blob layout (offsets relative to the blob start, big-endian).
///
/// <b>Pointer convention -- every <c>m_p*</c> field is a self-relative offset
/// from the start of its <i>containing</i> struct</b> (verified against three
/// shipped DIST_Industrial AIPath PSGs via <c>psg_structure_dumper.py</c>).
/// The engine's PSG arena loader patches each of these fields at load time to
/// <c>container_addr + on_disk_value</c>, then code reads them as absolute
/// pointers. Writing absolute blob-relative offsets makes the loader compute
/// <c>container_addr + blob_offset</c> -- an address past the end of the blob's
/// mapped memory -- and the engine crashes on first deref (we saw this in the
/// <c>sub_D9D1C</c> ExtData-copy path: src ptr = node_addr + on_disk_absolute_offset
/// landed in unmapped memory around 0x82504998).
///
///   +0x00  u32  m_uiNumOfPaths
///   +0x04  u32  m_pPaths = 0x10                       Aipathdata-relative -> first path header
///   +0x08  8 B  padding (0xDE in shipped files; we write 0x00 -- engine ignores)
///   +0x10  N × 96 B  tAIPath headers (back-to-back, no padding)
///   +X     path 0 nodes (44 B each, count = path0.NumNodes)
///   +Y     path 0 extdata (40 B each, count = nodes with ext != null)
///   +Z     path 1 nodes
///   ...
///
/// tAIPath header (96 B, all offsets relative to the header's own start):
///   +0x00  Vector4  bbox_min (min_x, min_y, min_z, 1.0)
///   +0x10  Vector4  bbox_max (max_x, max_y, max_z, 1.0)
///   +0x20  16 B     m_ID (AIPathID-style 16-byte identifier)
///   +0x30  u32      m_pNodes        HEADER-relative to first tAIPathNode
///   +0x34  u32      m_uiNumNodes
///   +0x38  u32      m_pExtData      HEADER-relative to first tAIPathNodeExtData blob
///                                   (0 if no node in this path carries ext data)
///   +0x3C  u32      m_pBranchGroup  HEADER-relative to first tAIPathBranchGroup
///                                   (0 -- engine recomputes via PathPreProcessor::FindIntersections at load)
///   +0x40  u32      m_uiNumGroups   0 -- same reason
///   +0x44  u32      m_BitFlags      default 7 (matches majority of shipped paths)
///   +0x48  u64      m_AllowedSkaters
///   +0x50  i32      m_SkillLevel
///   +0x54  i32      m_ExtraData1    zero
///   +0x58  i32      m_ExtraData2    zero
///   +0x5C  i32      m_ExtraData3    zero
///
/// tAIPathNode (44 B). Recorder writes the 44 bytes verbatim; we rewrite ONLY the
/// <c>m_pExtData</c> field at +0x20:
///   +0x00  Vec3    m_Position
///   +0x0C  Vec3    m_Direction
///   +0x18  4 B     m_BoardOrientation (compressed quaternion)
///   +0x1C  4 B     m_SkaterOrientation (compressed quaternion)
///   +0x20  u32     m_pExtData       NODE-relative offset to this node's ExtData blob
///                                   (0 if this node has no ext data)
///   +0x24  u8      m_uiFramesSinceLastNode
///   +0x25  u8      m_uiNodeWidthLeft
///   +0x26  u8      m_uiNodeWidthRight
///   +0x27  u8      m_uiEventType
///   +0x28  u8      m_i8Flags
///   +0x29  3 B     m_uiExtraData1..3
/// </summary>
public static class AiPathDataBuilder
{
    public const int NodeStride           = 44;
    public const int ExtStride            = 40;
    public const int PathHeaderStride     = 96;
    public const int AipathdataHeaderSize = 16;            // 8 B header + 8 B padding
    public const int NodeExtDataOffset    = 0x20;          // m_pExtData inside tAIPathNode
    public const uint DefaultBitFlags     = 7;             // matches shipped paths

    public sealed record PathSpec(
        ReadOnlyMemory<byte> Identifier16,
        IReadOnlyList<AiPathBinFile.Node> Nodes);

    /// <summary>
    /// Build one Aipathdata blob containing every path supplied. Returns the raw bytes
    /// ready to wrap in a <see cref="Psg.PsgObjectSpec"/>.
    /// </summary>
    public static byte[] Build(
        IReadOnlyList<PathSpec> paths,
        ulong allowedSkaters,
        int   skillLevel,
        bool  isLoop /* file-level; reserved for future use -- shipped tAIPath has no IsLoop field */,
        byte? widthClampLeft  = null,
        byte? widthClampRight = null)
    {
        if (paths == null || paths.Count == 0)
            throw new ArgumentException("at least one path is required", nameof(paths));
        _ = isLoop; // shipped tAIPath header has no is-loop field; metadata reserved for downstream tools

        // ── Layout pass: compute the final offset of every node/ext blob ──────
        int pathArrayStart  = AipathdataHeaderSize;
        int pathArrayBytes  = paths.Count * PathHeaderStride;
        int cursor          = pathArrayStart + pathArrayBytes;

        var nodeBlockStart    = new int[paths.Count];
        var extBlockStart     = new int[paths.Count]; // 0 if no node in path has ext
        var anyExtInPath      = new bool[paths.Count];
        // Per-node m_pExtData value, written verbatim into the node bytes at +0x20.
        // Encoded as NODE-RELATIVE (= ext_blob_blob_offset - node_blob_offset), per
        // stock convention. 0 means "no ext data for this node".
        var extOffsetPerNode = new int[paths.Count][];

        for (int p = 0; p < paths.Count; p++)
        {
            var spec = paths[p];
            nodeBlockStart[p] = cursor;
            cursor += spec.Nodes.Count * NodeStride;

            int firstExtCursor = cursor;
            extOffsetPerNode[p] = new int[spec.Nodes.Count];
            for (int n = 0; n < spec.Nodes.Count; n++)
            {
                int nodeAbsOffset = nodeBlockStart[p] + n * NodeStride;
                if (spec.Nodes[n].ExtBytes is null)
                {
                    extOffsetPerNode[p][n] = 0;
                }
                else
                {
                    // ext_blob lives at `cursor` (blob-absolute); the engine reads
                    // m_pExtData and computes `node_addr + m_pExtData` to get the
                    // ext-blob's address, so we store the delta.
                    extOffsetPerNode[p][n] = cursor - nodeAbsOffset;
                    if (!anyExtInPath[p])
                    {
                        extBlockStart[p] = cursor;
                        anyExtInPath[p]  = true;
                    }
                    cursor += ExtStride;
                }
            }
            if (!anyExtInPath[p])
                extBlockStart[p] = 0; // sentinel; not written to header
        }

        int totalSize = (cursor + 0x0F) & ~0x0F;             // 16-B align the blob tail
        var buf = new byte[totalSize];
        var s = buf.AsSpan();

        // ── Aipathdata header ────────────────────────────────────────────────
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), (uint)paths.Count);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), (uint)pathArrayStart);
        // +0x08..+0x0F: leave zero (shipped uses 0xDE debug-fill; engine ignores).

        // ── Per-path emission ────────────────────────────────────────────────
        for (int p = 0; p < paths.Count; p++)
        {
            var spec = paths[p];
            int header = pathArrayStart + p * PathHeaderStride;
            int nodes  = nodeBlockStart[p];

            // bbox over all node positions
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            for (int n = 0; n < spec.Nodes.Count; n++)
            {
                var (x, y, z) = AiPathBinFile.ReadPos(spec.Nodes[n]);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            // bbox_min Vector4
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x00, 4), minX);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x04, 4), minY);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x08, 4), minZ);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x0C, 4), 1.0f);
            // bbox_max Vector4
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x10, 4), maxX);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x14, 4), maxY);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x18, 4), maxZ);
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(header + 0x1C, 4), 1.0f);
            // m_ID (16 B, pass through caller's identifier, zero-pad)
            var idSpan = s.Slice(header + 0x20, 16);
            idSpan.Clear();
            var id = spec.Identifier16.Span;
            int idCopy = Math.Min(16, id.Length);
            if (idCopy > 0) id.Slice(0, idCopy).CopyTo(idSpan);
            // Pointers + counts. All m_p* values are HEADER-RELATIVE (subtract `header`,
            // the path header's blob-absolute offset, so the engine's loader can do
            // header_addr + on_disk_value = target_addr at load time).
            uint pNodesRel   = (uint)(nodes - header);
            uint pExtDataRel = anyExtInPath[p] ? (uint)(extBlockStart[p] - header) : 0u;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(header + 0x30, 4), pNodesRel);                // m_pNodes
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(header + 0x34, 4), (uint)spec.Nodes.Count);   // m_uiNumNodes
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(header + 0x38, 4), pExtDataRel);              // m_pExtData (header-relative)
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(header + 0x3C, 4), 0);                        // m_pBranchGroup (0 -- engine recomputes branches)
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(header + 0x40, 4), 0);                        // m_uiNumGroups
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(header + 0x44, 4), DefaultBitFlags);          // m_BitFlags
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(header + 0x48, 8), allowedSkaters);           // m_AllowedSkaters
            BinaryPrimitives.WriteInt32BigEndian (s.Slice(header + 0x50, 4), skillLevel);               // m_SkillLevel
            // +0x54..+0x5F m_ExtraData1/2/3 left zero.

            // Copy node bytes + patch m_pExtData inline. Optional width clamp:
            // when widthClamp{Left,Right} is non-null, override the recorder's
            // +0x25/+0x26 to tighten the AI follower's path-width tolerance.
            // m_pExtData (+0x20) is rewritten unconditionally because the recorder
            // .bin stores it as zero and the on-disk value is layout-dependent.
            for (int n = 0; n < spec.Nodes.Count; n++)
            {
                int nodeOff = nodes + n * NodeStride;
                spec.Nodes[n].NodeBytes.AsSpan(0, NodeStride).CopyTo(s.Slice(nodeOff, NodeStride));
                BinaryPrimitives.WriteUInt32BigEndian(
                    s.Slice(nodeOff + NodeExtDataOffset, 4),
                    (uint)extOffsetPerNode[p][n]);
                if (widthClampLeft  is byte wl) s[nodeOff + 0x25] = wl;
                if (widthClampRight is byte wr) s[nodeOff + 0x26] = wr;
            }
            // Copy ext-data bytes
            int extWalk = extBlockStart[p];
            for (int n = 0; n < spec.Nodes.Count; n++)
            {
                if (spec.Nodes[n].ExtBytes is { } ext)
                {
                    ext.AsSpan(0, ExtStride).CopyTo(s.Slice(extWalk, ExtStride));
                    extWalk += ExtStride;
                }
            }
        }

        return buf;
    }

    /// <summary>
    /// Build a 16-byte tAIPath identifier matching what stock <b>freeskate / ambient</b>
    /// AI paths carry. Decoded from Sk2 release-symboled binary (sk82_na_f.xex)
    /// <c>Sk8::AIPath::AIPathID::CompareAgainstFilter</c> @ 0x823EA678 and verified
    /// against a stock DIST_Industrial AIPath dump:
    /// <list type="bullet">
    ///   <item><c>m_ID[0]  = 0x00</c> — ambient-eligible marker. The filter takes the
    ///       "empty-ID" branch and accepts any path whose <c>m_ID[0]==0</c> as long as
    ///       <c>filter.mData[0]</c> is in <c>{7, 8}</c>. <see cref="Sk8::AIPath::PathManager"/>
    ///       constructs <c>mAmbientFilter</c> with <c>mAIPathID.mData[0]=8</c>, so any
    ///       path with <c>m_ID[0]=0</c> survives the freeskate AI traffic query.</item>
    ///   <item><c>m_ID[1..4]</c> = first 4 chars of the map name stem (stock: "indu" for
    ///       Industrial). Cosmetic only when <c>m_ID[0]==0</c>; the filter ignores them.</item>
    ///   <item><c>m_ID[5]    = 0x00</c></item>
    ///   <item><c>m_ID[6..13]</c> = an 8-byte content-derived blob (stock fills these with
    ///       what looks like a DIST hash). Filter ignores when <c>m_ID[0]==0</c>; we
    ///       reuse a stable hash so per-path IDs stay deterministic across builds.</item>
    ///   <item><c>m_ID[14..15]</c> = a 16-bit per-path index (unique within the file).</item>
    /// </list>
    /// Prior implementation wrote <c>m_ID[0]=0x01</c> which forced the filter into its
    /// non-empty branch and rejected the path on the byte 1/2/3/14/15 equality checks.
    /// </summary>
    public static byte[] DefaultIdentifier(string nameStem, int pathIndex)
    {
        var id = new byte[16];
        // [0] left as 0x00 — ambient/freeskate-eligible marker (see XML-doc above).
        var stem = Encoding.ASCII.GetBytes(nameStem ?? "");
        int copy = Math.Min(4, stem.Length);
        if (copy > 0) Array.Copy(stem, 0, id, 1, copy);
        // [5] left as 0x00.
        // [6..13]: stable per-recording 8-byte hash so two paths in the same recording
        // get different IDs even when stems and indices collide across recordings.
        // The filter ignores these bytes for ambient paths but the engine still uses
        // m_ID as the PathManager hash-map key, so non-zero entropy here keeps the
        // map from collapsing every path into one bucket.
        ulong hash = unchecked((ulong)((nameStem ?? "").GetHashCode()) * 0x9E3779B97F4A7C15ul);
        for (int k = 0; k < 8; k++) id[6 + k] = (byte)(hash >> (k * 8));
        // [14..15]: per-path index (big-endian u16, matches stock convention of the
        // last two bytes varying per path within a file).
        id[14] = (byte)((pathIndex >> 8) & 0xFF);
        id[15] = (byte)( pathIndex       & 0xFF);
        return id;
    }
}
