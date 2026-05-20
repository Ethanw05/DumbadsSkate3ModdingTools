using System.Buffers.Binary;
using System.Numerics;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Packs vertex data from float arrays to PSG format.
/// Real mesh layout (stride 28): Position (float3), TEX0 (half2), TEX1 (int16x2), 4-byte reserved gap, Tangent (dec3n).
/// Stride = 28 bytes.
/// </summary>
public static class MeshVertexPacker
{
    public const int Stride = 28;

    /// <summary>
    /// Packs a single vertex. Positions can be scaled (e.g. 256.0 for game units).
    /// </summary>
    public static void PackVertex(
        Span<byte> output,
        in Vector3 position,
        in Vector2 uv0,
        in Vector2 uv1,
        in Vector3 tangent,
        float scale = 1f)
    {
        int off = 0;
        BinaryPrimitives.WriteSingleBigEndian(output.Slice(off, 4), position.X * scale);
        off += 4;
        BinaryPrimitives.WriteSingleBigEndian(output.Slice(off, 4), position.Y * scale);
        off += 4;
        BinaryPrimitives.WriteSingleBigEndian(output.Slice(off, 4), position.Z * scale);
        off += 4;

        // TEX0 format 0x03 = half2.
        BinaryPrimitives.WriteHalfBigEndian(output.Slice(off, 2), (Half)uv0.X);
        off += 2;
        BinaryPrimitives.WriteHalfBigEndian(output.Slice(off, 2), (Half)uv0.Y);
        off += 2;

        // TEX1 format 0x01 in real meshes: signed normalized int16 pair.
        BinaryPrimitives.WriteInt16BigEndian(output.Slice(off, 2), ToSnorm16(uv1.X));
        off += 2;
        BinaryPrimitives.WriteInt16BigEndian(output.Slice(off, 2), ToSnorm16(uv1.Y));
        off += 2;

        // Reserved gap present in real layout (offset 20..23).
        output.Slice(off, 4).Clear();
        off += 4;

        BinaryPrimitives.WriteUInt32BigEndian(output.Slice(off, 4), PackDec3n(tangent));
    }

    /// <summary>
    /// Packs unique vertices. Index buffer references 0..positions.Count-1.
    /// </summary>
    /// <remarks>
    /// Materializes each <see cref="IReadOnlyList{T}"/> to a concrete array
    /// once, then reads via <see cref="ReadOnlySpan{T}"/> in the inner loop.
    /// IReadOnlyList&lt;T&gt; indexer access is a virtual call the JIT can't
    /// inline or hoist bounds checks for — measurable in this loop (~4
    /// indexer calls per vertex, up to 65k vertices per call). Span access
    /// is direct memory addressing.
    /// </remarks>
    public static byte[] PackVertices(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> uvs,
        IReadOnlyList<int> indices,
        float scale = 1f,
        IReadOnlyList<Vector2>? uvs1 = null)
    {
        ReadOnlySpan<Vector3> posSpan = AsSpan(positions);
        ReadOnlySpan<Vector2> uvSpan  = AsSpan(uvs);
        ReadOnlySpan<Vector2> uv1Span = uvs1 != null ? AsSpan(uvs1) : default;

        var tangents = ComputeTangents(positions, normals, uvs, indices);

        int count = posSpan.Length;
        var outBuf = new byte[count * Stride];
        Span<byte> outSpan = outBuf;
        for (int i = 0; i < count; i++)
        {
            Vector2 tex1 = (!uv1Span.IsEmpty && i < uv1Span.Length) ? uv1Span[i] : uvSpan[i];
            // Never pack a zero tangent: engine can shade black (e.g. N·L with zero).
            var tangent = tangents[i];
            if (tangent.LengthSquared() < 1e-12f)
                tangent = Vector3.UnitX;
            PackVertex(
                outSpan.Slice(i * Stride, Stride),
                posSpan[i],
                uvSpan[i],
                tex1,
                tangent,
                scale);
        }
        return outBuf;
    }

