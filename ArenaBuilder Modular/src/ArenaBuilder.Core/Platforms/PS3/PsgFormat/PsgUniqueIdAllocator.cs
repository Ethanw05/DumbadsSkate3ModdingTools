using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Process-wide unique ID allocator for PSG generation.
/// Uses existing computed IDs as seeds, then probes deterministic variants on collision.
/// </summary>
public static class PsgUniqueIdAllocator
{
    private static readonly ConcurrentDictionary<uint, byte> ArenaIds = new();
    private static readonly ConcurrentDictionary<ulong, byte> Guid64Ids = new();

    /// <summary>
    /// Returns a process-unique arena ID. Preserves seed when available.
    /// Avoids reserved values 0 and 0xFFFFFFFF.
    /// </summary>
    public static uint AcquireArenaId(uint seed)
    {
        uint candidate = NormalizeArenaSeed(seed);
        for (int i = 0; i < int.MaxValue; i++)
        {
            if (ArenaIds.TryAdd(candidate, 0))
                return candidate;
            candidate = NextUInt32(candidate);
            candidate = NormalizeArenaSeed(candidate);
        }
        throw new InvalidOperationException("Unable to allocate unique arena ID.");
    }

    /// <summary>
    /// Derives a stable arena-ID seed from output label + seed.
    /// This keeps IDs unique across separate processes for distinct output paths.
    /// </summary>
    public static uint DeriveArenaSeed(string label, uint seed)
    {
        string safe = string.IsNullOrWhiteSpace(label) ? "<unknown-output>" : label;
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"{safe}|{seed:X8}"));
        uint mixed = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(0, 4)) ^ seed;
        return NormalizeArenaSeed(mixed);
    }

    /// <summary>
    /// Returns a process-unique 64-bit GUID-like value.
    /// Avoids reserved values 0 and ulong.MaxValue.
    /// </summary>
    public static ulong AcquireGuid64(ulong seed)
    {
        ulong candidate = NormalizeGuidSeed(seed);
        for (int i = 0; i < int.MaxValue; i++)
        {
            if (Guid64Ids.TryAdd(candidate, 0))
                return candidate;
            candidate = NextUInt64(candidate);
            candidate = NormalizeGuidSeed(candidate);
        }
        throw new InvalidOperationException("Unable to allocate unique 64-bit ID.");
    }

    private static uint NormalizeArenaSeed(uint v)
    {
        if (v == 0 || v == 0xFFFFFFFFu)
            return 0xA5A5A5A5u;
        return v;
    }

    private static ulong NormalizeGuidSeed(ulong v)
    {
        if (v == 0 || v == ulong.MaxValue)
            return 0xA5A5A5A5A5A5A5A5UL;
        return v;
    }

    private static uint NextUInt32(uint x)
    {
        // xorshift32 probe sequence
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    private static ulong NextUInt64(ulong x)
    {
        // xorshift64* probe sequence
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        return x * 2685821657736338717UL;
    }

    /// <summary>
    /// Clears process-wide ID tables so memory from a large build can be reclaimed after export.
    /// Safe to call between builds; next <see cref="AcquireArenaId"/> / <see cref="AcquireGuid64"/> repopulates as needed.
    /// </summary>
    public static void Reset()
    {
        ArenaIds.Clear();
        Guid64Ids.Clear();
    }
}
