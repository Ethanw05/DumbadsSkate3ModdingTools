using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Per-class row builders. Each takes the bin-pool pointer offsets it needs
/// (caller has already allocated the strings/blobs in the BinPool) and returns
/// a fully-formed `CollectionBlob`. Field shapes pinned to retail-verified
/// bytes; comments preserve the audit history.
public static class VltRowBuilders
{
    /// `world/<distKey>` — the per-map world row. 14 attributes spanning
    /// audio data, FE map info, sky pair, big mount, world stream. Verified
    /// byte-for-byte against retail Danny Way `world/dlc_dw_megacompound`.
    /// All five "iter 22" attributes (AudioData, DMOBanks, FEMapIconOffset,
    /// SkyBoxModel, SkyBoxTexture) are non-negotiable — without them the
    /// FE locator scan rejects the world and `Challenges_FilterFreeskateList`
    /// drops every freeskate row referencing it.
    public static CollectionBlob BuildWorldCollection(
        DlcSpec spec,
        uint namePtr,
        uint halPtr,
        uint hash95Ptr,
        uint shortPtr,
        uint bigPtr,
        uint streamPtr,
        uint mapCategoryRef,
        uint feMapInfoOff,
        uint skyBoxModelPtr,
        uint skyBoxTexturePtr,
        uint audioDataOff,
        uint dmoBanksOff,
        uint feMapIconOffsetOff)
    {
        // Schema declaration — engine validates row schema against this list at boot.
        // Verified against DW dlc_danny_way_park.vlt @ 0x588: NumTypes=6 here
        // (we pass 6 explicit names; no NumTypesDup padding needed).
        string[] worldTypes =
        {
            "EA::Reflection::Text",
            "Attrib::RefSpec",
            "Attrib::Types::Vector2",
            "Sk8::FE::tMapInfo",
            "Attrib::Gen::ClassRefSpec_map_category",
            "EA::Reflection::Bool",
        };

        return VltCollectionBuilder.BuildCollection(
            "world", spec.DistKey, "default_dlc", layoutOffset: 0u,
            new CollectionAttributeSpec[]
            {
                VltAttribute.Pointer("Name", "EA::Reflection::Text", namePtr),
                // Dedicated 24-byte zero RefSpec blob (NOT bin offset 0 which
                // holds StrE chunk header). Engine reads cache slot zeros as
                // NULL → "no link" → skipped instead of dereferencing garbage.
                VltAttribute.PointerNoFixup("AudioData", "Attrib::RefSpec", audioDataOff, 0x08),
                VltAttribute.PointerNoFixup("DMOBanks", "Attrib::RefSpec", dmoBanksOff, 0x0A),
                // FEMapIconOffset (Vector2). NF=0x00 inline-struct-pointer with
                // PtrN entry pointing at an 8-byte zero blob. AttrPointer routes
                // through NfForType("Vector2")=0x00 and AttributeNeedsPtrN registers
                // the fixup automatically.
                VltAttribute.Pointer("FEMapIconOffset", "Attrib::Types::Vector2", feMapIconOffsetOff),
                VltAttribute.Pointer("FEMapInfo", "Sk8::FE::tMapInfo", feMapInfoOff),
                VltAttribute.Pointer("HALName", "EA::Reflection::Text", halPtr),
                VltAttribute.Pointer("MapCategory", "Attrib::Gen::ClassRefSpec_map_category", mapCategoryRef),
                VltAttribute.Inline("MountBigFile", "EA::Reflection::Bool", 0u),
                VltAttribute.PointerRawHash("Hash_95D7C0CA40A34EA", "EA::Reflection::Text", hash95Ptr, 0x95D7C0CA40A34EAUL),
                VltAttribute.Pointer("ShortName", "EA::Reflection::Text", shortPtr),
                VltAttribute.Pointer("SkyBoxModel", "EA::Reflection::Text", skyBoxModelPtr),
                VltAttribute.Pointer("SkyBoxTexture", "EA::Reflection::Text", skyBoxTexturePtr),
                VltAttribute.Pointer("WorldBigFile", "EA::Reflection::Text", bigPtr),
                VltAttribute.Pointer("WorldStream", "EA::Reflection::Text", streamPtr),
            },
            explicitTypes: worldTypes);
    }