    /// <summary>
    /// Reinterpret an <see cref="IReadOnlyList{T}"/> as a <see cref="ReadOnlySpan{T}"/>
    /// when the underlying type is <c>T[]</c> or <c>List&lt;T&gt;</c> — those are
    /// the only two types the GLB flattener actually produces, so the fast
    /// path always wins. Falls back to <c>ToArray()</c> for anything else.
    /// </summary>
    private static ReadOnlySpan<T> AsSpan<T>(IReadOnlyList<T> list)
    {
        if (list is T[] arr) return arr;
        if (list is List<T> l) return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l);
        // Defensive: unknown impl — materialize once. Callers in this
        // codebase always pass an array or List<T>, so this branch is dead
        // for normal builds.
        var copy = new T[list.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
        return copy;
    }

    private static short ToSnorm16(float v)
    {
        float clamped = Math.Clamp(v, -1f, 1f);
        return (short)MathF.Round(clamped * 32767f);
    }

    /// <summary>Packs a normal/tangent as dec3n (11+11+10 bits). Matches glbtopsg: round then mask.</summary>
    private static uint PackDec3n(Vector3 n)
    {
        float nx = Math.Clamp(n.X, -1f, 1f);
        float ny = Math.Clamp(n.Y, -1f, 1f);
        float nz = Math.Clamp(n.Z, -1f, 1f);
        int ix = (int)MathF.Round(nx * 1023f) & 0x7FF;
        int iy = (int)MathF.Round(ny * 1023f) & 0x7FF;
        int iz = (int)MathF.Round(nz * 511f) & 0x3FF;
        return (uint)((iz << 22) | (iy << 11) | ix);
    }

    private static Vector3[] ComputeTangents(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> uvs,
        IReadOnlyList<int> indices)
    {
        // Same justification as PackVertices: materialize-once + span access
        // for the hot accumulation loop (3N indexer reads per triangle ×
        // N triangles — biggest single contributor to mesh PSG build time
        // on dense meshes).
        ReadOnlySpan<Vector3> pos = AsSpan(positions);
        ReadOnlySpan<Vector3> norm = AsSpan(normals);
        ReadOnlySpan<Vector2> uv  = AsSpan(uvs);
        ReadOnlySpan<int>    idx = AsSpan(indices);

        var tangents = new Vector3[pos.Length];
        for (int i = 0; i < idx.Length; i += 3)
        {
            int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
            var p0 = pos[i0]; var p1 = pos[i1]; var p2 = pos[i2];
            var uv0 = uv[i0]; var uv1 = uv[i1]; var uv2 = uv[i2];
            var edge1 = p1 - p0;
            var edge2 = p2 - p0;
            var deltaUV1 = uv1 - uv0;
            var deltaUV2 = uv2 - uv0;
            float f = deltaUV1.X * deltaUV2.Y - deltaUV2.X * deltaUV1.Y;
            if (Math.Abs(f) > 1e-6f)
            {
                var tangent = (edge1 * deltaUV2.Y - edge2 * deltaUV1.Y) / f;
                tangents[i0] += tangent;
                tangents[i1] += tangent;
                tangents[i2] += tangent;
            }
        }
        for (int i = 0; i < tangents.Length; i++)
        {
            var t = tangents[i];
            var n = norm[i];
            t -= n * Vector3.Dot(n, t);
            if (t.LengthSquared() > 1e-9f)
            {
                tangents[i] = Vector3.Normalize(t);
            }
            else
            {
                // Fallback: derive a stable tangent from the normal instead of using an arbitrary fixed axis.
                // This avoids pathological normal-map shading on triangles with zero UV area.
                var nNorm = n;
                if (nNorm.LengthSquared() < 1e-9f)
                    nNorm = Vector3.UnitY;

                Vector3 basis = MathF.Abs(Vector3.Dot(nNorm, Vector3.UnitY)) < 0.99f
                    ? Vector3.UnitY
                    : Vector3.UnitX;
                var fallbackTangent = Vector3.Cross(nNorm, basis);
                if (fallbackTangent.LengthSquared() > 1e-9f)
                    tangents[i] = Vector3.Normalize(fallbackTangent);
                else
                    tangents[i] = Vector3.UnitX;
            }
        }
        return tangents;
    }
}
