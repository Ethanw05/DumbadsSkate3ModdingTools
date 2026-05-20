using System.Text;

namespace DlcBuilder.Builders;

/// Bob Jenkins lookup8 hash — the canonical 64-bit hash EA used for vault key
/// resolution (collection keys, attribute keys, type names, parent links).
/// Bit-identical port of `Attribulator.Hashing.Hash` so we don't have to
/// reference the decompiled Attribulator.dll.
///
/// The seed `0xABCDEF0011223344` and the 24-byte block schedule + final-mix
/// are exactly what the runtime uses to look attributes up in
/// `sub_733450(row, attr_hash)`. Matching it byte-for-byte is non-negotiable
/// — every hash we emit on disk has to match what the engine recomputes at
/// load time.
public static class Lookup8Hashing
{
    private const ulong Seed = 0xABCDEF0011223344UL;

    /// 64-bit lookup8 hash of an ASCII string.
    public static ulong Hash(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        ulong hash = Seed;
        byte[] data = Encoding.ASCII.GetBytes(input);
        uint bytesProcessed = (uint)input.Length;
        ulong mixVar1 = bytesProcessed;
        ulong manipulatedPrime = 0x9E3779B97F4A7C13UL;
        ulong mixVar2;
        ulong manipulatedhash = hash;
        int dataPos = 0;

        if (mixVar1 < 0x18)
        {
            manipulatedPrime = 0x9E3779B97F4A7C13UL;
            manipulatedhash = hash;
        }
        else
        {
            do
            {
                ulong lVar5 = manipulatedhash + data[dataPos + 8] + ((ulong)data[dataPos + 0xf] << 0x38) +
                              (ulong)data[dataPos + 0xb] * 0x1000000 + ((ulong)data[dataPos + 0xd] << 0x28) +
                              (ulong)data[dataPos + 9] * 0x100 + (ulong)data[dataPos + 10] * 0x10000 +
                              ((ulong)data[dataPos + 0xc] << 0x20) + ((ulong)data[dataPos + 0xe] << 0x30);
                manipulatedPrime = manipulatedPrime + data[dataPos + 0x10] + ((ulong)data[dataPos + 0x17] << 0x38) +
                                (ulong)data[dataPos + 0x13] * 0x1000000 + ((ulong)data[dataPos + 0x15] << 0x28) +
                                (ulong)data[dataPos + 0x11] * 0x100 + (ulong)data[dataPos + 0x12] * 0x10000 +
                                ((ulong)data[dataPos + 0x14] << 0x20) + ((ulong)data[dataPos + 0x16] << 0x30);
                manipulatedhash = hash + data[dataPos + 0] + ((ulong)data[dataPos + 7] << 0x38) +
                                  (ulong)data[dataPos + 3] * 0x1000000 + ((ulong)data[dataPos + 5] << 0x28) +
                                  (ulong)data[dataPos + 1] * 0x100 + (ulong)data[dataPos + 2] * 0x10000 +
                                  ((ulong)data[dataPos + 4] << 0x20) + ((ulong)data[dataPos + 6] << 0x30) - lVar5
                        - manipulatedPrime ^ manipulatedPrime >> 0x2b;
                hash = lVar5 - manipulatedPrime - manipulatedhash ^ manipulatedhash << 9;
                mixVar2 = manipulatedPrime - manipulatedhash - hash ^ hash >> 8;
                manipulatedhash = manipulatedhash - hash - mixVar2 ^ mixVar2 >> 0x26;
                hash = hash - mixVar2 - manipulatedhash ^ manipulatedhash << 0x17;
                mixVar2 = mixVar2 - manipulatedhash - hash ^ hash >> 5;
                manipulatedhash = manipulatedhash - hash - mixVar2 ^ mixVar2 >> 0x23;
                manipulatedPrime = hash - mixVar2 - manipulatedhash ^ manipulatedhash << 0x31;
                mixVar2 = mixVar2 - manipulatedhash - manipulatedPrime ^ manipulatedPrime >> 0xb;
                hash = manipulatedhash - manipulatedPrime - mixVar2 ^ mixVar2 >> 0xc;
                manipulatedhash = manipulatedPrime - mixVar2 - hash ^ hash << 0x12;
                manipulatedPrime = mixVar2 - hash - manipulatedhash ^ manipulatedhash >> 0x16;
                dataPos += 0x18;
                bytesProcessed -= 0x18;
            } while (0x17 < bytesProcessed);
        }
        manipulatedPrime += mixVar1 & 0xffffffffUL;
        switch (bytesProcessed)
        {
            case 0x17: manipulatedPrime += (ulong)data[dataPos + 0x16] << 0x38; goto case 0x16;
            case 0x16: manipulatedPrime += (ulong)data[dataPos + 0x15] << 0x30; goto case 0x15;
            case 0x15: manipulatedPrime += (ulong)data[dataPos + 0x14] << 0x28; goto case 0x14;
            case 0x14: manipulatedPrime += (ulong)data[dataPos + 0x13] << 0x20; goto case 0x13;
            case 0x13: manipulatedPrime += (ulong)data[dataPos + 0x12] * 0x1000000; goto case 0x12;
            case 0x12: manipulatedPrime += (ulong)data[dataPos + 0x11] * 0x10000; goto case 0x11;
            case 0x11: manipulatedPrime += (ulong)data[dataPos + 0x10] * 0x100; goto case 0x10;
            case 0x10: manipulatedhash += (ulong)data[dataPos + 0xf] << 0x38; goto case 0xf;
            case 0xf:  manipulatedhash += (ulong)data[dataPos + 0xe] << 0x30; goto case 0xe;
            case 0xe:  manipulatedhash += (ulong)data[dataPos + 0xd] << 0x28; goto case 0xd;
            case 0xd:  manipulatedhash += (ulong)data[dataPos + 0xc] << 0x20; goto case 0xc;
            case 0xc:  manipulatedhash += (ulong)data[dataPos + 0xb] * 0x1000000; goto case 0xb;
            case 0xb:  manipulatedhash += (ulong)data[dataPos + 10] * 0x10000; goto case 0xa;
            case 10:   manipulatedhash += (ulong)data[dataPos + 9] * 0x100; goto case 9;
            case 9:    manipulatedhash += data[dataPos + 8]; goto case 8;
            case 8:    hash += (ulong)data[dataPos + 7] << 0x38; goto case 7;
            case 7:    hash += (ulong)data[dataPos + 6] << 0x30; goto case 6;
            case 6:    hash += (ulong)data[dataPos + 5] << 0x28; goto case 5;
            case 5:    hash += (ulong)data[dataPos + 4] << 0x20; goto case 4;
            case 4:    hash += (ulong)data[dataPos + 3] * 0x1000000; goto case 3;
            case 3:    hash += (ulong)data[dataPos + 2] * 0x10000; goto case 2;
            case 2:    hash += (ulong)data[dataPos + 1] * 0x100; goto case 1;
            case 1:    hash += data[dataPos]; goto default;
            default:
                mixVar1 = hash - manipulatedhash - manipulatedPrime ^ manipulatedPrime >> 0x2b;
                hash = manipulatedhash - manipulatedPrime - mixVar1 ^ mixVar1 << 9;
                mixVar2 = manipulatedPrime - mixVar1 - hash ^ hash >> 8;
                manipulatedhash = mixVar1 - hash - mixVar2 ^ mixVar2 >> 0x26;
                mixVar1 = hash - mixVar2 - manipulatedhash ^ manipulatedhash << 0x17;
                mixVar2 = mixVar2 - manipulatedhash - mixVar1 ^ mixVar1 >> 5;
                manipulatedhash = manipulatedhash - mixVar1 - mixVar2 ^ mixVar2 >> 0x23;
                mixVar1 = mixVar1 - mixVar2 - manipulatedhash ^ manipulatedhash << 0x31;
                mixVar2 = mixVar2 - manipulatedhash - mixVar1 ^ mixVar1 >> 0xb;
                manipulatedhash = manipulatedhash - mixVar1 - mixVar2 ^ mixVar2 >> 0xc;
                mixVar1 = mixVar1 - mixVar2 - manipulatedhash ^ manipulatedhash << 0x12;
                return mixVar2 - manipulatedhash - mixVar1 ^ mixVar1 >> 0x16;
        }
    }
}
