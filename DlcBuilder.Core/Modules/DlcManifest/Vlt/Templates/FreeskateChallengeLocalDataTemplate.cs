using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt.Templates;

/// `challenge_local_data/default` template VLT — the foundational 45-attr row
/// every freeskate / OTS challenge inherits from. Empty (no rows) makes the
/// engine's unload pass leave audio/physics buffers dangling because there's
/// no parseable row to walk on cleanup. Match retail byte-for-byte.
///
/// Returns (.vlt + .bin) bytes for `default.vlt` / `default.bin` (caller picks
/// the on-disk filename).
public static class FreeskateChallengeLocalDataTemplate
{
    public sealed record TemplateArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static TemplateArtifacts Build(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        var collections = new List<CollectionBlob>();
        AppendDefaultHash0Row(bin, collections);

        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, Array.Empty<(uint, uint)>());
        byte[] binBytes = bin.BuildBinFile();
        return new TemplateArtifacts(fileName, vltBytes, binBytes);
    }

    /// Appends the 45-attr default/Hash_0 row to the caller's bin + collections.
    /// MUST be invoked before any other strings/blobs are added to the bin pool —
    /// it depends on a precise byte layout for the leading strings + 16B pad +
    /// tail blob landing at exactly 0x60.
    public static void AppendDefaultHash0Row(BinPoolBuilder bin, List<CollectionBlob> collections)
    {
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(collections);

        // Strings — byte-positions match retail Danny Way's bin exactly:
        //   0x08 = "ID_CHALLENGE_CENTRAL_MESSAGE_OBJECTIVE_RETURNTOCHAL"
        //   0x3C = ""
        //   0x3D = "ID_COMMON_NONE"
        //   0x4C/4D/4E/4F = "" (4 sentinel empties)
        bin.AddString("ID_CHALLENGE_CENTRAL_MESSAGE_OBJECTIVE_RETURNTOCHAL"); // 0x08
        bin.AddString("");                                                    // 0x3C
        bin.AddString("ID_COMMON_NONE");                                      // 0x3D
        bin.AddString("");                                                    // 0x4C
        bin.AddString("");                                                    // 0x4D
        bin.AddString("");                                                    // 0x4E
        bin.AddString("");                                                    // 0x4F

        // 16-byte zero pad — matches the gap retail leaves between StrE chunk
        // end (0x50) and the trailing tail block (0x60). Without this the tail
        // blob lands at 0x50 instead of 0x60 and every attribute Data offset
        // drifts by 0x10.
        bin.AddBlob(new byte[16]);

        uint tailOff = bin.AddBlob(FreeskateLocationsTailBlob.Bytes);
        if (tailOff != 0x60u)
            throw new InvalidOperationException(
                $"Tail blob landed at 0x{tailOff:X4}, expected 0x60 — bin layout drifted.");

        // Type table (10 types) — order matches retail exactly so type indices
        // line up with the schema's expectations.
        string[] types =
        {
            "Sk8::Audio::eSk8Characters",                            // 0 (declared, unused)
            "EA::Reflection::Bool",                                  // 1
            "EA::Reflection::Int32",                                 // 2
            "EA::Reflection::Text",                                  // 3
            "Attrib::RefSpec",                                       // 4
            "Sk8::Audio::eChallengeMusicType",                       // 5
            "EA::Reflection::UInt8",                                 // 6
            "EA::Reflection::Float",                                 // 7
            "Attrib::Gen::ClassRefSpec_livingworld_categorygroups",  // 8
            "Sk8::Challenge::tObjectiveStatusField",                 // 9
        };

        var attrs = new[]
        {
            VltAttribute.Inline("AllowSecurityServiceToBeCalled",        "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("AllowSessionMarkersToBeUsedInWipeout",  "EA::Reflection::Bool",   0x01000000u),
            // Retail writes this with NF=0x02 (array-like) even though it's an Int32 with a
            // raw value — matching exactly avoids any schema-driven loader divergence.
            new CollectionAttributeSpec("AudioChallengeRoundOverride", "EA::Reflection::Int32", 100u, 0x02, 0x00, null),
            VltAttribute.InlineRawHash("Hash_117191727D1F3C41", "EA::Reflection::Bool", 0x01000000u, 0x117191727D1F3C41UL),
            VltAttribute.InlineRawHash("Hash_4C89D137B3D2944E", "EA::Reflection::Bool", 0u,         0x4C89D137B3D2944EUL),
            VltAttribute.InlineRawHash("Hash_F80366582AB8737F", "EA::Reflection::Bool", 0u,         0xF80366582AB8737FUL),
            VltAttribute.InlineRawHash("Hash_BEAFE2539CC67A68", "EA::Reflection::Bool", 0u,         0xBEAFE2539CC67A68UL),
            VltAttribute.InlineRawHash("Hash_D35023F4C43A6421", "EA::Reflection::Bool", 0u,         0xD35023F4C43A6421UL),
            VltAttribute.InlineRawHash("Hash_B01EAF6B0EA7373C", "EA::Reflection::Bool", 0u,         0xB01EAF6B0EA7373CUL),
            VltAttribute.Inline("CentralMessageHALIDReturnToChallengeArea", "EA::Reflection::Text", 0x08u),
            VltAttribute.PointerNoFixup("ChallengeCompleteForShowOnMap",     "Attrib::RefSpec", 0x70u, 0x0A),
            VltAttribute.PointerNoFixup("ChallengeCompleteForShowOnMiniMap", "Attrib::RefSpec", 0x78u, 0x0A),
            VltAttribute.Inline("ChallengeInfoShowCompetitors",        "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("ChallengeInfoShowDesc",               "EA::Reflection::Bool",   0x01000000u),
            VltAttribute.Inline("ChallengeInfoShowLocationImage",      "EA::Reflection::Bool",   0u),
            VltAttribute.InlineRawHash("Hash_D46D537AC3775C5D", "EA::Reflection::Bool", 0u,      0xD46D537AC3775C5DUL),
            VltAttribute.Inline("ChallengeMusicType",                  "Sk8::Audio::eChallengeMusicType", 0u),
            VltAttribute.Inline("ChallengePart",                       "EA::Reflection::Text",   0x3Cu),
            VltAttribute.Inline("ChallengeQuittable",                  "EA::Reflection::Bool",   0x01000000u),
            VltAttribute.Inline("ChallengeStage",                      "EA::Reflection::UInt8",  0u),
            VltAttribute.Inline("ChallengeUnlockableAward",            "EA::Reflection::Text",   0x3Cu),
            VltAttribute.Inline("ClearSessionMarkerOnExit",            "EA::Reflection::Bool",   0x01000000u),
            VltAttribute.Inline("CountdownDelay",                      "EA::Reflection::Float",  0x3FC00000u),  // 1.5f
            VltAttribute.Inline("DisableChallengeEntitiesOnStart",     "EA::Reflection::Bool",   0x01000000u),
            VltAttribute.Inline("DmoRedoOperationsOnRestart",          "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("IgnoreNewChallengeCount",             "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("IsDynamicCompetitorChallenge",        "EA::Reflection::Bool",   0u),
            VltAttribute.InlineRawHash("Hash_A4A69FC7384CBF93", "EA::Reflection::Float", 0x40533333u, 0xA4A69FC7384CBF93UL),
            VltAttribute.PointerNoFixup("LargeCrowdPopulationBreakdown", "Attrib::Gen::ClassRefSpec_livingworld_categorygroups", 0x80u, 0x08),
            VltAttribute.Inline("ObjectiveNotificationShownOnHUD",
                "Sk8::Challenge::tObjectiveStatusField", 0u),
            VltAttribute.Inline("ObjectiveShowActiveWithNotification", "EA::Reflection::Bool",   0x01000000u),
            VltAttribute.Inline("OutOfBoundsTime",                     "EA::Reflection::Int32",  5u),
            VltAttribute.InlineRawHash("Hash_7B834A07530629FA", "EA::Reflection::Float", 0x40466666u, 0x7B834A07530629FAUL),
            VltAttribute.PointerNoFixup("PoolDrainChallenge",          "Attrib::RefSpec", 0x90u, 0x08),
            VltAttribute.Inline("PoolDrainRequirement",                "EA::Reflection::Bool",   0u),
            VltAttribute.PointerNoFixup("RailCapChallenge",            "Attrib::RefSpec", 0xA8u, 0x0A),
            VltAttribute.Inline("RailCapRequirement",                  "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("ShowAISkatersInMiniMap",              "EA::Reflection::Bool",   0x01000000u),
            VltAttribute.Inline("SpawnOffboard",                       "EA::Reflection::Bool",   0u),
            VltAttribute.InlineRawHash("Hash_CAF082B3A11F4AFB", "EA::Reflection::Float", 0x40400000u, 0xCAF082B3A11F4AFBUL),
            VltAttribute.Inline("TutorialNISHalTitle",                 "EA::Reflection::Text",   0x3Du),
            VltAttribute.PointerNoFixup("TutorialVideoAsset",          "Attrib::RefSpec", 0xB0u, 0x08),
            VltAttribute.Inline("UseModifiedAIPathNames",              "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("UseStaticCameras",                    "EA::Reflection::Bool",   0u),
            VltAttribute.Inline("WipeoutOverResetObjectiveData",       "EA::Reflection::Bool",   0u),
        };

        if (attrs.Length != 45)
            throw new InvalidOperationException(
                $"FreeskateChallengeLocalDataTemplate: expected 45 attributes, built {attrs.Length}.");

        // layoutOffset=0x60 — bin tail blob's location (asserted above).
        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenge_local_data",
            key: "default",
            parent: "Hash_0",
            layoutOffset: 0x60u,
            attrs: attrs,
            explicitTypes: types));
    }
}
