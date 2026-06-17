using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArenaBuilder.Collision;

/// <summary>
/// Builds <see cref="PsgTocSpec"/> for collision PSG from bounds and spline count.
/// </summary>
public static class CollisionTocBuilder
{
    private static readonly uint[] TypeMapTypes =
    {
        0x00EB0066, 0x00EB0005, 0x00EB0067, 0x00EB0006, 0x00EB0001, 0x00EB000A,
        0x00EB0065, 0x00EB0007, 0x00EB0069, 0x00EB000D, 0x00EB006B, 0x00EB0019,
        0x00EB0064, 0x00EB0004, 0x00EB0068, 0x00EB0009, 0x00EB0016, 0x00EB0013,
        0x00EB0014, 0x00EB0018, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0026,
        0x00EB0027
    };

    /// <summary>
    /// Builds TOC spec for collision: InstanceSubRef, InstanceData, N×SplineSubRef, SplineData, DmoData.
    /// ObjectPtr: direct dict index (1, 5, 6) or 0x00800000 | subrefRecordIndex for subrefs.
    /// </summary>
    public static PsgTocSpec Build(
        int numSplines,
        (float X, float Y, float Z) boundsMin,
        (float X, float Y, float Z) boundsMax,
        int vertexCount,
        ulong? instanceGuidOverride = null)
    {
        string boundsStr = BuildPythonBoundsString(boundsMin, boundsMax, vertexCount);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(boundsStr));
        ulong instanceGuid = instanceGuidOverride ?? BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
        var entries = new List<PsgTocEntry>();

        // InstanceSubRef -> subref 0
        entries.Add(new PsgTocEntry(0, instanceGuid, 0x00EB0069, 0x00800000));

        // InstanceData -> dict index 1
        byte[] instHash = MD5.HashData(Encoding.UTF8.GetBytes("inst" + boundsStr));
        ulong entryHash = BinaryPrimitives.ReadUInt64BigEndian(instHash.AsSpan(0, 8));
        entries.Add(new PsgTocEntry(0, entryHash, 0x00EB000D, 1));

        // N×SplineSubRef -> subref 1..numSplines
        for (int i = 0; i < numSplines; i++)
        {
            byte[] sh = MD5.HashData(Encoding.UTF8.GetBytes($"spline{i}{boundsStr}"));
            ulong splineHash = BinaryPrimitives.ReadUInt64BigEndian(sh.AsSpan(0, 8));
            entries.Add(new PsgTocEntry(0, splineHash, 0x00EB0064, 0x00800001u + (uint)i));
        }

        // SplineData -> dict index 6
        byte[] sdHash = MD5.HashData(Encoding.UTF8.GetBytes("splinedata" + boundsStr));
        ulong splineDataHash = BinaryPrimitives.ReadUInt64BigEndian(sdHash.AsSpan(0, 8));
        entries.Add(new PsgTocEntry(0, splineDataHash, 0x00EB0004, 6));

        // DmoData -> dict index 5
        byte[] dmoHash = MD5.HashData(Encoding.UTF8.GetBytes("dmo" + boundsStr));
        ulong dmoHashVal = BinaryPrimitives.ReadUInt64BigEndian(dmoHash.AsSpan(0, 8));
        entries.Add(new PsgTocEntry(0, dmoHashVal, 0x00EB001D, 5));

        int numItems = entries.Count;
        // Type map: Python uses num_items for EVERY type (PSG_File_Format_Analysis.md lines 4300-4308)
        var typeMap = new (uint TypeId, uint StartIndex)[TypeMapTypes.Length];
        for (int i = 0; i < TypeMapTypes.Length; i++)
        {
            typeMap[i] = (TypeMapTypes[i], (uint)numItems);
        }

        return new PsgTocSpec
        {
            Entries = entries,
            TypeMap = typeMap
        };
    }

    private static string BuildPythonBoundsString(
        (float X, float Y, float Z) boundsMin,
        (float X, float Y, float Z) boundsMax,
        int vertexCount)
    {
        return $"[{PyFloat(boundsMin.X)}, {PyFloat(boundsMin.Y)}, {PyFloat(boundsMin.Z)}]" +
               $"[{PyFloat(boundsMax.X)}, {PyFloat(boundsMax.Y)}, {PyFloat(boundsMax.Z)}]" +
               $"{vertexCount}";
    }

    private static string PyFloat(float v)
    {
        double dv = v;
        if (dv == 0.0 && (BitConverter.DoubleToInt64Bits(dv) & (1L << 63)) != 0)
            return "-0.0";
        string s = dv.ToString("G17", CultureInfo.InvariantCulture).Replace('E', 'e');
        if (!s.Contains('.') && !s.Contains('e'))
            s += ".0";
        return s;
    }
}
