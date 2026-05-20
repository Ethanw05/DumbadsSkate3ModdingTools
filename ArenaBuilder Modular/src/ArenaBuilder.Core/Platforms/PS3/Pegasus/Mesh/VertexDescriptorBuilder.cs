using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// VertexDescriptor object (0x000200E9). Layout matches real mesh PSG dumps and
/// blender_psg_material_importer._parse_vertex_descriptor:
/// - Header is 16 bytes; element count is read from bytes 10-11 (vd_offset + 0x0A).
/// - Elements at offset 0x10, 8 bytes each: [0]=vertex_type, [1]=num_components, [2]=stream, [3]=offset,
///   [4:6]=stride(be), [6]=element_type, [7]=class_id.
/// - Real meshes use 4 elements (Position, TEX0, TEX1, Tangent) or 3 elements (no Tangent).
/// - 4-element: 56 bytes, stride 28; 3-element: 48 bytes, stride 24. Both end with 8-byte terminator.
/// </summary>
public static class VertexDescriptorBuilder
{
    // Element types (blender_psg_material_importer ELEMENT_TYPE_* / glbtopsg elem_map)
    public const byte ElemXYZ = 0;
    public const byte ElemTEX0 = 8;
    public const byte ElemTEX1 = 9;
    public const byte ElemTANGENT = 14;

    // Vertex type formats (blender_psg_material_importer VT_*)
    public const byte VT_INT16 = 0x01;
    public const byte VT_FLOAT32 = 0x02;
    public const byte VT_FLOAT16 = 0x03;
    public const byte VT_DEC3N = 0x06;

    /// <summary>
    /// Builds the real mesh static VertexDescriptor (stride 28):
    /// Position(0), TEX0(12), TEX1(16), Tangent(24), +4 bytes reserved at 20..23.
    /// </summary>
    public static byte[] BuildStaticMeshLayout()
    {
        const int stride = 28;
        var buf = new List<byte>(56);

        // Header bytes per real mesh PSGs. Real 4-element meshes use 0x4301 at 0x06; 3-element use 0x0301.
        // Blender parser consumes byte 10..11 as element count (4).
        buf.AddRange(BeU32(0x00000000)); // 0x00
        buf.AddRange(BeU16(0x0000));     // 0x04
        buf.AddRange(BeU16(0x4301));     // 0x06 (real 4-element constant; 3-element uses 0x0301)
        buf.AddRange(BeU16(0x0001));     // 0x08 instanceStreams
        buf.AddRange(BeU16(0x0004));     // 0x0A num_elements
        buf.AddRange(BeU16(0x000F));     // 0x0C (real-mesh constant)
        buf.AddRange(BeU16(0x0000));     // 0x0E

        // 8-byte elements per blender_psg_material_importer: elem_data[0..7]
        AddElement(buf, VT_FLOAT32, 3, 0, 0, stride, ElemXYZ, 1);      // Position
        AddElement(buf, VT_FLOAT16, 2, 0, 12, stride, ElemTEX0, 1);    // TEX0 (half2)
        // Real mesh PSGs store TEX1 metadata as 0x01 0x04 ... 0x09 0x01.
        // glbtopsg/PsgMeshnBones treat this as TEX1 (UV1) data, not NORMAL.
        AddElement(buf, VT_INT16, 4, 0, 16, stride, ElemTEX1, 1);      // TEX1 (int16 pair in 4-byte slot)
        AddElement(buf, VT_DEC3N, 1, 0, 24, stride, ElemTANGENT, 1);   // Tangent

        // Real meshes include an 8-byte terminator.
        buf.Add(0xFF);
        buf.Add(0x00);
        buf.Add(0xFF);
        buf.Add(0x00);
        buf.AddRange(BeU32(0x00000000));

        return buf.ToArray();
    }

    /// <summary>
    /// Writes one 8-byte element in blender_psg_material_importer format:
    /// [0]=vertex_type, [1]=num_components, [2]=stream, [3]=offset, [4:6]=stride(be), [6]=element_type, [7]=class_id.
    /// </summary>
    private static void AddElement(List<byte> buf, byte vertexType, byte numComponents, byte stream, byte offset, ushort stride, byte elementType, byte classId)
    {
        buf.Add(vertexType);
        buf.Add(numComponents);
        buf.Add(stream);
        buf.Add(offset);
        buf.AddRange(BeU16(stride));
        buf.Add(elementType);
        buf.Add(classId);
    }
}

