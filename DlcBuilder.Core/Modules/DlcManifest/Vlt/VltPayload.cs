using System.IO;
using System.Text;
using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Tiny utilities used by every VLT writer: a `Payload` builder that wraps an
/// `Action<BinaryWriter>` into a byte[], and the EA chunk framing (4-char
/// magic + big-endian total-size, 16-byte aligned).
public static class VltPayload
{
    /// Build a byte[] by writing into a transient `BinaryWriter`. Standard
    /// pattern across the VLT codebase — replaces the original
    /// `static byte[] Payload(Action<BinaryWriter>)` helper.
    public static byte[] Build(Action<BinaryWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            write(bw);
        }
        return ms.ToArray();
    }

    /// EA chunk framing: 4-char ASCII magic + big-endian uint32 total size +
    /// payload + zero pad to 16-byte alignment.
    public static byte[] Chunk(string magic, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magic);
        ArgumentNullException.ThrowIfNull(payload);
        if (Encoding.ASCII.GetByteCount(magic) != 4)
            throw new ArgumentException("Chunk magic must be exactly 4 ASCII chars.", nameof(magic));

        int unpadded = 8 + payload.Length;
        int pad = (16 - unpadded % 16) % 16;
        return Build(w =>
        {
            w.Write(Encoding.ASCII.GetBytes(magic));
            w.WriteBE((uint)(unpadded + pad));
            w.Write(payload);
            if (pad > 0) w.Write(new byte[pad]);
        });
    }
}
