using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Small bin-pool blob builders. Each returns the bytes for one inline
/// struct that gets dropped into the BinPool via `binPool.AddBlob(...)` and
/// referenced from a CollectionAttribute via PtrN fixup. Blob shapes are
/// pinned to retail-verified bytes — a 16- vs 24-byte mismatch crashes the
/// engine on load (see comments per helper for the bug history).
public static class VltBinHelpers
{
    /// 8-byte ArrayData header: count(2)=0, capacity(2)=0, typeSize(2),
    /// align(2)=0. Stock empty arrays (e.g. `dlc_mapping/<pack>.unlocks`)
    /// use this exact shape. typeSize MUST match the schema (Sk8::BE::tUnlockableItemInfo
    /// is 8, RefSpec is 24, etc.) — the engine's array walker uses it for stride.
    public static byte[] BuildEmptyArrayHeader(ushort typeSize) =>
        VltPayload.Build(w =>
        {
            w.WriteBE((ushort)0);     // count
            w.WriteBE((ushort)0);     // capacity
            w.WriteBE(typeSize);
            w.WriteBE((ushort)0);     // align flag (0 = 8-byte unaligned)
        });

    /// <summary>
    /// 4-element <c>Attrib::RefSpec[]</c> for <c>world.DMOBanks</c>, byte-identical to retail
    /// Danny Way <c>world/dlc_dw_megacompound</c> (Attrib dump:
    /// <c>documentation/AttribXMLDump_Dannyway/dlc_danny_way_park/Dump/Skate3_skater/Collections/world/dlc_dw_megacompound.xml</c>).
    /// Each entry is class <c>dmo_banks</c> (Lookup8 <c>0x9F0D024CD713A9DC</c>) plus the four collection-key hashes DW ships.
    /// </summary>
    /// <remarks>
    /// Custom DLC manifests previously used an empty array; the engine still resolves these
    /// RefSpecs against loaded <c>dmo_banks</c> rows (base game + DLC). Reuse one blob for
    /// every map in a multi-map package until per-map <c>dmo_banks</c> authoring exists.
    /// </remarks>
    public static byte[] BuildDannyWayMegacompoundDmoBanksArray() => (byte[])DannyWayMegacompoundDmoBanks.Clone();

    private static readonly byte[] DannyWayMegacompoundDmoBanks = Convert.FromHexString(
        "0004000400180000" + // count=4, capacity=4, typeSize=24 (RefSpec), align=0
        "9F0D024CD713A9DC316A03931530B5E60000000000000000" +
        "9F0D024CD713A9DC647C0AA898F5BFDF0000000000000000" +
        "9F0D024CD713A9DC4630E94E4119A5B60000000000000000" +
        "9F0D024CD713A9DC520C455A5CCD67760000000000000000");

