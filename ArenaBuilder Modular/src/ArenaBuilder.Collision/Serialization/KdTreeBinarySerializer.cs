using ArenaBuilder.Collision.KdTree;
using System.Buffers.Binary;
using System.Numerics;

namespace ArenaBuilder.Collision.Serialization;

/// <summary>
/// Runtime KD-tree blob inside ClusteredMesh. Layout matches <c>KDTreeBase</c> image + <c>BranchNode</c>
/// (kdtreebase.h): 48-byte header (<c>m_branchNodes</c> offset, <c>m_numBranchNodes</c>, <c>m_numEntries</c>, pad, <c>m_bbox</c>),
/// then <c>m_numBranchNodes</c> × 32 bytes (parent, axis, two <c>NodeRef</c>, two floats).
/// First DWORD is fixed up (+ mesh base) so it points at the branch array from the mesh allocation base.
/// </summary>
public static class KdTreeBinarySerializer
{
    /// <summary>Size of KD-tree preamble (branch pointer + counts + AABB).</summary>
    public const int HeaderSize = 48;

    /// <summary><c>sizeof(KDTreeBase::BranchNode)</c> — two <c>uint32_t</c> refs ×2 + two floats.</summary>
    public const int BranchNodeSize = 32;

    /// <param name="offsetOfKdBlobFromMeshBase">Offset from ClusteredMesh base to the start of this KD blob (e.g. 0x60). Required when numBranches &gt; 0 so first DWORD = offsetFromMeshBaseToBranchArray for Fixup.</param>
    public static byte[] Serialize(
        IReadOnlyList<KdTreeNode> kdTreeNodes,
        Vector3 bboxMin,
        Vector3 bboxMax,
        int numEntries,
        int offsetOfKdBlobFromMeshBase = 0)
    {
        int numBranches = kdTreeNodes.Count;
        int branchArraySize = numBranches * BranchNodeSize;
        int totalSize = numBranches == 0 ? HeaderSize : HeaderSize + branchArraySize;
        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        // First DWORD: Fixup does *v4 += ptr (mesh base). So stored value must be offset from mesh base to branch array.
        // Branch array starts at mesh_base + offsetOfKdBlobFromMeshBase + HeaderSize.
        uint branchOffset = numBranches == 0
            ? 0u
            : (uint)(offsetOfKdBlobFromMeshBase + HeaderSize);
        span = WriteBeU32(span, branchOffset);
        span = WriteBeU32(span, (uint)numBranches);
        span = WriteBeU32(span, (uint)numEntries);
        span = WriteBeU32(span, 0);
        span = WriteBeF32Vec3WithPad(span, bboxMin);
        span = WriteBeF32Vec3WithPad(span, bboxMax);

        for (int i = 0; i < numBranches; i++)
        {
            var node = kdTreeNodes[i];
            span = WriteBeU32(span, (uint)node.Parent);
            span = WriteBeU32(span, node.Axis);
            if (node.Entries.Length > 0)
            {
                span = WriteBeU32(span, node.Entries[0].Content);
                span = WriteBeU32(span, node.Entries[0].Index);
            }
            else
            {
                span = WriteBeU32(span, 0);
                span = WriteBeU32(span, 0);
            }
            if (node.Entries.Length > 1)
            {
                span = WriteBeU32(span, node.Entries[1].Content);
                span = WriteBeU32(span, node.Entries[1].Index);
            }
            else
            {
                span = WriteBeU32(span, 0);
                span = WriteBeU32(span, 0);
            }
            span = WriteBeF32(span, node.Ext0);
            span = WriteBeF32(span, node.Ext1);
        }

        return buffer;
    }

    private static Span<byte> WriteBeU32(Span<byte> s, uint v)
    {
        BinaryPrimitives.WriteUInt32BigEndian(s, v);
        return s[4..];
    }

    private static Span<byte> WriteBeF32(Span<byte> s, float v)
    {
        BinaryPrimitives.WriteInt32BigEndian(s, BitConverter.SingleToInt32Bits(v));
        return s[4..];
    }

    private static Span<byte> WriteBeF32Vec3WithPad(Span<byte> s, Vector3 v)
    {
        WriteBeF32(s, v.X);
        WriteBeF32(s[4..], v.Y);
        WriteBeF32(s[8..], v.Z);
        // +0x0C / +0x1C: padding uint32 = 0 (part of the 16-byte vec3 slot in file layout)
        WriteBeU32(s[12..], 0);
        return s[16..];
    }
}
