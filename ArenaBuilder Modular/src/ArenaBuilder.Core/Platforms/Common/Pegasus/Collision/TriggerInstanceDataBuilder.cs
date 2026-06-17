using System.Buffers.Binary;
using System.Text;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Collision;

/// <summary>
/// Builds the on-disk bytes of <c>pegasus::tTriggerInstanceData</c>
/// (RWOBJECTTYPE_TRIGGERINSTANCEDATA, type id <c>0x00EB0019</c>) — the PSG
/// object that registers a set of trigger volumes (boundaries / spot volumes /
/// scoring zones) with the engine when a mission's <c>cSim_Global.psf</c>
/// stream is loaded.
///
/// <para>
/// Layout reverse-engineered from sk82_na_f.xex IDA types
/// <c>pegasus::tTriggerInstanceData</c> + <c>pegasus::tTriggerInstance</c>
/// (verified against tTriggerInstanceData::Fixup / Unfix decompiled output and
/// against retail DW DLC OTS PSGs via psg_structure_dumper.py).
/// </para>
///
/// <para><b>Header (0x14 bytes):</b></para>
/// <list type="table">
///   <item><term>0x00 m_TypeID</term><description>uint32 — class hash (e.g. <c>0x46DB86E5</c> in OTS DW PSGs).</description></item>
///   <item><term>0x04 m_uiNumInstances</term><description>uint32 — count of <see cref="tTriggerInstance"/> records.</description></item>
///   <item><term>0x08 m_uiNumStrings</term><description>uint32 — count of NUL-terminated names in the string blob.</description></item>
///   <item><term>0x0C m_Instances</term><description>uint32 — byte offset (relative to the struct start) to the first <see cref="tTriggerInstance"/>.
///     Engine adds <c>ptr</c> at fixup time. Always <c>0x20</c> here (16-byte aligned past the 0x14 header).</description></item>
///   <item><term>0x10 m_StringList</term><description>uint32 — byte offset (relative to struct start) to the string pool. Engine adds <c>ptr</c> at fixup time.</description></item>
/// </list>
///
/// <para><b>Per-instance record (240 bytes, <c>pegasus::tTriggerInstance</c>):</b></para>
/// <list type="table">
///   <item><term>0x00 m_TransformMatrix</term><description>Matrix44Affine (64 B). Identity for axis-aligned ground volumes.</description></item>
///   <item><term>0x40 m_BBox</term><description>tAABB (Vec4 m_Min, Vec4 m_Max — w=0 each). World-space AABB.</description></item>
///   <item><term>0x60 m_BasePlaneNormal</term><description>Vector3 padded to 16 B. <c>(0, 1, 0, 0)</c> for ground-plane volumes (Y-up).</description></item>
///   <item><term>0x70 m_BasePlaneLeadingEdge[2]</term><description>2 × Vector3 padded — corners on the leading (max-X) edge of the bottom face: <c>(Xmax, Ymin, Zmax, 1)</c>, <c>(Xmax, Ymin, Zmin, 1)</c>.</description></item>
///   <item><term>0x90 m_BasePlaneTrailingEdge[2]</term><description>2 × Vector3 padded — corners on the trailing (min-X) edge of the bottom face: <c>(Xmin, Ymin, Zmax, 1)</c>, <c>(Xmin, Ymin, Zmin, 1)</c>.</description></item>
///   <item><term>0xB0 m_uiGuid</term><description>uint64 — Lookup8 of the volume's identifier string (or any unique 64-bit hash).</description></item>
///   <item><term>0xB8 m_uiGuidLocal</term><description>uint64 — engine-side resolution hash (e.g. <c>0x2c701706003d0bac</c> for <c>ots_dwmc_01_challengeboundary</c>).</description></item>
///   <item><term>0xC0 m_AttribKey</term><description>tAttribPair (uint64×2). Both fields <c>0xFFFFFFFFFFFFFFFF</c> = "no attrib key" (typical for OTS).</description></item>
///   <item><term>0xD0 m_TriggerData</term><description>uint64 — runtime data slot. Always 0 on disk.</description></item>
///   <item><term>0xD8 m_TriggerType</term><description>uint32 enum: 0=Challenge, 1=Stairs, 2=Camera, 3=Crowd.</description></item>
///   <item><term>0xDC m_pCModel</term><description>uint32 encoded dict ID — index of the paired
///     <see cref="ArenaBuilder.Core.Platforms.Common.Pegasus.Collision.CollisionModelDataBuilder">CollisionModelData</see>
///     in the same PSG arena dictionary. Engine resolves to a real pointer at fixup.</description></item>
///   <item><term>0xE0 m_Name</term><description>uint32 — byte offset (relative to struct start) into the string pool.</description></item>
///   <item><term>0xE4 m_PadBuffer[12]</term><description>Always zero on disk.</description></item>
/// </list>
///
/// <para><b>Bin layout this writer produces:</b></para>
/// <code>
/// [0x00..0x14)  header (m_TypeID, m_uiNumInstances, m_uiNumStrings, m_Instances=0x20, m_StringList=&lt;past instances&gt;)
/// [0x14..0x20)  zero pad to 16-byte align
/// [0x20..0x20+N*0xF0)  N × tTriggerInstance records
/// [...)         NUL-terminated name strings (one per instance, in m_Name order)
/// </code>
/// </summary>
public static class TriggerInstanceDataBuilder
{
    /// <summary>RW object type ID for tTriggerInstanceData (= <c>0x00EB0019</c>).</summary>
    public const uint TypeIdTriggerInstanceData = 0x00EB0019u;