    /// `fe_locations/<distKey>` — the per-map FE menu row.
    /// Verified against retail DW: NumTypes=5, NumTypesDup=6 (one zero-padded
    /// schema slot the engine reserves for `tLocationID`). Without the full
    /// schema declaration the row gets dropped from the per-DLC content registry
    /// and the world becomes invisible online.
    public static CollectionBlob BuildFeLocationsCollection(
        DlcSpec spec,
        uint layoutOffset,
        uint descPtr,
        uint imagePtr,
        uint catRef,
        uint worldRef)
    {
        string[] feLocationTypes =
        {
            "EA::Reflection::Text",
            "Sk8::Challenge::tLocationID",
            "Attrib::Gen::ClassRefSpec_map_category",
            "EA::Reflection::Bool",
            "Attrib::Gen::ClassRefSpec_world",
        };

        return VltCollectionBuilder.BuildCollection(
            "fe_locations", spec.DistKey, "default_dlc", layoutOffset,
            new CollectionAttributeSpec[]
            {
                VltAttribute.Pointer("Description", "EA::Reflection::Text", descPtr),
                VltAttribute.Pointer("Image", "EA::Reflection::Text", imagePtr),
                VltAttribute.Pointer("MapCategory", "Attrib::Gen::ClassRefSpec_map_category", catRef),
                VltAttribute.Inline("Unlocked", "EA::Reflection::Bool", 0x01000000u),
                VltAttribute.Pointer("World", "Attrib::Gen::ClassRefSpec_world", worldRef),
            },
            explicitTypes: feLocationTypes,
            numTypesDup: 6);
    }

    /// `map_category/<key>` — the parent map_category row.
    public static CollectionBlob BuildMapCategoryCollection(
        string categoryKey,
        string parentKey,
        uint namePtr,
        uint displayHalPtr,
        uint sortKey)
    {
        return VltCollectionBuilder.BuildCollection(
            "map_category", categoryKey, parentKey, layoutOffset: 0u,
            new CollectionAttributeSpec[]
            {
                VltAttribute.Pointer("Name", "EA::Reflection::Text", namePtr),
                VltAttribute.Pointer("DisplayText", "EA::Reflection::Text", displayHalPtr),
                VltAttribute.Inline("SortKey", "EA::Reflection::Int32", sortKey),
            });
    }

    /// `map_filter/<key>` — selects what shows up in a map menu filter chip.
    /// `parent="default_dlc"` for the main offline filter; `parent="freeskate_locations"`
    /// for the online-freeskate variant. The latter must ship IF=0 on every
    /// attribute — `exactInstanceFlags=true` (default) handles that.
    public static CollectionBlob BuildMapFilterCollection(
        string key,
        string parent,
        uint namePtr,
        uint helperPtr,
        uint filterDescOff,
        uint halIdPtr,
        bool exactInstanceFlags = true)
    {
        CollectionAttributeSpec[] attrs = exactInstanceFlags
            ? new CollectionAttributeSpec[]
            {
                VltAttribute.Inline("Name", "EA::Reflection::Text", namePtr),
                VltAttribute.Inline("FEHelperText", "EA::Reflection::Text", helperPtr),
                // tCompiledLua inline-struct (NF=0x00) — the pointer ends up in the
                // bin via PtrN; on disk `data` is the bin offset of the descriptor.
                new CollectionAttributeSpec("Filter", "LuaState::tCompiledLua", filterDescOff, 0x00, 0x00, null),
                VltAttribute.Inline("HalID", "EA::Reflection::Text", halIdPtr),
            }
            : new CollectionAttributeSpec[]
            {
                VltAttribute.Pointer("Name", "EA::Reflection::Text", namePtr),
                VltAttribute.Pointer("FEHelperText", "EA::Reflection::Text", helperPtr),
                VltAttribute.Pointer("Filter", "LuaState::tCompiledLua", filterDescOff),
                VltAttribute.Pointer("HalID", "EA::Reflection::Text", halIdPtr),
            };

        return VltCollectionBuilder.BuildCollection(
            "map_filter", key, parent, layoutOffset: 0u, attrs,
            exactInstanceFlags: exactInstanceFlags);
    }