    /// 16-byte typed ClassRefSpec used for inline ClassRef attributes (NF=0x08)
    /// whose type is `Attrib::Gen::ClassRefSpec_<class>`. The class is implicit
    /// in the field's TypeName, so on disk we ship ONLY {key_hash, cache_slot=0}.
    /// Verified against retail rows: 16 bytes = 8B key_hash + 8B cache slot.
    public static byte[] BuildClassRefSpec(string key) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(key));   // 8B key hash
            w.WriteBE(0UL);                         // 8B cache slot (zero on disk)
        });

    /// 16-byte ClassRef16 — 1-element ClassRefSpec array shape used for
    /// fe_locations.WorldRef and similar single-target references. 8B class
    /// hash (implicit "world") + 8B cache slot.
    public static byte[] BuildClassRef16(string key) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(key));
            w.WriteBE(0UL);
        });

    /// 24-byte <c>Attrib::RefSpec</c> (<c>sizeof == 0x18</c> in IDA): on the vault / bin pool
    /// we emit three big-endian u64s — <c>mClassKey</c>, <c>mCollectionKey</c>, then
    /// <c>mCollectionPtr == 0</c> (no resolved collection pointer on disk). Lookup8 hashes
    /// of the class/key strings fill the first two slots. Distinct from
    /// <c>ClassRefSpec_&lt;class&gt;</c> (16B). Confused 16B vs 24B once and crashed at PPU 0x73F750.
    public static byte[] BuildRefSpec24(string className, string key) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(className)); // mClassKey
            w.WriteBE(Lookup8Hashing.Hash(key));       // mCollectionKey
            w.WriteBE(0UL);                           // mCollectionPtr (null)
        });

    /// Same on-disk shape as <see cref="BuildRefSpec24(string,string)"/>, with precomputed
    /// Lookup8 values for <c>mClassKey</c> / <c>mCollectionKey</c> (retail keys not in our corpus).
    public static byte[] BuildRefSpec24(ulong classLookup8Hash, ulong keyLookup8Hash) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(classLookup8Hash);
            w.WriteBE(keyLookup8Hash);
            w.WriteBE(0UL);
        });

    /// Lookup8 <c>mCollectionKey</c> for <c>ribbon_indicator</c> on retail in-challenge / path slots
    /// (race + Danny Way <c>ots_dwmc_01</c> elems 2–4, etc.). OTS <c>_chev_*</c> VisualIndicators use this hash;
    /// <c>_vis_*</c> defaults to string key <c>arrow</c>.
    public const ulong RibbonIndicatorSecondarySpotKeyHash = 0xD6C61BC5BC0F285AUL;

    /// 12-byte tCompiledLua descriptor:
    ///   +0x00 u32 BE src_offset      (PtrN-fixed at runtime)
    ///   +0x04 u32 BE 0  (padding)
    ///   +0x08 u32 BE nul_offset      (PtrN-fixed at runtime)
    /// Verified against MinimalDlcBuilder/DlcBuilder.cs:4651. The engine reads
    /// the descriptor as 3 × u32 BE in this exact layout — anything else
    /// breaks bin-pool offset alignment for the next blob and may cause the
    /// engine to validate the padding slot as zero.
    public static byte[] BuildLuaDescriptor(uint srcOffset, uint nulOffset) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(srcOffset);
            w.WriteBE(0u);
            w.WriteBE(nulOffset);
        });

    /// 12-byte FE layout struct: u32 worldNamePtr + u32 spawnPtr + u32 locHalPtr.
    /// All three slots get PtrN fixups; on disk the values mirror the bin
    /// offsets (later patched to absolute pointers).
    public static byte[] BuildFeLayout(uint worldNamePtr, uint spawnPtr, uint locHalPtr) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(worldNamePtr);
            w.WriteBE(spawnPtr);
            w.WriteBE(locHalPtr);
        });

    /// 32-byte `Sk8::FE::tMapInfo` blob. Verified vs MinimalDlcBuilder
    /// /DlcBuilder.cs:4631-4642 and retail world rows across stock + every
    /// shipping DLC.
    /// Layout (all big-endian u32 = 8 fields × 4 bytes):
    ///   [0x00] reserved/keyPtr   (PtrN-patched at runtime to map texture key)
    ///   [0x04] boundMaxX (signed)
    ///   [0x08] boundMinX (signed, typically negative)
    ///   [0x0C] boundMaxY (signed)
    ///   [0x10] boundMinY (signed, typically negative)
    ///   [0x14] tileX (e.g. 512 / 1152)
    ///   [0x18] tileY (e.g. 512 / 640)
    ///   [0x1C] reserved
    /// Earlier shape stored bounds/tiles as `ushort` (2B each) producing a
    /// 28-byte blob, which shifted every subsequent BIN offset by 4 bytes
    /// and made the engine's array walker read 4 bytes of the next blob as
    /// trailing pad of this struct.
    public static byte[] BuildTMapInfo(int boundMaxX, int boundMinX, int boundMaxY, int boundMinY, int tileX, int tileY) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(0u);                  // [0x00] reserved/keyPtr (PtrN-patched)
            w.WriteBE((uint)boundMaxX);     // [0x04]
            w.WriteBE((uint)boundMinX);     // [0x08]
            w.WriteBE((uint)boundMaxY);     // [0x0C]
            w.WriteBE((uint)boundMinY);     // [0x10]
            w.WriteBE((uint)tileX);         // [0x14]
            w.WriteBE((uint)tileY);         // [0x18]
            w.WriteBE(0u);                  // [0x1C] reserved
        });

    /// 24-byte `world` row MapCategory ref. Verified against retail DW
    /// `dlc_danny_way_park.bin` @ 0x848 (immediately before `MountBigFile`):
    /// {Lookup8(mapCategoryKey), 0, 0x0001000100100000}. The trailing
    /// constant is a schema sentinel — DO NOT change.
    public static byte[] BuildMapCategoryWorldRef24(string mapCategoryKey) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(mapCategoryKey));
            w.WriteBE(0UL);
            w.WriteBE(0x0001000100100000UL);
        });

    /// 24-byte `fe_locations` row MapCategory ref. Verified against retail DW
    /// @ 0x730: {Lookup8(mapCategoryKey), 0, Lookup8(worldDistKey)}. The
    /// trailing hash differs per-map so the FE listing can disambiguate
    /// entries that share the same category.
    public static byte[] BuildMapCategoryFeLocationsRef24(string mapCategoryKey, string worldDistKey) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(mapCategoryKey));
            w.WriteBE(0UL);
            w.WriteBE(Lookup8Hashing.Hash(worldDistKey));
        });

    /// 24-byte `Attrib::Gen::ClassRefSpec_*` blob (extended form): BE u64
    /// Lookup8(collectionKey), middle qword zero, BE u64 extension (often
    /// another Lookup8 or sentinel `1`). Used on OTS `challenge_global_data`
    /// rows for MapCategory ("ots" + Hash_…0001) and World ("bam" + fe_locations).
    public static byte[] BuildClassRefSpecExtended24(string collectionKey, ulong extensionThird) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(collectionKey));
            w.WriteBE(0UL);
            w.WriteBE(extensionThird);
        });

    /// `Sk8::Challenge::tChallengeIconDefinition` payload — two BE u64s padded
    /// to 36 bytes. Padding matters: the link helper at retail CIA `0x73558`
    /// issues `lwz r0, 0x20(r3)` against the attribute's resolved pointer,
    /// and back-to-back 16-byte payloads make that load see the next icon's
    /// first word and crash dereferencing it. ≥36 bytes guarantees zeros at
    /// offset +0x20.
    public static byte[] BuildChallengeIconDefinitionPadded(ulong firstQword, ulong secondQword) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(firstQword);
            w.WriteBE(secondQword);
            w.Write(new byte[20]);   // 16 + 20 = 36B
        });

    /// Danny Way `challenge_global_data/dlc_dwgh_own_the_spots.xml`:
    /// TypeSize=24 = two BE u64 icon words + 8 zero bytes (see ChallengeIcon hex).
    /// Distinct from <see cref="BuildChallengeIconDefinitionPadded"/> used where
    /// extra isolation from adjacent pool entries is required.
    public static byte[] BuildChallengeIconDefinitionDw24(ulong firstQword, ulong secondQword) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(firstQword);
            w.WriteBE(secondQword);
            w.WriteBE(0u);
            w.WriteBE(0u);
        });

    /// 1-element ClassRefSpec_progression_state array. 8B header + 16B payload.
    /// Used for state-graph StateNodes pointing at a single complete state.
    public static byte[] BuildSingleProgressionStateRefArray(string stateKey) =>
        VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)16);
            w.WriteBE((ushort)0);
            w.WriteBE(Lookup8Hashing.Hash(stateKey));
            w.WriteBE(0UL);
        });

    /// N-element ClassRefSpec_progression_state array. Used for StateNodes
    /// fields aggregating multiple per-OTS complete states. Empty key list
    /// returns the canonical 8-byte empty array header.
    public static byte[] BuildProgressionStateRefArray(IReadOnlyList<ulong> stateKeyHashes)
    {
        ArgumentNullException.ThrowIfNull(stateKeyHashes);
        if (stateKeyHashes.Count == 0) return BuildEmptyArrayHeader(16);
        return VltPayload.Build(w =>
        {
            ushort count = (ushort)stateKeyHashes.Count;
            w.WriteBE(count);
            w.WriteBE(count);
            w.WriteBE((ushort)16);
            w.WriteBE((ushort)0);
            foreach (ulong h in stateKeyHashes)
            {
                w.WriteBE(h);
                w.WriteBE(0UL);
            }
        });
    }

    /// 1-element tObservatoryTrigger array. 8B header + 24B payload:
    /// {u32 type, u32 pad, u64 target_hash, u64 pad}. type=1 = OnEvent
    /// (single-target dispatch); target_hash points at the
    /// progression_group/state row to fire.
    public static byte[] BuildSingleObservatoryTrigger(uint triggerType, ulong targetHash) =>
        VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)1);
            w.WriteBE((ushort)24);
            w.WriteBE((ushort)0);
            w.WriteBE(triggerType);
            w.WriteBE(0u);
            w.WriteBE(targetHash);
            w.WriteBE(0UL);
        });

    /// 1-element typed RefSpec[] blob. 8B header (count=1, cap=1, typeSize=24,
    /// align=0) + 24B RefSpec (Lookup8(className) + Lookup8(key) + 8B cache
    /// slot). Used by ObjectivesOwned/ObjectivesKilledIt to link OTS rows to
    /// their per-tier objectives_group rows. typeSize=24 is mandatory — empty
    /// or zero-typed headers leave the engine's array walker computing bogus
    /// element strides during inheritance walks.
    public static byte[] BuildSingleRefSpecArray(string className, string key) =>
        VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);   // count
            w.WriteBE((ushort)1);   // capacity
            w.WriteBE((ushort)24);  // typeSize (RefSpec is 24B)
            w.WriteBE((ushort)0);   // align
            w.WriteBE(Lookup8Hashing.Hash(className));
            w.WriteBE(Lookup8Hashing.Hash(key));
            w.WriteBE(0UL);
        });

    /// 16-byte `Sk8::Challenge::tTriggerVolumeInstanceID`. Bytes [0..7] are
    /// Sk8::Challenge::tTriggerVolumeInstanceID (IDA sizeof=0x10): VolumeName + pad + u64 VolumeID @ +8.
    /// VolumeName ptr + padding (PtrN-fixed at load); bytes [8..15] are the
    /// uint64 BE VolumeID. The engine's cTriggerVolumeManager keys on the
    /// VolumeID field — it MUST equal the matching tTriggerInstance's
    /// m_uiGuidLocal in the cSim_Global PSG for the bind to succeed.
    public static byte[] BuildTriggerVolumeIdStruct(ulong volumeId) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(0u);          // VolumeName ptr (PtrN-fixed)
            w.WriteBE(0u);          // padding
            w.WriteBE(volumeId);    // VolumeID — engine's resolution key
        });

    /// 32-byte tVaultedRefSpec built from class + key strings. Layout matches
    /// the retail shape: 8B Lookup8(className) + 8B Lookup8(key) + 16B zero
    /// cache slot. The +0x18 slot is a path-string ptr that callers PtrN-fix
    /// to a `"<class>\<key>.vlt"` string in the bin pool — engine's
    /// sub_A6D400 path-dispatch reads it and constructs `"data/db/<that>"`.
    public static byte[] BuildVaultedRefSpec(string className, string rowKey) =>
        VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash(className));
            w.WriteBE(Lookup8Hashing.Hash(rowKey));
            w.WriteBE(0UL);
            w.WriteBE(0UL);
        });

    /// 8-byte ArrayData header + a single UInt16 element. Used for the
    /// `dlc_mapping.Hash_481D3FE849C4D988` slot-index array — claims one DLC
    /// slot (DLC001..DLC020) by storing its slot number. Engine sets the
    /// matching bit in `dlc_mask`; the Online → Freeskate menu filters by it.
    /// Verified retail values: DW=10, ArtGallery=17, Creator=15.
    public static byte[] BuildSingleUInt16ArrayBlob(ushort value) =>
        VltPayload.Build(w =>
        {
            w.WriteBE((ushort)1);     // count
            w.WriteBE((ushort)1);     // capacity
            w.WriteBE((ushort)2);     // type size = sizeof(UInt16)
            w.WriteBE((ushort)0);     // align flag
            w.WriteBE(value);
            w.WriteBE((ushort)0);     // 2-byte tail pad to 8B
        });
}
