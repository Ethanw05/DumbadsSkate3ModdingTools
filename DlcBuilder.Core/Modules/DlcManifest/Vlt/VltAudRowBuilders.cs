using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Per-class row builders for the audio + worldpainter + progression-summary
/// classes that the main `world` row pulls in by reference. Without these
/// rows the engine's audio bigfile registry / freeskate trigger pool /
/// FE summary panel either returns null on per-DLC lookups or leaves the
/// audio buffer pool slots in a partially-claimed state — verified retail
/// behavior across DW + AG + Creator.
public static class VltAudRowBuilders
{
    /// `aud_worlddata/<distKey>` — engine audio sees the world. Two attrs:
    /// Emitter_Files (empty Text array) + NIS_Speech (zero RefSpec24).
    public static CollectionBlob BuildAudWorldDataCollection(
        string distKey,
        uint emitterFilesArrayOff,
        uint nisSpeechRefOff)
    {
        return VltCollectionBuilder.BuildCollection(
            "aud_worlddata", distKey, "default", layoutOffset: 0u,
            new CollectionAttributeSpec[]
            {
                VltAttribute.PointerNoFixup("Emitter_Files", "EA::Reflection::Text", emitterFilesArrayOff, 0x02),
                VltAttribute.PointerNoFixup("NIS_Speech",    "Attrib::RefSpec",      nisSpeechRefOff,    0x08),
            },
            explicitTypes: new[] { "EA::Reflection::Text", "Attrib::RefSpec" });
    }

    /// `aud_bigfiles/<distKey>` — registers the audio bigfile descriptor.
    /// Without this row the engine's audio cleanup fails to find the per-DLC
    /// audio state to release, leaving buffer pool slots partially-claimed
    /// across world transitions. We point at empty strings (no DLC-specific
    /// audio bigfile) so cleanup walks the chain and finds zeros.
    public static CollectionBlob BuildAudBigfilesCollection(
        string distKey,
        uint defaultAudioPathPtr,
        uint emptyStringPtr)
    {
        return VltCollectionBuilder.BuildCollection(
            "aud_bigfiles", distKey, "default", layoutOffset: 0u,
            new CollectionAttributeSpec[]
            {
                VltAttribute.Inline("Default_Big", "EA::Reflection::Text", defaultAudioPathPtr),
                // The three Hash_… attributes are file-name fields stock
                // packs use to reference per-DLC audio bigfiles; we ship
                // empty strings since we don't bundle our own audio bigfile.
                VltAttribute.InlineRawHash("Hash_12DFB2BDD9356640", "EA::Reflection::Text", emptyStringPtr, 0x12DFB2BDD9356640UL),
                VltAttribute.InlineRawHash("Hash_1179AE4EB7022F99", "EA::Reflection::Text", emptyStringPtr, 0x1179AE4EB7022F99UL),
                VltAttribute.InlineRawHash("Hash_CBDF09A8387553E2", "EA::Reflection::Text", emptyStringPtr, 0xCBDF09A8387553E2UL),
                VltAttribute.Inline("Is_DLC_NIS",  "EA::Reflection::Bool",  0u),
                VltAttribute.Inline("Is_NIS",      "EA::Reflection::Bool",  0u),
                VltAttribute.Inline("NIS_Bank_ID", "EA::Reflection::Int32", 0u),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "EA::Reflection::Bool",
                "EA::Reflection::Int32",
            },
            numTypesDup: 4);
    }

    /// `worldpainter_fe_location_triggers/<distKey>` — registers freeskate
    /// trigger volumes into the engine's DynamicHullManager pool. Without
    /// this row the freeskate gate at retail `sub_22D078` returns 0 →
    /// `ID_ONLINE_ERROR_NO_FREESKATE_AREA`. Schema requires Types=[Text]
    /// (length 1) with NumTypesDup=2.
    public static CollectionBlob BuildWorldPainterFeLocationTriggersCollection(
        string distKey,
        uint layoutZero40Off)
    {
        return VltCollectionBuilder.BuildBareCollection(
            "worldpainter_fe_location_triggers",
            distKey,
            "default_dlc",
            layoutOffset: layoutZero40Off,
            explicitTypes: new[] { "EA::Reflection::Text" },
            numTypesDup: 2);
    }

    /// `progression_summary_group/<frameworkKey>` — anchor row registering
    /// the framework with the FE summary panel. 0 attrs.
    public static CollectionBlob BuildProgressionSummaryGroupAnchor(string frameworkKey)
    {
        return VltCollectionBuilder.BuildBareCollection(
            "progression_summary_group", frameworkKey, "default");
    }

    /// `progression_summary_group/<slug>_dlc` — per-map summary entry. 4 attrs:
    /// ChallengeGroup (empty array), group_order (Int16), Hash_786255B8FF426A3D
    /// (icon ClassRef→teleport), Title (HAL).
    public static CollectionBlob BuildProgressionSummaryGroupEntry(
        string entryKey,
        string parentFrameworkKey,
        uint emptyChallengeGroupArrayOff,
        uint iconRef16Off,
        uint titlePtr,
        ushort groupOrder)
    {
        // group_order is Int16 stored at the MSB of a UInt32 BE.
        uint groupOrderData = (uint)groupOrder << 16;
        return VltCollectionBuilder.BuildCollection(
            "progression_summary_group", entryKey, parentFrameworkKey, layoutOffset: 0u,
            new CollectionAttributeSpec[]
            {
                // NF=0x0A = RefSpec[] — NOT NF=0x02. Retail packs all use 0x0A;
                // 0x02 makes the engine walk the bin payload as a flat array
                // of 16-byte structs instead of pointer-resolving each entry.
                VltAttribute.PointerNoFixup("ChallengeGroup", "Attrib::Gen::ClassRefSpec_progression_group", emptyChallengeGroupArrayOff, 0x0A),
                VltAttribute.Inline("group_order", "EA::Reflection::Int16", groupOrderData),
                VltAttribute.PointerRawHash("Hash_786255B8FF426A3D", "Attrib::Gen::ClassRefSpec_fe_map_icons", iconRef16Off, 0x786255B8FF426A3DUL),
                VltAttribute.Pointer("Title", "EA::Reflection::Text", titlePtr),
            },
            explicitTypes: new[]
            {
                "Attrib::Gen::ClassRefSpec_progression_group",
                "EA::Reflection::Int16",
                "Attrib::Gen::ClassRefSpec_fe_map_icons",
                "EA::Reflection::Text",
            });
    }
}
