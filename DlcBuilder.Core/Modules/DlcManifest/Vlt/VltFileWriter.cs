using System.IO;
using System.Text;
using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Assembles a complete `.vlt` file from a list of `CollectionBlob`s + the
/// matching `binFixups` list (PtrN patches into the BinPool sibling `.bin`).
///
/// File framing (every retail vlt):
///   Vers chunk    fixed magic uint64 0x6928BD0F2C5B3768
///   StrN chunk    8 bytes (count=0, offset=0) — vlts use a separate .bin pool
///   DepN chunk    dependency table (the .vlt + companion .bin filename pair)
///   DatN chunk    raw row payloads, concatenated in order
///   ExpN chunk    exports table — one entry per row with class/key hash + DatN offset
///   PtrN chunk    runtime pointer fixup table (bin pointers + per-row attribute fixups)
///   EndC chunk    8-byte zero terminator
///
/// PtrN fixup entry shape (each is 16 bytes):
///   u32 fixup_offset   absolute offset within the file the runtime patches
///   u16 type           2 = "no fixup target" (sentinel rows), 3 = bin-pointer
///   u16 idx            1 = bin-pool target, 0 = sentinel
///   u64 ptr            value to write at fixup_offset (bin offset for type=3)
public static class VltFileWriter
{
    /// Build the final `.vlt` bytes. Mutates each `CollectionBlob.RelativeOffset`
    /// as a side effect (assigns its position within the DatN payload).
    public static byte[] BuildVltWithCollections(
        string vltFileName,
        string dependencyName,
        IReadOnlyList<CollectionBlob> collections,
        IReadOnlyList<(uint fixupOffset, uint ptrValue)> binFixups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vltFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyName);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(binFixups);

        byte[] datPayload = BuildDatPayload(collections);

        // Vers chunk fingerprint — hardcoded constant matching every VLT
        // Black Box ships, including all DLC. Verified against MinimalDlcBuilder
        // /DlcBuilder.cs:3416. Using a different value here writes a header
        // the engine's vault loader rejects (Attribulator's Verify accepts
        // arbitrary fingerprints, but the in-game loader compares against
        // this exact constant during boot manifest scan).
        byte[] vers = VltPayload.Chunk("Vers", VltPayload.Build(w => w.WriteBE(0x693CDCC3F57ADB28UL)));
        byte[] strN = VltPayload.Chunk("StrN", VltPayload.Build(w => { w.WriteBE(0u); w.WriteBE(0u); }));
        byte[] depN = VltPayload.Chunk("DepN", BuildDependencyPayload(vltFileName, dependencyName));
        byte[] datN = VltPayload.Chunk("DatN", datPayload);

