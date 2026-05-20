using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Packs triangle indices to uint16 big-endian. Per glbtopsg: make_face_bin.
/// reverseWinding: when true, each triangle (i0,i1,i2) is written as (i0,i2,i1) to flip front face (fixes invisible mesh if game culls back faces and our winding is opposite).
/// </summary>
public static class MeshIndexPacker
{
    public static byte[] PackIndices(IReadOnlyList<int> indices, bool reverseWinding = false)
    {
        if (indices == null)
            throw new ArgumentNullException(nameof(indices));
        if (reverseWinding && (indices.Count % 3 != 0))
            throw new InvalidOperationException("Index buffer is not triangle-aligned (count must be multiple of 3).");

        // Materialize once into a span — the JIT can't inline IReadOnlyList<int>'s
        // indexer or hoist bounds checks across it, so the tight write loops
        // below pay one virtual call per index without this. Callers in this
        // codebase always pass int[] or List<int>, both of which take the
        // fast path.
        ReadOnlySpan<int> idxSpan = indices switch
        {
            int[] arr => arr,
            List<int> list => CollectionsMarshal.AsSpan(list),
            _ => ToArrayOnce(indices),
        };

        for (int i = 0; i < idxSpan.Length; i++)
        {
            int idx = idxSpan[i];
            if ((uint)idx > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Mesh index {idx} at position {i} exceeds 16-bit limit (65535). " +
                    "Split the mesh into smaller parts before PSG build.");
            }
        }

        var buf = new byte[idxSpan.Length * 2];
        Span<byte> outSpan = buf;
        if (!reverseWinding)
        {
            for (int i = 0; i < idxSpan.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(outSpan.Slice(i * 2, 2), (ushort)idxSpan[i]);
        }
        else
        {
            for (int i = 0; i < idxSpan.Length; i += 3)
            {
                int i0 = idxSpan[i], i1 = idxSpan[i + 1], i2 = idxSpan[i + 2];
                BinaryPrimitives.WriteUInt16BigEndian(outSpan.Slice((i + 0) * 2, 2), (ushort)i0);
                BinaryPrimitives.WriteUInt16BigEndian(outSpan.Slice((i + 1) * 2, 2), (ushort)i2);
                BinaryPrimitives.WriteUInt16BigEndian(outSpan.Slice((i + 2) * 2, 2), (ushort)i1);
            }
        }
        return buf;
    }

    private static int[] ToArrayOnce(IReadOnlyList<int> indices)
    {
        var copy = new int[indices.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = indices[i];
        return copy;
    }
}
