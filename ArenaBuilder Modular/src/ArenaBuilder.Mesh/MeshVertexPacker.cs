using System.Buffers.Binary;
using System.Numerics;

using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Packs vertex data from float arrays to PSG format.
/// Layout (stride 32): Position (float3) @0, TEX0 (float2) @12, TEX1/lm_norm (int16x4) @20, Tangent (dec3n) @28.
/// TEX0 uses FLOAT32 instead of half2 — half mantissa is 10 bits, so a UV value of 100 has
/// precision ~0.05 (visible warping). World-aligned terrain UVs routinely hit hundreds. FLOAT32
/// keeps UVs accurate across the whole map.
///
/// The TEX1 (int16x4) slot is NOT a plain UV1 — it is Skate's combined lightmap-UV + vertex-normal
/// pack ("lm_norm"), the only per-vertex normal the static-environment shader has (there is no
/// dedicated NORMAL element in stock static meshes). The static-environment VS decodes it as:
///   vNormal.xy = lm_norm.zw;                          // components [2],[3] = normal.x, normal.y
///   signs      = sign(lm_norm.xy);                    // sign of [0],[1]
///   vNormal.z  = signs.y * sqrt(1 - dot(vNormal.xy)); // [1] sign carries normal.z sign
///   vNormal   *= signs.x;                             // [0] sign carries overall flip
///   lightmapUV = abs(lm_norm.xy);                     // |[0]|,|[1]| = lightmap U,V
/// Verified against stock cPres meshes (decoded normal == geometric face normal, |dot| ≈ 0.999;
/// components [2],[3] never leave the unit circle). Writing only the UV ints and zeroing [2],[3]
/// makes the engine reconstruct a garbage normal → scattered/faceted lighting.
/// Matching descriptor lives in
/// <see cref="ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh.VertexDescriptorBuilder.BuildStaticMeshLayout"/>.
/// </summary>
public static class MeshVertexPacker
{
    public const int Stride = 32;

    /// <summary>
    /// Packs a single vertex. Positions can be scaled (e.g. 256.0 for game units).
    /// </summary>
    public static void PackVertex(
        Span<byte> output,
        in Vector3 position,
        in Vector2 uv0,
        in Vector2 lightmapUv,
        in Vector3 normal,
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

        // TEX0 format 0x02 = float2. Half2 lost precision on large UVs (world-aligned terrain).
        BinaryPrimitives.WriteSingleBigEndian(output.Slice(off, 4), uv0.X);
        off += 4;
        BinaryPrimitives.WriteSingleBigEndian(output.Slice(off, 4), uv0.Y);
        off += 4;

        // TEX1 (int16x4) = lm_norm: lightmap UV magnitude + packed vertex normal. See class remarks.
        PackLmNorm(output.Slice(off, 8), lightmapUv, normal);
        off += 8;

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
        ReadOnlySpan<Vector3> posSpan  = AsSpan(positions);
        ReadOnlySpan<Vector3> normSpan = AsSpan(normals);
        ReadOnlySpan<Vector2> uvSpan   = AsSpan(uvs);
        ReadOnlySpan<Vector2> uv1Span  = uvs1 != null ? AsSpan(uvs1) : default;

        var tangents = ComputeTangents(positions, normals, uvs, indices);

        int count = posSpan.Length;
        var outBuf = new byte[count * Stride];
        Span<byte> outSpan = outBuf;
        for (int i = 0; i < count; i++)
        {
            // TEX1 carries the lightmap UV (UV1 channel). When no UV1 was authored, fall back to UV0
            // so the magnitudes stay in a sane range; the sign bits/normal channels are filled below.
            Vector2 lightmapUv = (!uv1Span.IsEmpty && i < uv1Span.Length) ? uv1Span[i] : uvSpan[i];
            Vector3 normal = i < normSpan.Length ? normSpan[i] : Vector3.UnitY;
            // Never pack a zero tangent: engine can shade black (e.g. N·L with zero).
            var tangent = tangents[i];
            if (tangent.LengthSquared() < 1e-12f)
                tangent = Vector3.UnitX;
            PackVertex(
                outSpan.Slice(i * Stride, Stride),
                posSpan[i],
                uvSpan[i],
                lightmapUv,
                normal,
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

    /// <summary>
    /// Packs the lightmap UV + vertex normal into the 8-byte TEX1 "lm_norm" slot (4× signed int16, BE).
    /// Inverse of the static-environment VS decode (see class remarks):
    ///   [0] = +|U|·32767            (sign always + → decoded signs.x = +1, no overall flip)
    ///   [1] = sign(Nz)·|V|·32767    (sign carries normal.z's sign)
    ///   [2] = Nx·32767, [3] = Ny·32767   (normal.z reconstructed as signs.y·sqrt(1-Nx²-Ny²))
    /// </summary>
    private static void PackLmNorm(Span<byte> dst, Vector2 lightmapUv, Vector3 normal)
    {
        float nx = normal.X, ny = normal.Y, nz = normal.Z;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 1e-8f) { nx /= len; ny /= len; nz /= len; }
        else { nx = 0f; ny = 1f; nz = 0f; }

        // Lightmap UV magnitudes. Clamp to [0,1]; the sign bits are reserved for the normal.
        float lu = Math.Clamp(lightmapUv.X, 0f, 1f);
        float lv = Math.Clamp(lightmapUv.Y, 0f, 1f);

        short i0 = (short)MathF.Round(lu * 32767f);          // always ≥ 0 (signs.x = +1)
        short i1mag = (short)MathF.Round(lv * 32767f);
        // sign of [1] carries sign(Nz). Preserve it even when |V| rounds to 0.
        short i1 = nz < 0f ? (short)(i1mag == 0 ? -1 : -i1mag) : i1mag;
        short i2 = (short)MathF.Round(Math.Clamp(nx, -1f, 1f) * 32767f);
        short i3 = (short)MathF.Round(Math.Clamp(ny, -1f, 1f) * 32767f);

        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(0, 2), i0);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(2, 2), i1);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(4, 2), i2);
        BinaryPrimitives.WriteInt16BigEndian(dst.Slice(6, 2), i3);
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
