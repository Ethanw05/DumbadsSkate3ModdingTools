namespace ArenaBuilder.Core.Platforms.Xbox;

/// <summary>
/// Xbox 360 <c>renderengine::VertexFormat</c> enum (hash codes).
///
/// Values are 32-bit hashes embedded in <c>VertexDescriptor::Element.format</c> at +0x04 of
/// each 16-byte Element record. Resolved at load time by <c>VertexFormatGetStride</c>
/// (sk82_na_zd.xex @ 0x830c81a4) — switch over hash → byte stride for the Xenos GPU
/// vertex fetch unit.
///
/// All values cross-verified against:
///   - GLBtoRX2-v1.0.py reference implementation (SunJay/Dumbad/RenderWareGavin/Tuukkas)
///   - Disassembled comparator constants in VertexFormatGetStride
///   - 4-element stock VertexDescriptor sample (DIST_BlackBoxPark cPres_-50_-50_high)
///
/// See docs/X360_Port_Deltas.md §4 for the full enumeration.
/// </summary>
public static class XboxVertexFormat
{
    // ─── Float formats ────────────────────────────────────────────────────────
    public const uint FLOAT1     = 0x002C84E4; // stride 4
    // FLOAT2 = 2× float32, stride 8. The 0x002C… prefix is the ≤2-component class (same as FLOAT1 /
    // SHORT2 / FLOAT16_2); component COUNT not byte size. No stock Skate 3 mesh uses it (stock UVs are
    // half4 0x001A2360), but it IS the correct 2-comp float hash and is used for TEX0 by
    // VertexDescriptorBuilder.Tex0Float2 — full-precision UVs by design. The earlier "rejected /
    // invisible" note was a misdiagnosis of the VB/IB fetch-address bug (fixed 2026-06-13).
    public const uint FLOAT2     = 0x002C2525; // stride 8 — 2× float32 (used for TEX0)
    public const uint FLOAT3     = 0x002A23B9; // stride 12 (Position observed in stock)
    public const uint FLOAT4     = 0x001A2326; // stride 16

    // ─── Half-precision float ─────────────────────────────────────────────────
    public const uint FLOAT16_2  = 0x002C24DF; // stride 4
    public const uint FLOAT16_4  = 0x001A22E0; // stride 8

    // ─── Signed short ─────────────────────────────────────────────────────────
    public const uint SHORT2     = 0x002C24D9; // stride 4
    public const uint SHORT4     = 0x001A22DA; // stride 8
    public const uint SHORT2N    = 0x002C22D9; // stride 4
    public const uint SHORT4N    = 0x001A20DA; // stride 8 (XYZ uses this — 4 shorts incl. w=0)

    // ─── Unsigned short ───────────────────────────────────────────────────────
    public const uint USHORT2N   = 0x002C21D9; // stride 4
    public const uint USHORT4N   = 0x001A1FDA; // stride 8

    // ─── Bytes ────────────────────────────────────────────────────────────────
    public const uint D3DCOLOR   = 0x00182106; // stride 4
    public const uint UBYTE4     = 0x001A2206; // stride 4 (bone indices — raw bytes)
    public const uint UBYTE4N    = 0x001A2006; // stride 4 (weights — normalized)
    public const uint BYTE4N     = 0x001A2106; // stride 4

    // ─── Packed 3-component ───────────────────────────────────────────────────
    public const uint UDEC3      = 0x002A2287; // stride 4
    public const uint DEC3N      = 0x002A2187; // stride 4
    public const uint DEC3N_V    = 0x002A2190; // stride 4 (variant observed in stock TANGENT slot)

    /// <summary>Returns the byte stride for a given VertexFormat hash. Mirrors
    /// <c>renderengine::VertexFormatGetStride</c> for the formats above.</summary>
    public static int GetStride(uint vertexFormatHash) => vertexFormatHash switch
    {
        FLOAT1 or FLOAT16_2 or SHORT2 or SHORT2N or USHORT2N or
        D3DCOLOR or UBYTE4 or UBYTE4N or BYTE4N or
        UDEC3 or DEC3N or DEC3N_V => 4,

        FLOAT2 or FLOAT16_4 or SHORT4 or SHORT4N or USHORT4N => 8,

        FLOAT3 => 12,

        FLOAT4 => 16,

        _ => throw new ArgumentException($"Unknown VertexFormat hash 0x{vertexFormatHash:X8}", nameof(vertexFormatHash))
    };
}

/// <summary>
/// Xbox 360 <c>D3DDECLUSAGE</c> enum — value stored in
/// <c>VertexDescriptor::Element.usage</c> byte at element +0x09.
/// </summary>
public static class XboxDeclUsage
{
    public const byte POSITION     = 0;
    public const byte BLENDWEIGHT  = 1;
    public const byte BLENDINDICES = 2;
    public const byte NORMAL       = 3;
    public const byte PSIZE        = 4;
    public const byte TEXCOORD     = 5;
    public const byte TANGENT      = 6;
    public const byte BINORMAL     = 7;
    public const byte COLOR        = 10;
    public const byte FOG          = 11;
}
