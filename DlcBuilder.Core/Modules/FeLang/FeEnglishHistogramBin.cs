using DlcBuilder.Builders;

namespace DlcBuilder.Modules.FeLang;

/// `LANGUAGE_English_Histogram.bin` — fixed 6,156-byte header + body the FE
/// language system reads alongside `LANGUAGE_English.bin`. Header (12 bytes
/// LE):
///   u32 magic            = 0x00039001
///   u32 payload_length   = 6148
///   u32 magic2           = 0x00000100
/// Body is 6,144 zero bytes that the histogram path tolerates as "empty"
/// (verified across every retail DLC).
public static class FeEnglishHistogramBin
{
    public static byte[] Build()
    {
        byte[] buf = new byte[6156];
        using var ms = new System.IO.MemoryStream(buf);
        using var w = new System.IO.BinaryWriter(ms);
        w.WriteLE(0x00039001u);
        w.WriteLE(6148u);
        w.WriteLE(0x00000100u);
        return buf;
    }
}
