using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.Mesh;

/// <summary>
/// Xbox 360 VertexDescriptor object (RW type 0x000200E9) — variable-length form mirroring
/// the runtime <c>renderengine::VertexDescriptor</c> struct (sk82_na_zd.xex IDA-verified).
///
/// Layout: 16 B header + N × 16 B element records + N × 1 B per-stream strides.
///
/// Header (16 B):
///   +0x00  m_d3dVertexDeclaration (D3DVertexDeclaration*)  — NULL on disk; engine creates
///   +0x04  m_typesFlags (uint32)                           — bitmask of element.type values
///   +0x08  m_numElements (uint16)
///   +0x0A  m_refCount (int16)
///   +0x0C  m_instanceStreams (uint16)
///   +0x0E  m_pad0 (uint16)
///
/// Element (16 B each, starting at +0x10):
///   +0x00  stream      (uint16)
///   +0x02  offset      (uint16)  — byte offset of attribute within vertex
///   +0x04  format      (uint32)  — VertexFormat hash (see <see cref="XboxVertexFormat"/>)
///   +0x08  method      (uint8)
///   +0x09  usage       (uint8)   — D3DDECLUSAGE (see <see cref="XboxDeclUsage"/>)
///   +0x0A  usageIndex  (uint8)
///   +0x0B  type        (uint8)   — Pegasus element-type code (0=XYZ, 8=TEX0, 14=TANGENT, ...)
///   +0x0C  elementClass(uint32)
///
/// Engine behaviour at load (<c>VertexDescriptor::Initialize</c> @ 0x830cb460 — DECOMPILED):
///   - Zeros the 32 B header allocation.
///   - Iterates Parameters.elements[0..15], skipping slots with format == 0xFFFFFFFF.
///   - Recomputes m_typesFlags as OR(1 &lt;&lt; element.type) across all elements.
///   - Falls back to <c>elementTypeParamsTable</c> for usage/usageIndex when those bytes
///     are 0xFF (sentinel). Builder writes explicit values so this isn't triggered.
///   - Calls <c>CreateD3DObject</c> to build the D3DVertexDeclaration.
///
/// See docs/X360_Port_Deltas.md §4.
/// </summary>
public static class VertexDescriptorBuilder
{
    /// <summary>
    /// One vertex attribute. Mirrors <c>renderengine::VertexDescriptor::Element</c>.
    /// </summary>
    public readonly record struct Element(
        ushort Stream,
        ushort Offset,
        uint Format,
        byte Method,
        byte Usage,
        byte UsageIndex,
        byte Type,
        uint ElementClass = 1);

