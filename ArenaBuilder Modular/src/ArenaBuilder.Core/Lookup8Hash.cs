using System.Text;

namespace ArenaBuilder.Core;

/// <summary>
/// Lookup8 hash algorithm used by the vault editor.
/// Produces a 64-bit hash suitable for PSG filenames (e.g. 09D7BC5A9527DD0B.psg).
/// </summary>
public static class Lookup8Hash
{
    private const ulong Seed = 0xABCDEF0011223344;
    private const ulong Prime = 0x9e3779b97f4a7c13;
    private const ulong Mask64 = 0xFFFFFFFFFFFFFFFF;

    private static ulong U64(ulong value) => value & Mask64;

    /// <summary>
    /// Hashes a string using Lookup8. Input must be ASCII.
    /// Returns a 64-bit unsigned hash suitable for formatting as 16-char hex (e.g. 09D7BC5A9527DD0B).
    /// </summary>
    public static ulong HashString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return 0;

        byte[] data = Encoding.ASCII.GetBytes(input);
        int bytesProcessed = data.Length;
        uint mixVar1 = (uint)bytesProcessed;
        ulong manipulatedPrime = Prime;
        ulong manipulatedHash = Seed;
        ulong hashVal = Seed;
        int dataPos = 0;

        unchecked
        {
            while (bytesProcessed > 0x17)
            {
                ulong lVar5 = U64(
                    manipulatedHash + (ulong)data[dataPos + 8] +
                    ((ulong)(data[dataPos + 0xF] & 0xFF) << 0x38) +
                    (ulong)(data[dataPos + 0xB] & 0xFF) * 0x1000000 +
                    ((ulong)(data[dataPos + 0xD] & 0xFF) << 0x28) +
                    (ulong)(data[dataPos + 9] & 0xFF) * 0x100 +
                    (ulong)(data[dataPos + 10] & 0xFF) * 0x10000 +
                    ((ulong)(data[dataPos + 0xC] & 0xFF) << 0x20) +
                    ((ulong)(data[dataPos + 0xE] & 0xFF) << 0x30));

                manipulatedPrime = U64(
                    manipulatedPrime + (ulong)data[dataPos + 0x10] +
                    ((ulong)(data[dataPos + 0x17] & 0xFF) << 0x38) +
                    (ulong)(data[dataPos + 0x13] & 0xFF) * 0x1000000 +
                    ((ulong)(data[dataPos + 0x15] & 0xFF) << 0x28) +
                    (ulong)(data[dataPos + 0x11] & 0xFF) * 0x100 +
                    (ulong)(data[dataPos + 0x12] & 0xFF) * 0x10000 +
                    ((ulong)(data[dataPos + 0x14] & 0xFF) << 0x20) +
                    ((ulong)(data[dataPos + 0x16] & 0xFF) << 0x30));

                manipulatedHash = U64(
                    (hashVal + (ulong)data[dataPos + 0] +
                     ((ulong)(data[dataPos + 7] & 0xFF) << 0x38) +
                     (ulong)(data[dataPos + 3] & 0xFF) * 0x1000000 +
                     ((ulong)(data[dataPos + 5] & 0xFF) << 0x28) +
                     (ulong)(data[dataPos + 1] & 0xFF) * 0x100 +
                     (ulong)(data[dataPos + 2] & 0xFF) * 0x10000 +
                     ((ulong)(data[dataPos + 4] & 0xFF) << 0x20) +
                     ((ulong)(data[dataPos + 6] & 0xFF) << 0x30) -
                     lVar5 - manipulatedPrime) ^ (manipulatedPrime >> 0x2b));

                hashVal = U64((lVar5 - manipulatedPrime - manipulatedHash) ^ (manipulatedHash << 9));
                ulong mixVar2 = U64((manipulatedPrime - manipulatedHash - hashVal) ^ (hashVal >> 8));
                manipulatedHash = U64((manipulatedHash - hashVal - mixVar2) ^ (mixVar2 >> 0x26));
                hashVal = U64((hashVal - mixVar2 - manipulatedHash) ^ (manipulatedHash << 0x17));
                mixVar2 = U64((mixVar2 - manipulatedHash - hashVal) ^ (hashVal >> 5));
                manipulatedHash = U64((manipulatedHash - hashVal - mixVar2) ^ (mixVar2 >> 0x23));
                manipulatedPrime = U64((hashVal - mixVar2 - manipulatedHash) ^ (manipulatedHash << 0x31));
                mixVar2 = U64((mixVar2 - manipulatedHash - manipulatedPrime) ^ (manipulatedPrime >> 0xb));
                hashVal = U64((manipulatedHash - manipulatedPrime - mixVar2) ^ (mixVar2 >> 0xc));
                manipulatedHash = U64((manipulatedPrime - mixVar2 - hashVal) ^ (hashVal << 0x12));
                manipulatedPrime = U64((mixVar2 - hashVal - manipulatedHash) ^ (manipulatedHash >> 0x16));

                dataPos += 0x18;
                bytesProcessed -= 0x18;
            }

            manipulatedPrime = U64(manipulatedPrime + (mixVar1 & 0xFFFFFFFF));

            int remaining = bytesProcessed;
            if (remaining >= 0x17) manipulatedPrime = U64(manipulatedPrime + ((ulong)(data[dataPos + 0x16] & 0xFF) << 0x38));
            if (remaining >= 0x16) manipulatedPrime = U64(manipulatedPrime + ((ulong)(data[dataPos + 0x15] & 0xFF) << 0x30));
            if (remaining >= 0x15) manipulatedPrime = U64(manipulatedPrime + ((ulong)(data[dataPos + 0x14] & 0xFF) << 0x28));
            if (remaining >= 0x14) manipulatedPrime = U64(manipulatedPrime + ((ulong)(data[dataPos + 0x13] & 0xFF) << 0x20));
            if (remaining >= 0x13) manipulatedPrime = U64(manipulatedPrime + (ulong)(data[dataPos + 0x12] & 0xFF) * 0x1000000);
            if (remaining >= 0x12) manipulatedPrime = U64(manipulatedPrime + (ulong)(data[dataPos + 0x11] & 0xFF) * 0x10000);
            if (remaining >= 0x11) manipulatedPrime = U64(manipulatedPrime + (ulong)(data[dataPos + 0x10] & 0xFF) * 0x100);
            if (remaining >= 0x10) manipulatedHash = U64(manipulatedHash + ((ulong)(data[dataPos + 0xF] & 0xFF) << 0x38));
            if (remaining >= 0xF) manipulatedHash = U64(manipulatedHash + ((ulong)(data[dataPos + 0xE] & 0xFF) << 0x30));
            if (remaining >= 0xE) manipulatedHash = U64(manipulatedHash + ((ulong)(data[dataPos + 0xD] & 0xFF) << 0x28));
            if (remaining >= 0xD) manipulatedHash = U64(manipulatedHash + ((ulong)(data[dataPos + 0xC] & 0xFF) << 0x20));
            if (remaining >= 0xC) manipulatedHash = U64(manipulatedHash + (ulong)(data[dataPos + 0xB] & 0xFF) * 0x1000000);
            if (remaining >= 0xB) manipulatedHash = U64(manipulatedHash + (ulong)(data[dataPos + 10] & 0xFF) * 0x10000);
            if (remaining >= 10) manipulatedHash = U64(manipulatedHash + (ulong)(data[dataPos + 9] & 0xFF) * 0x100);
            if (remaining >= 9) manipulatedHash = U64(manipulatedHash + (ulong)(data[dataPos + 8] & 0xFF));
            if (remaining >= 8) hashVal = U64(hashVal + ((ulong)(data[dataPos + 7] & 0xFF) << 0x38));
            if (remaining >= 7) hashVal = U64(hashVal + ((ulong)(data[dataPos + 6] & 0xFF) << 0x30));
            if (remaining >= 6) hashVal = U64(hashVal + ((ulong)(data[dataPos + 5] & 0xFF) << 0x28));
            if (remaining >= 5) hashVal = U64(hashVal + ((ulong)(data[dataPos + 4] & 0xFF) << 0x20));
            if (remaining >= 4) hashVal = U64(hashVal + (ulong)(data[dataPos + 3] & 0xFF) * 0x1000000);
            if (remaining >= 3) hashVal = U64(hashVal + (ulong)(data[dataPos + 2] & 0xFF) * 0x10000);
            if (remaining >= 2) hashVal = U64(hashVal + (ulong)(data[dataPos + 1] & 0xFF) * 0x100);
            if (remaining >= 1) hashVal = U64(hashVal + (ulong)(data[dataPos] & 0xFF));

            ulong mixVar1L = U64((hashVal - manipulatedHash - manipulatedPrime) ^ (manipulatedPrime >> 0x2b));
            hashVal = U64((manipulatedHash - manipulatedPrime - mixVar1L) ^ (mixVar1L << 9));
            ulong mixVar2F = U64((manipulatedPrime - mixVar1L - hashVal) ^ (hashVal >> 8));
            manipulatedHash = U64((mixVar1L - hashVal - mixVar2F) ^ (mixVar2F >> 0x26));
            mixVar1L = U64((hashVal - mixVar2F - manipulatedHash) ^ (manipulatedHash << 0x17));
            mixVar2F = U64((mixVar2F - manipulatedHash - mixVar1L) ^ (mixVar1L >> 5));
            manipulatedHash = U64((manipulatedHash - mixVar1L - mixVar2F) ^ (mixVar2F >> 0x23));
            mixVar1L = U64((mixVar1L - mixVar2F - manipulatedHash) ^ (manipulatedHash << 0x31));
            mixVar2F = U64((mixVar2F - manipulatedHash - mixVar1L) ^ (mixVar1L >> 0xb));
            manipulatedHash = U64((manipulatedHash - mixVar1L - mixVar2F) ^ (mixVar2F >> 0xc));
            mixVar1L = U64((mixVar1L - mixVar2F - manipulatedHash) ^ (manipulatedHash << 0x12));
            ulong result = U64((mixVar2F - manipulatedHash - mixVar1L) ^ (mixVar1L >> 0x16));

            return result;
        }
    }

    /// <summary>
    /// Returns the Lookup8 hash of the input string formatted as 16-char uppercase hex (e.g. 09D7BC5A9527DD0B).
    /// </summary>
    public static string HashStringToHex(string input) => HashString(input).ToString("X16");
}