    /// `map_listing/<key>` — the actual menu entry (icon/title/helper) the
    /// player clicks. `parent="progression_locations"` for offline,
    /// `parent="online_freeskate"` for the online variant.
    public static CollectionBlob BuildMapListingCollection(
        string key,
        string parent,
        uint listingRefOff,
        ushort ordering,
        bool exactInstanceFlags = true)
    {
        CollectionAttributeSpec[] attrs = exactInstanceFlags
            ? new CollectionAttributeSpec[]
            {
                VltAttribute.PointerNoFixup("Listing", "Attrib::RefSpec", listingRefOff, 0x08),
                VltAttribute.Inline("Ordering", "EA::Reflection::Int16", (uint)ordering << 16),
            }
            : new CollectionAttributeSpec[]
            {
                VltAttribute.Pointer("Listing", "Attrib::RefSpec", listingRefOff),
                VltAttribute.Inline("Ordering", "EA::Reflection::Int16", (uint)ordering << 16),
            };

        return VltCollectionBuilder.BuildCollection(
            "map_listing", key, parent, layoutOffset: 0u, attrs,
            exactInstanceFlags: exactInstanceFlags);
    }

    /// `dlc_mapping/pack_<slug>` — the DLC pack manifest row. Stock retail
    /// always ships one (parent=default) per pack: product_id (16-char ASCII
    /// matching the install folder), three empty unlock arrays, and a
    /// 1-element UInt16 array under Hash_481D3FE849C4D988 carrying the DLC
    /// slot index (DW=10, AG=17, Creator=15).
    public static CollectionBlob BuildDlcMappingCollection(string packageSlug, BinPoolBuilder bin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSlug);
        ArgumentNullException.ThrowIfNull(bin);

        string packKey = "pack_" + packageSlug.ToLowerInvariant();

        // product_id: 7 alphanumerics from the slug (uppercase) + 9 zeros.
        // Must EXACTLY match the DLC's install folder name —
        // dev_hdd0/game/BLUS30464/USRDIR/<folder>. A mismatch hides the area
        // from Online → Freeskate even when every other vault row is correct.
        string slugAlnum = new string(packageSlug.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        string productId = (slugAlnum.Length >= 7 ? slugAlnum.Substring(0, 7) : slugAlnum)
            .PadRight(16, '0').Substring(0, 16);
        uint productIdPtr = bin.AddString(productId);

        // DLC slot index 11 — between DW (10) and Creator (15), unclaimed by
        // any reverse-engineered shipping content.
        const ushort DlcSlotIndex = 11;
        uint hashArrayOff = bin.AddBlob(VltBinHelpers.BuildSingleUInt16ArrayBlob(DlcSlotIndex));
        uint assetUnlocksOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(8));
        uint progressionUnlocksOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(8));
        uint unlocksOff = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(8));

        return VltCollectionBuilder.BuildCollection(
            "dlc_mapping", packKey, "default", layoutOffset: 0u,
            new CollectionAttributeSpec[]
            {
                VltAttribute.EmptyArrayRawHash("Hash_481D3FE849C4D988", "EA::Reflection::UInt16", hashArrayOff, 0x481D3FE849C4D988UL),
                VltAttribute.EmptyArray("asset_unlocks", "Sk8::BE::tUnlockableItemInfo", assetUnlocksOff),
                VltAttribute.Pointer("product_id", "EA::Reflection::Text", productIdPtr),
                VltAttribute.EmptyArray("progression_unlocks", "Sk8::BE::tUnlockableItemInfo", progressionUnlocksOff),
                VltAttribute.EmptyArray("unlocks", "Sk8::BE::tUnlockableItemInfo", unlocksOff),
            },
            // Retail DW writes NumTypes=3, NumTypesDup=4 — the 4th slot is
            // zero-padded, engine reserves it for schema validation.
            numTypesDup: 4);
    }
}