        // DatN starts after Vers + StrN + DepN; absolute file offset of the
        // first byte of the DatN PAYLOAD = sum of those three chunks + 8 (DatN's own header).
        int datAbsStart = vers.Length + strN.Length + depN.Length + 8;
        byte[] expN = VltPayload.Chunk("ExpN", BuildExportsPayload(collections, datAbsStart));
        byte[] ptrN = VltPayload.Chunk("PtrN", BuildPtrnPayload(collections, datAbsStart, binFixups));
        byte[] endC = VltPayload.Chunk("EndC", new byte[8]);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(vers);
        bw.Write(strN);
        bw.Write(depN);
        bw.Write(datN);
        bw.Write(expN);
        bw.Write(ptrN);
        bw.Write(endC);
        return ms.ToArray();
    }

    /// Concatenate every CollectionBlob's body into one buffer. Sets each
    /// blob's `RelativeOffset` to its starting byte within DatN — the PtrN
    /// builder reads these to compute fixup offsets.
    public static byte[] BuildDatPayload(IReadOnlyList<CollectionBlob> collections)
    {
        ArgumentNullException.ThrowIfNull(collections);
        int total = 0;
        foreach (var c in collections) total += c.Blob.Length;

        byte[] dst = new byte[total];
        int dstOffset = 0;
        foreach (var c in collections)
        {
            c.RelativeOffset = dstOffset;
            Buffer.BlockCopy(c.Blob, 0, dst, dstOffset, c.Blob.Length);
            dstOffset += c.Blob.Length;
        }
        return dst;
    }

    /// DepN payload: dependency-name table (always 2 entries: the .vlt + the .bin).
    /// Hashes both names + writes them as fixed-stride zero-padded strings so
    /// the engine can resolve them without reading the file's StrN.
    public static byte[] BuildDependencyPayload(string vlt, string bin)
    {
        string[] deps = { vlt, bin };
        int slotSize = deps.Max(d => Encoding.ASCII.GetByteCount(d) + 1);
        return VltPayload.Build(w =>
        {
            w.WriteBE(0u);
            w.WriteBE((uint)deps.Length);
            foreach (string d in deps) w.WriteBE(Lookup8Hashing.Hash(d));
            w.WriteBE(0u);
            w.WriteBE((uint)slotSize);
            foreach (string s in deps)
            {
                byte[] b = Encoding.ASCII.GetBytes(s);
                w.Write(b);
                w.Write((byte)0);
                int pad = slotSize - b.Length - 1;
                if (pad > 0) w.Write(new byte[pad]);
            }
        });
    }

    /// ExpN payload: per-row export table. One entry per `CollectionBlob`:
    /// (Hash("<class>/<key>"), Hash("Attrib::CollectionLoadData"), blob_size,
    ///  abs_offset_in_file). The engine uses this to locate each row by class/key.
    public static byte[] BuildExportsPayload(IReadOnlyList<CollectionBlob> collections, int datAbsStart)
    {
        ulong typeHash = Lookup8Hashing.Hash("Attrib::CollectionLoadData");
        return VltPayload.Build(w =>
        {
            w.WriteBE((ulong)collections.Count);
            foreach (var c in collections)
            {
                w.WriteBE(Lookup8Hashing.Hash($"{c.ClassName}/{c.Key}"));
                w.WriteBE(typeHash);
                w.WriteBE((uint)c.Blob.Length);
                w.WriteBE((uint)(datAbsStart + c.RelativeOffset));
            }
        });
    }

    /// PtrN payload: runtime pointer fixup table.
    ///   - First a leading `(0, type=2, idx=1, 0)` sentinel when there are bin fixups.
    ///   - Then one bin-pointer entry per binFixup: `(fixup_offset, type=3, idx=1, ptr_value)`.
    ///   - Then a `(0, type=2, idx=0, 0)` sentinel between bin and row fixups.
    ///   - Then per-row entries: layout pointer (if any) + per-attribute fixups
    ///     for attributes whose `NeedsFixupMask` is true.
    ///   - Terminating `(0, 0, 0, 0)` entry.
    public static byte[] BuildPtrnPayload(
        IReadOnlyList<CollectionBlob> collections,
        int datAbsStart,
        IReadOnlyList<(uint fixupOffset, uint ptrValue)> binFixups)
    {
        return VltPayload.Build(w =>
        {
            if (binFixups.Count > 0)
            {
                PtrEntry(w, 0u, 2, 1, 0UL);
                foreach (var (fixup, ptr) in binFixups)
                    PtrEntry(w, fixup, 3, 1, ptr);
            }

            PtrEntry(w, 0u, 2, 0, 0UL);
            foreach (var c in collections)
            {
                int rowAbs = datAbsStart + c.RelativeOffset;
                if (c.LayoutOffset > 0u)
                    PtrEntry(w, (uint)(rowAbs + 40), 3, 1, c.LayoutOffset);

                int attrTableStart = 0x30 + c.TypeCount * 8;
                for (int i = 0; i < c.Attributes.Length; i++)
                {
                    if (c.NeedsFixupMask[i])
                        PtrEntry(w, (uint)(rowAbs + attrTableStart + i * 16 + 8), 3, 1, c.Attributes[i].Data);
                }
            }
            PtrEntry(w, 0u, 0, 0, 0UL);
        });
    }

    private static void PtrEntry(BinaryWriter w, uint fixup, ushort type, ushort idx, ulong ptr)
    {
        w.WriteBE(fixup);
        w.WriteBE(type);
        w.WriteBE(idx);
        w.WriteBE(ptr);
    }
}
