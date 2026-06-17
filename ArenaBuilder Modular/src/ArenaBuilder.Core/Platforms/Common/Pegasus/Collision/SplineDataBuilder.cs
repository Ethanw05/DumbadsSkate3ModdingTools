using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Collision;

/// <summary>
/// SplineData object. Port of Python _build_splinedata (Collision_Export_Dumbad_Tuukkas_original.py lines 3973-4162).
/// </summary>
public static class SplineDataBuilder
{
    /// <summary>
    /// Build SplineData from splines. Returns empty SplineData (16 bytes of zeros) when no valid splines exist.
    /// This function matches Python behavior including:
    /// - Filtering out splines with &lt;2 points or with no segment longer than 1mm
    /// - Pointer fields as RELATIVE BYTE OFFSETS within the SplineData object (0 = NULL)
    /// </summary>
    public static byte[] Build(IReadOnlyList<IReadOnlyList<Vector3>>? splines, out int numSplinesUsed)
    {
        if (splines == null || splines.Count == 0)
        {
            numSplinesUsed = 0;
            return BuildEmpty();
        }

        // Python: valid_splines = splines with at least one segment length > 0.001
        // NOTE: Python does NOT drop short/zero-length segments; it only filters at the spline level.
        var valid = new List<IReadOnlyList<Vector3>>();
        for (int s = 0; s < splines.Count; s++)
        {
            var pts = splines[s];
            if (pts == null || pts.Count < 2) continue;

            bool hasValidSegment = false;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                double dx = (double)b.X - a.X;
                double dy = (double)b.Y - a.Y;
                double dz = (double)b.Z - a.Z;
                double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length > 0.001) { hasValidSegment = true; break; }
            }
            if (hasValidSegment) valid.Add(pts);
        }

        if (valid.Count == 0)
        {
            numSplinesUsed = 0;
            return BuildEmpty();
        }

        numSplinesUsed = valid.Count;
        return BuildWithSplines(valid);
    }

    private static byte[] BuildWithSplines(IReadOnlyList<IReadOnlyList<Vector3>> splines)
    {
        int numSplines = splines.Count;
        int totalSegments = 0;
        for (int i = 0; i < splines.Count; i++)
            totalSegments += Math.Max(0, splines[i].Count - 1);

        // Header (16 bytes)
        var blob = new List<byte>(16 + numSplines * 0x20 + totalSegments * 0x90);
        blob.AddRange(BeU32((uint)numSplines));      // m_uiNumSplines
        blob.AddRange(BeU32((uint)totalSegments));   // m_uiNumSegments
        blob.AddRange(BeU32(0x10));                  // m_Splines offset
        blob.AddRange(BeU32((uint)(0x10 + numSplines * 0x20))); // m_Segments offset

        // Spline headers (32 bytes each)
        int segmentIdx = 0;
        for (int splineIdx = 0; splineIdx < numSplines; splineIdx++)
        {
            var pts = splines[splineIdx];
            int numSegs = Math.Max(0, pts.Count - 1);

            // guid_hi: sha256(points_str).hexdigest()[:16] interpreted as hex (Python)
            // points_str = ''.join(f"{p.x:.6f}{p.y:.6f}{p.z:.6f}" for p in spline.points)
            string pointsStr = BuildPointsString(pts);
            byte[] sha = SHA256.HashData(Encoding.UTF8.GetBytes(pointsStr));
            ulong guidHi = BinaryPrimitives.ReadUInt64BigEndian(sha.AsSpan(0, 8));

            // guid_lo: constant rail/grind category id used by Python exporter
            const ulong guidLo = 0x2C7017070007004AUL;

            blob.AddRange(BeU64(guidHi));
            blob.AddRange(BeU64(guidLo));
            blob.AddRange(BeU32(0)); // m_Instance

            uint segmentsBase = (uint)(0x10 + numSplines * 0x20);
            if (numSegs > 0)
            {
                uint headOffset = segmentsBase + (uint)(segmentIdx * 0x90);
                uint tailOffset = segmentsBase + (uint)((segmentIdx + numSegs - 1) * 0x90);
                blob.AddRange(BeU32(headOffset)); // m_Head
                blob.AddRange(BeU32(tailOffset)); // m_Tail
            }
            else
            {
                blob.AddRange(BeU32(0));
                blob.AddRange(BeU32(0));
            }

            blob.AddRange(BeU32(0)); // padding
            segmentIdx += numSegs;
        }

        // Segments (144 bytes each)
        int globalSegIdx = 0;
        for (int splineIdx = 0; splineIdx < numSplines; splineIdx++)
        {
            var pts = splines[splineIdx];
            double splineStartDistance = 0.0;

            for (int segLocalIdx = 0; segLocalIdx < pts.Count - 1; segLocalIdx++)
            {
                var v1 = pts[segLocalIdx];
                var v2 = pts[segLocalIdx + 1];

                double dx = (double)v2.X - v1.X;
                double dy = (double)v2.Y - v1.Y;
                double dz = (double)v2.Z - v1.Z;
                double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                // Hermite basis (4x4) for linear segment:
                // Row0 = [dx,dy,dz,0], Row1 = 0, Row2 = 0, Row3 = [x1,y1,z1,1]
                blob.AddRange(BeF32((float)dx));
                blob.AddRange(BeF32((float)dy));
                blob.AddRange(BeF32((float)dz));
                blob.AddRange(BeF32(0f));
                for (int i = 0; i < 8; i++) blob.AddRange(BeF32(0f)); // rows 1 and 2 (8 floats)
                blob.AddRange(BeF32(v1.X));
                blob.AddRange(BeF32(v1.Y));
                blob.AddRange(BeF32(v1.Z));
                blob.AddRange(BeF32(1f));

                // Inverse parameters (4 floats)
                float invLength = length > 0 ? (float)(1.0 / length) : 0f;
                blob.AddRange(BeF32(invLength));
                blob.AddRange(BeF32(0f));
                blob.AddRange(BeF32(0f));
                blob.AddRange(BeF32(0f));

                // BBox: min(x,y,z,0) + max(x,y,z,0)
                float minX = Math.Min(v1.X, v2.X);
                float minY = Math.Min(v1.Y, v2.Y);
                float minZ = Math.Min(v1.Z, v2.Z);
                float maxX = Math.Max(v1.X, v2.X);
                float maxY = Math.Max(v1.Y, v2.Y);
                float maxZ = Math.Max(v1.Z, v2.Z);
                blob.AddRange(BeF32(minX));
                blob.AddRange(BeF32(minY));
                blob.AddRange(BeF32(minZ));
                blob.AddRange(BeF32(0f));
                blob.AddRange(BeF32(maxX));
                blob.AddRange(BeF32(maxY));
                blob.AddRange(BeF32(maxZ));
                blob.AddRange(BeF32(0f));

                // Segment properties
                blob.AddRange(BeF32((float)length));               // +0x70 m_fLength
                blob.AddRange(BeF32((float)splineStartDistance));  // +0x74 m_fDistance
                splineStartDistance += length;

                // Linkage pointers (relative byte offsets within SplineData)
                uint segmentsBase = (uint)(0x10 + numSplines * 0x20);
                uint splineHeaderOffset = (uint)(0x10 + splineIdx * 0x20);
                blob.AddRange(BeU32(splineHeaderOffset)); // +0x78 splinePtr

                if (segLocalIdx > 0)
                {
                    uint prevOffset = segmentsBase + (uint)((globalSegIdx - 1) * 0x90);
                    blob.AddRange(BeU32(prevOffset));
                }
                else
                {
                    blob.AddRange(BeU32(0));
                }

                if (segLocalIdx < pts.Count - 2)
                {
                    uint nextOffset = segmentsBase + (uint)((globalSegIdx + 1) * 0x90);
                    blob.AddRange(BeU32(nextOffset));
                }
                else
                {
                    blob.AddRange(BeU32(0));
                }

                // Padding (3 u32)
                blob.AddRange(BeU32(0));
                blob.AddRange(BeU32(0));
                blob.AddRange(BeU32(0));

                globalSegIdx++;
            }
        }

        return blob.ToArray();
    }

    private static byte[] BuildEmpty()
    {
        var empty = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(empty.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(empty.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(empty.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(empty.AsSpan(12, 4), 0);
        return empty;
    }

    private static string BuildPointsString(IReadOnlyList<Vector3> pts)
    {
        // Python: ''.join(f"{p.x:.6f}{p.y:.6f}{p.z:.6f}" for p in spline.points)
        var sb = new StringBuilder(pts.Count * 24);
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            sb.Append(p.X.ToString("F6", CultureInfo.InvariantCulture));
            sb.Append(p.Y.ToString("F6", CultureInfo.InvariantCulture));
            sb.Append(p.Z.ToString("F6", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