    /// <summary>Class hash observed in every retail OTS DLC PSG (DW <c>cSim_Global.psf</c> trigger data).</summary>
    public const uint DefaultTypeId = 0x46DB86E5u;

    /// <summary>Size of one <c>pegasus::tTriggerInstance</c> on disk.</summary>
    public const int InstanceStride = 0xF0;

    /// <summary>Header size before the instance array.</summary>
    public const int HeaderSize = 0x14;

    /// <summary>The instance array always starts 16-byte aligned past the 0x14 header.</summary>
    public const int InstancesOffset = 0x20;

    /// <summary>
    /// <c>eTriggerInstanceType</c> enum values from sk82_na_f.xex.
    /// </summary>
    public enum TriggerType : uint
    {
        Challenge = 0,
        Stairs = 1,
        Camera = 2,
        Crowd = 3,
    }

    /// <summary>
    /// Single trigger volume specification. The polygon footprint itself lives
    /// in the paired <see cref="CollisionModelData"/> + <c>ClusteredMesh</c> +
    /// <c>Volume</c> objects elsewhere in the PSG; this struct is just the
    /// engine-facing "named entry" that points at that data via dictionary
    /// index.
    /// </summary>
    public sealed record InstanceSpec
    {
        /// <summary>World-space AABB minimum corner. Used for broad-phase culling.</summary>
        public required (float X, float Y, float Z) BBoxMin { get; init; }

        /// <summary>World-space AABB maximum corner.</summary>
        public required (float X, float Y, float Z) BBoxMax { get; init; }

        /// <summary>
        /// Stable per-volume identifier hash. Convention: Lookup8 of the volume's
        /// canonical name (e.g. <c>"ots_dwmc_01_challengeboundary"</c>).
        /// </summary>
        public required ulong Guid { get; init; }

        /// <summary>
        /// Engine-side resolution hash. In retail content this is the lookup8
        /// fragment baked into world-painter content (e.g.
        /// <c>0x2c701706003d0bac</c>); for builder-authored content, set to a
        /// stable per-volume hash unique to this DLC.
        /// </summary>
        public required ulong GuidLocal { get; init; }

        /// <summary>
        /// Display / lookup name written into the string pool. Retail uses the
        /// verbose <c>&lt;World&gt;|&lt;volume_basename&gt;|0x&lt;lookup8&gt;</c>
        /// path-style string (the same string referenced by the per-instance
        /// challenge_local_data row's <c>ChallengeBoundary</c> /
        /// <c>OTSScoringBoundary</c> attribute in the bin pool).
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Dictionary index of the paired <c>pegasus::tCollisionModelData</c>
        /// object in the same PSG arena. The engine resolves this to a real
        /// pointer via <c>Arena::IdToObject</c> during fixup.
        /// </summary>
        public required uint CollisionModelDictIndex { get; init; }

        /// <summary>Trigger-volume type. Most challenge volumes use <see cref="TriggerType.Challenge"/>.</summary>
        public TriggerType Type { get; init; } = TriggerType.Challenge;

