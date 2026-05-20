using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// InstanceData object. Per IDA pegasus::tInstanceData::Fixup (0x827E7878): tInstance has three
/// encoded pointers at +0x80, +0x84, +0x88 (decoded via section/type table, same as TOC).
/// documentation/cPres Documentation/InstanceData.txt and PsgFunctDocumentation/pegasus_tInstanceData_Fixup.
/// +0x80 = m_pMaterial (encoded subref to RenderMaterialData slot; real files use 0x00800000 = subref 0).
/// +0x84, +0x88 = second/third encoded ptr (0 = null for mesh-only).
/// </summary>
public static class InstanceDataBuilder
{
    /// <summary>Encoded subref for first material slot (RenderMaterialData @ Material[0]). Per real dumps and IDA.</summary>
    public const uint MaterialSubref0 = 0x00800000u;

    /// <summary>
    /// Computes the instance GUID exactly as this builder serializes it into tInstance[0].m_uiGuid.
    /// Reuse this for TOC Instancesubref linkage so both GUIDs stay in sync.
    /// Uses full 64 bits of MD5 to avoid collisions when multiple meshes/chunks are in the same cPres
    /// (old formula used only low byte → 256 max values → duplicate filenames).
    /// </summary>
    /// <param name="boundsMin">Axis-aligned bounds minimum.</param>
    /// <param name="boundsMax">Axis-aligned bounds maximum.</param>
    /// <param name="vertexCount">Vertex count.</param>
    /// <param name="namespaceSuffix">Optional suffix so mesh and collision from the same GLB get different GUIDs (e.g. "mesh" vs "collision"). Pass null or empty for legacy behavior.</param>
    public static ulong ComputeInstanceGuid(
        (float X, float Y, float Z) boundsMin,
        (float X, float Y, float Z) boundsMax,
        int vertexCount,
        string? namespaceSuffix = null)
    {
        string boundsStr = BuildPythonBoundsString(boundsMin, boundsMax, vertexCount);
        if (!string.IsNullOrEmpty(namespaceSuffix))
            boundsStr = boundsStr + "|" + namespaceSuffix;
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(boundsStr));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
    }

    public static byte[] Build(
        (float X, float Y, float Z) boundsMin,
        (float X, float Y, float Z) boundsMax,
        int vertexCount,
        uint encodedPtrAt0x80 = 0x00800000u,
        uint encodedPtrAt0x84 = 0,
        uint encodedPtrAt0x88 = 0,
        string nameSuffix = "_Blender_Export_Collision",
        ulong? instanceGuidOverride = null)
    {
        // Python: bounds_str = f"{self.obj.bounds_min}{self.obj.bounds_max}{len(self.obj.vertices)}"
        // bounds_min/max are Python lists printed with ", " separators and float formatting.
        ulong guid = instanceGuidOverride ?? ComputeInstanceGuid(boundsMin, boundsMax, vertexCount);

        var blob = new List<byte>();
        blob.AddRange(BeU32(0xACB31C9A));
        blob.AddRange(BeU32(1));
        blob.AddRange(BeU32(2));
        blob.AddRange(BeU32(0x20));
        blob.AddRange(BeU32(0xC0));
        while (blob.Count < 0x20) blob.Add(0);
        for (int i = 0; i < 16; i++)
            blob.AddRange(BeF32(i % 5 == 0 ? 1f : 0f));
        blob.AddRange(BeF32(boundsMin.X));
        blob.AddRange(BeF32(boundsMin.Y));
        blob.AddRange(BeF32(boundsMin.Z));
        blob.AddRange(BeF32(0));
        blob.AddRange(BeF32(boundsMax.X));
        blob.AddRange(BeF32(boundsMax.Y));
        blob.AddRange(BeF32(boundsMax.Z));
        blob.AddRange(BeF32(0));
        blob.AddRange(BeU64(guid));
        blob.AddRange(BeU64(0xFFFFFFFFFFFFFFFF));
        blob.AddRange(BeU64(0xFFFFFFFFFFFFFFFF));
        blob.AddRange(BeU32(0)); // +0x78: m_Name (0 = null; real meshes use 0, not 0xFFFFFFFF)
        blob.AddRange(BeU32(0)); // +0x7C: m_Description (0 = null; real meshes use 0)
        blob.AddRange(BeU32(encodedPtrAt0x80)); // +0x80: m_pMaterial (mesh: dict index; collision: subref 0x00800000)
        blob.AddRange(BeU32(encodedPtrAt0x84)); // +0x84: encoded ptr (0 = null)
        blob.AddRange(BeU32(encodedPtrAt0x88)); // +0x88: encoded ptr (0 = null)
        blob.AddRange(BeU32(0xC0));  // m_Component: offset to first string
        blob.AddRange(BeU32(0));     // m_Category: placeholder, patched below
        blob.AddRange(BeU32(0));     // field_0x94: placeholder, patched below
        blob.AddRange(BeU32(0));     // field_0x98: placeholder, patched below
        blob.AddRange(BeU32(0));
        while (blob.Count < 0xC0) blob.Add(0);
        byte[] componentBytes = Encoding.UTF8.GetBytes($"[0x{guid:x16}]{nameSuffix}\x00");
        uint categoryOffset = (uint)(0xC0 + componentBytes.Length); // Per real dumps: offset to "undefined"
        blob.AddRange(componentBytes);
        // Patch m_Category, field_0x94, field_0x98 at offsets 0x90, 0x94, 0x98
        var offBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(offBytes, categoryOffset);
        for (int i = 0; i < 4; i++) blob[0x90 + i] = offBytes[i];
        for (int i = 0; i < 4; i++) blob[0x94 + i] = offBytes[i];
        for (int i = 0; i < 4; i++) blob[0x98 + i] = offBytes[i];
        while (blob.Count < (int)categoryOffset) blob.Add(0);
        blob.AddRange(Encoding.UTF8.GetBytes("undefined\x00"));
        while (blob.Count < 0x128) blob.Add(0);
        return blob.ToArray();
    }

    private static string BuildPythonBoundsString(
        (float X, float Y, float Z) boundsMin,
        (float X, float Y, float Z) boundsMax,
        int vertexCount)
    {
        // Produces: "[minX, minY, minZ][maxX, maxY, maxZ]{vertexCount}"
        // matching Python list __str__ formatting closely.
        return $"[{PyFloat(boundsMin.X)}, {PyFloat(boundsMin.Y)}, {PyFloat(boundsMin.Z)}]" +
               $"[{PyFloat(boundsMax.X)}, {PyFloat(boundsMax.Y)}, {PyFloat(boundsMax.Z)}]" +
               $"{vertexCount}";
    }

    private static string PyFloat(float v)
    {
        // Python float is double; use double formatting for closer parity.
        double dv = v;
        // Preserve "-0.0"
        if (dv == 0.0 && (BitConverter.DoubleToInt64Bits(dv) & (1L << 63)) != 0)
            return "-0.0";

        string s = dv.ToString("G17", CultureInfo.InvariantCulture).Replace('E', 'e');
        if (!s.Contains('.') && !s.Contains('e'))
            s += ".0";
        return s;
    }
}