    /// <summary>
    /// Builds the variable-length VertexDescriptor for the given elements.
    /// <paramref name="strides"/> is one byte per used stream (typically a single entry).
    /// <paramref name="instanceStreams"/> is the bitmask of streams supplying per-instance
    /// data (usually 0 for static meshes; element method == 2 triggers it at load).
    /// </summary>
    public static byte[] Build(
        IReadOnlyList<Element> elements,
        IReadOnlyList<byte> strides,
        ushort instanceStreams = 0)
    {
        if (elements is null || elements.Count == 0)
            throw new ArgumentException("At least one element required.", nameof(elements));
        if (elements.Count > 16)
            throw new ArgumentException("VertexDescriptor::Parameters.elements is capped at 16.", nameof(elements));
        if (strides is null || strides.Count == 0)
            throw new ArgumentException("At least one stride byte required.", nameof(strides));

        // Layout: 16 B header + N×16 B elements + N stride bytes.
        // Stride bytes follow element array; engine reads strides[0..numElements) per
        // VertexDescriptor::Initialize loop @ 0x830cb678 (`r28 = params + 0x100`,
        // strides start at +0x100 of the Parameters struct).
        int size = 16 + elements.Count * 16 + strides.Count;
        var buf = new byte[size];
        var s = buf.AsSpan();

        // Header.
        // 0x00 m_d3dVertexDeclaration → NULL (engine creates at load).
        // 0x04 m_typesFlags → precompute OR(1 << element.type) for diff parity with stock.
        uint typesFlags = 0;
        foreach (var e in elements) typesFlags |= 1u << e.Type;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), typesFlags);

        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(0x08, 2), (ushort)elements.Count); // m_numElements
        BinaryPrimitives.WriteInt16BigEndian (s.Slice(0x0A, 2), 1);                       // m_refCount
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(0x0C, 2), instanceStreams);         // m_instanceStreams
        // 0x0E m_pad0 → 0 (zero-init).

        // Elements at +0x10.
        for (int i = 0; i < elements.Count; i++)
        {
            int off = 0x10 + i * 16;
            var e = elements[i];
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(off + 0x00, 2), e.Stream);
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(off + 0x02, 2), e.Offset);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 0x04, 4), e.Format);
            s[off + 0x08] = e.Method;
            s[off + 0x09] = e.Usage;
            s[off + 0x0A] = e.UsageIndex;
            s[off + 0x0B] = e.Type;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 0x0C, 4), e.ElementClass);
        }

        // Strides (1 byte per stream).
        int strideOff = 0x10 + elements.Count * 16;
        for (int i = 0; i < strides.Count; i++)
            s[strideOff + i] = strides[i];

        return buf;
    }

    /// <summary>
    /// TEX1 / lm_norm vertex-format hash. This is the i16×4 slot stock BlackBoxPark cPres meshes use
    /// (observed format 0x001A215A — NOT the plain SHORT4 0x001A22DA). <see cref="MeshVertexPacker"/>
    /// packs the lightmap-UV + normal exactly like stock, so we use stock's exact hash to decode it.
    /// </summary>
    private const uint Tex1LmNorm = 0x001A215Au;

    /// <summary>
    /// TEX0 vertex-format hash. MeshVertexPacker writes 2× float32 BE (8 bytes) at offset 12, so this
    /// MUST declare a 2-component FLOAT format = <see cref="XboxVertexFormat.FLOAT2"/> (0x002C2525).
    ///
    /// HISTORY: previously 0x001A2360. That hash is the engine's 4-COMPONENT class (0x001A… prefix —
    /// the same class as FLOAT4 / FLOAT16_4 / SHORT4), so the GPU read our 8-byte float2 as a half4
    /// and the UVs came out massively stretched (confirmed in-game 2026-06-13, once meshes finally
    /// rendered after the VB/IB fetch-address fix). The format-hash prefix encodes COMPONENT COUNT,
    /// not byte size: 0x002C…=≤2-comp, 0x002A…=3-comp (FLOAT3/position), 0x001A…=4-comp. So a 2× float32
    /// UV is the 2-comp float entry, 0x002C2525. The old comment claimed 0x002C2525 "makes the mesh
    /// invisible / is rejected" — that invisibility was the fetch-address bug, NOT this hash.
    /// </summary>
    private const uint Tex0Float2 = XboxVertexFormat.FLOAT2; // 0x002C2525 — 2× float32 (8 bytes)

    /// <summary>
    /// Builds the static-mesh VertexDescriptor for the layout <see cref="ArenaBuilder.Mesh.MeshVertexPacker"/>
    /// ACTUALLY produces — Position FLOAT3 @0, TEX0 FLOAT2 @12, TEX1/lm_norm i16×4 @20, TANGENT DEC3N @28,
    /// stride 32. (Stock uses a 28-byte layout with a quantized USHORT2 TEX0; our packer keeps TEX0 as
    /// full-precision FLOAT2 — same choice as the PS3 path — so the vertex is 32 B. The descriptor MUST
    /// describe our real data: if it claims stride 28 over 32-byte vertices the GPU walks every vertex at
    /// the wrong offset and the mesh renders as garbage / invisible.) Mirrors the working PS3
    /// VertexDescriptorBuilder.BuildStaticMeshLayout. Four stride bytes (one per stream slot) match stock.
    /// </summary>
    public static byte[] BuildStaticMeshLayout()
    {
        // Diffuse TEX0 is FLOAT3 (U,V,0) — full-precision float UVs. The engine maps the format hash
        // from the declaration (proven: changing the hash changes how the GPU reads the bytes), and
        // FLOAT3 (0x002A23B9) is a verified-recognized format (stock POSITION uses it). The shader reads
        // TEXCOORD0.xy = U,V. We pad to 3 floats because Xbox/Xenos has no 8-byte 2× float32 UV format
        // (stock UVs are all half2/half4); 12 bytes is the smallest VERIFIED float layout. MeshRX2Composer
        // re-packs the shared stride-32 (float2 TEX0) data to this stride-36 layout for X360 only.
        const byte stride = 36;
        var elements = new[]
        {
            new Element(0, 0,  XboxVertexFormat.FLOAT3,  0, XboxDeclUsage.POSITION, 0, 1),   // Position @0
            new Element(0, 12, XboxVertexFormat.FLOAT3,  0, XboxDeclUsage.TEXCOORD, 0, 6),   // TEX0 diffuse UV FLOAT3 (U,V,0) @12
            new Element(0, 24, Tex1LmNorm,               0, XboxDeclUsage.TEXCOORD, 1, 7),   // TEX1 lm_norm i16x4 @24
            new Element(0, 32, XboxVertexFormat.DEC3N_V, 0, XboxDeclUsage.TANGENT,  0, 21),  // Tangent dec3n @32
        };
        return Build(elements, new byte[] { stride, stride, stride, stride });
    }
}