        /// <summary>
        /// Row-major 4×4 transform matrix (16 floats). Written into
        /// m_TransformMatrix. Defaults to identity. For rotated trigger
        /// volumes (e.g. race gates) set this to the yaw rotation matrix
        /// so the engine orients visual indicators correctly.
        /// </summary>
        public float[]? TransformMatrix { get; init; }

        /// <summary>
        /// m_BasePlaneLeadingEdge — two world-space corners on the local +X
        /// edge of the bottom face: [0]=(+X,Ymin,+Z), [1]=(+X,Ymin,−Z).
        /// When null, falls back to AABB corners (correct for axis-aligned).
        /// </summary>
        public (float X, float Y, float Z)[]? LeadingEdge { get; init; }

        /// <summary>
        /// m_BasePlaneTrailingEdge — two world-space corners on the local −X
        /// edge: [0]=(−X,Ymin,+Z), [1]=(−X,Ymin,−Z).
        /// When null, falls back to AABB corners.
        /// </summary>
        public (float X, float Y, float Z)[]? TrailingEdge { get; init; }

        /// <summary>
        /// Optional 16-byte attribute key pair. The tAttribPair (16 B = 2 × uint64)
        /// is filled with <c>0xFFFFFFFF</c> by default — that's the "no attrib key"
        /// sentinel observed in retail OTS volumes.
        /// </summary>
        public ulong AttribKeyHigh { get; init; } = 0xFFFFFFFFFFFFFFFFul;
        public ulong AttribKeyLow { get; init; } = 0xFFFFFFFFFFFFFFFFul;
    }

