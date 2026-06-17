using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;

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
        // On-disk tInstanceData layout (verified against stock DIST_SkateSchool arenas 102/103 AND
        // IDA pegasus::tInstanceData::Fixup @0x82d173f8 in sk82_na_zd.xex):
        //   +0x20 Matrix44 (offset stored at +0x0C), +0x60 bbox, +0x80 m_uiGuid (8 B),
        //   +0x88..+0x9F = SIX 0xFFFFFFFF (six null handle/override fields the engine inits to -1),
        //   tInstance[] array begins at +0xA0 (Fixup reads matrixOffset+132 = +0xA4, minus one dword).
        // Each 40-byte tInstance element:
        //   +0x00 objId -> IdToObject (model/material link), +0x04/+0x08 objId (0=null),
        //   +0x0C/+0x10/+0x14/+0x18 = FOUR string-table offsets, every one asserted NON-NULL by
        //   Offset2Addr and patched with +=ptr. Stock points +0x0C at the component name and
        //   +0x10/+0x14/+0x18 all at "undefined". Writing 0 here makes the engine resolve them to
        //   the object header (garbage) -> instance binds wrong -> mesh never draws.
        blob.AddRange(BeU64(guid));               // +0x80 m_uiGuid
        blob.AddRange(BeU64(0xFFFFFFFFFFFFFFFF));  // +0x88..0x8F
        blob.AddRange(BeU64(0xFFFFFFFFFFFFFFFF));  // +0x90..0x97
        blob.AddRange(BeU64(0xFFFFFFFFFFFFFFFF));  // +0x98..0x9F  (six 0xFFFFFFFF total, matches stock)
        blob.AddRange(BeU32(encodedPtrAt0x80)); // +0xA0: tInstance[0] objId (model dict id; stock = 0x0A)
        blob.AddRange(BeU32(encodedPtrAt0x84)); // +0xA4: objId (0 = null)
        blob.AddRange(BeU32(encodedPtrAt0x88)); // +0xA8: objId (0 = null)
        blob.AddRange(BeU32(0xC0));  // +0xAC: m_Component string offset (to component name)
        blob.AddRange(BeU32(0));     // +0xB0: string offset -> "undefined", patched below
        blob.AddRange(BeU32(0));     // +0xB4: string offset -> "undefined", patched below
        blob.AddRange(BeU32(0));     // +0xB8: string offset -> "undefined", patched below
        blob.AddRange(BeU32(0));     // +0xBC: pad
        while (blob.Count < 0xC0) blob.Add(0);
        byte[] componentBytes = Encoding.UTF8.GetBytes($"[0x{guid:x16}]{nameSuffix}\x00");
        uint categoryOffset = (uint)(0xC0 + componentBytes.Length); // Per stock dumps: offset to "undefined"
        blob.AddRange(componentBytes);
        // Patch the three "undefined" string offsets at +0xB0/+0xB4/+0xB8 (the three trailing
        // non-null string pointers the Fixup loop requires), NOT +0x90 (that's a null -1 field).
        var offBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(offBytes, categoryOffset);
        for (int i = 0; i < 4; i++) blob[0xB0 + i] = offBytes[i];
        for (int i = 0; i < 4; i++) blob[0xB4 + i] = offBytes[i];
        for (int i = 0; i < 4; i++) blob[0xB8 + i] = offBytes[i];
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