    /// <summary>
    /// Build the full <c>pegasus::tTriggerInstanceData</c> payload (0x14 header
    /// + N × 240-byte instances + NUL-terminated string pool).
    /// </summary>
    /// <param name="instances">Per-volume specifications. Order must match the
    /// trigger volume reference order in the consumer (e.g. the
    /// <c>challenge_local_data</c> row's <c>ChallengeBoundary</c> /
    /// <c>OTSScoringBoundary</c> / <c>SpotVolumes</c> attribute names; the
    /// engine looks volumes up by string match against the names written here).</param>
    /// <param name="typeId">Class hash. Defaults to <see cref="DefaultTypeId"/>
    /// (matches retail OTS PSGs).</param>
    public static byte[] Build(IReadOnlyList<InstanceSpec> instances, uint typeId = DefaultTypeId)
    {
        if (instances == null) throw new ArgumentNullException(nameof(instances));
        if (instances.Count == 0)
            throw new ArgumentException("At least one tTriggerInstance is required.", nameof(instances));

        int instanceArrayBytes = instances.Count * InstanceStride;
        int stringPoolStart = InstancesOffset + instanceArrayBytes;

        // Build string pool first so we know the m_Name offsets per instance.
        var nameBlob = new MemoryStream();
        var nameOffsets = new uint[instances.Count];
        for (int i = 0; i < instances.Count; i++)
        {
            nameOffsets[i] = (uint)(stringPoolStart + nameBlob.Length);
            byte[] ascii = Encoding.ASCII.GetBytes(instances[i].Name);
            nameBlob.Write(ascii, 0, ascii.Length);
            nameBlob.WriteByte(0);
        }

        int totalSize = stringPoolStart + (int)nameBlob.Length;
        var buf = new byte[totalSize];
        var span = buf.AsSpan();

        // ─── Header (0x14 bytes) ───────────────────────────────────────────────
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x00, 4), typeId);                         // m_TypeID
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x04, 4), (uint)instances.Count);          // m_uiNumInstances
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x08, 4), (uint)instances.Count);          // m_uiNumStrings (1 string per instance)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x0C, 4), (uint)InstancesOffset);          // m_Instances (relative offset)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x10, 4), (uint)stringPoolStart);          // m_StringList (relative offset)
        // 0x14..0x1F is zero-padded by default `new byte[]` — no fields here.

        // ─── Instance records ──────────────────────────────────────────────────
        for (int i = 0; i < instances.Count; i++)
        {
            int recOff = InstancesOffset + i * InstanceStride;
            WriteInstance(span.Slice(recOff, InstanceStride), instances[i], nameOffsets[i]);
        }

        // ─── String pool ───────────────────────────────────────────────────────
        nameBlob.GetBuffer().AsSpan(0, (int)nameBlob.Length).CopyTo(span.Slice(stringPoolStart));

        return buf;
    }

    private static void WriteInstance(Span<byte> dst, InstanceSpec spec, uint nameOffset)
    {
        // 0x00..0x40 — m_TransformMatrix (Matrix44Affine).
        // Identity for axis-aligned volumes; yaw rotation for oriented gates.
        if (spec.TransformMatrix is { Length: 16 } tm)
        {
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteSingleBigEndian(dst.Slice(i * 4, 4), tm[i]);
        }
        else
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x00 + r * 16 + c * 4, 4),
                        r == c ? 1f : 0f);
        }

        // 0x40 — m_BBox.m_Min (Vec4, w=0)
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x40, 4), spec.BBoxMin.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x44, 4), spec.BBoxMin.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x48, 4), spec.BBoxMin.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x4C, 4), 0f);

        // 0x50 — m_BBox.m_Max (Vec4, w=0)
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x50, 4), spec.BBoxMax.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x54, 4), spec.BBoxMax.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x58, 4), spec.BBoxMax.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x5C, 4), 0f);

        // 0x60 — m_BasePlaneNormal (0, 1, 0, w=0). Y-up ground plane.
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x60, 4), 0f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x64, 4), 1f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x68, 4), 0f);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x6C, 4), 0f);

        // 0x70 — m_BasePlaneLeadingEdge[0] (local +X, +Z corner at bottom)
        var le0 = spec.LeadingEdge is { Length: >= 1 } le ? le[0] : (spec.BBoxMax.X, spec.BBoxMin.Y, spec.BBoxMax.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x70, 4), le0.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x74, 4), le0.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x78, 4), le0.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x7C, 4), 1f);

        // 0x80 — m_BasePlaneLeadingEdge[1] (local +X, −Z corner at bottom)
        var le1 = spec.LeadingEdge is { Length: >= 2 } le2 ? le2[1] : (spec.BBoxMax.X, spec.BBoxMin.Y, spec.BBoxMin.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x80, 4), le1.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x84, 4), le1.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x88, 4), le1.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x8C, 4), 1f);

        // 0x90 — m_BasePlaneTrailingEdge[0] (local −X, +Z corner at bottom)
        var te0 = spec.TrailingEdge is { Length: >= 1 } te ? te[0] : (spec.BBoxMin.X, spec.BBoxMin.Y, spec.BBoxMax.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x90, 4), te0.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x94, 4), te0.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x98, 4), te0.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0x9C, 4), 1f);

        // 0xA0 — m_BasePlaneTrailingEdge[1] (local −X, −Z corner at bottom)
        var te1 = spec.TrailingEdge is { Length: >= 2 } te2 ? te2[1] : (spec.BBoxMin.X, spec.BBoxMin.Y, spec.BBoxMin.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0xA0, 4), te1.X);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0xA4, 4), te1.Y);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0xA8, 4), te1.Z);
        BinaryPrimitives.WriteSingleBigEndian(dst.Slice(0xAC, 4), 1f);

        // 0xB0 — m_uiGuid (BE uint64)
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(0xB0, 8), spec.Guid);

        // 0xB8 — m_uiGuidLocal (BE uint64)
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(0xB8, 8), spec.GuidLocal);

        // 0xC0 — m_AttribKey (tAttribPair = 2 × uint64 BE)
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(0xC0, 8), spec.AttribKeyHigh);
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(0xC8, 8), spec.AttribKeyLow);

        // 0xD0 — m_TriggerData (uint64 = 0 on disk; runtime slot)
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(0xD0, 8), 0u);

        // 0xD8 — m_TriggerType (eTriggerInstanceType uint32)
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0xD8, 4), (uint)spec.Type);

        // 0xDC — m_pCModel (encoded dict ID — engine resolves via Arena::IdToObject)
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0xDC, 4), spec.CollisionModelDictIndex);

        // 0xE0 — m_Name (relative offset into the string pool, fixed up at load)
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0xE0, 4), nameOffset);

        // 0xE4..0xF0 — m_PadBuffer[12] = zero (already initialised by `new byte[]`).
    }
}
