using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Builds the OTS framework template row that lives inside
/// `dlc_&lt;framework&gt;_local_data_framework.vlt`. Mirrors retail
/// `challenge_local_data/dlc_dwgh_own_the_spots` byte-for-byte: 45 attrs across
/// 15 type slots (numTypesDup=16 with one trailing zero slot for Hash_0),
/// parent=&lt;framework&gt;.
///
/// Per-instance OTS rows climb this row's parent chain. Without its full
/// attribute set the engine's parent-chain walker (sub_737790 →
/// j_Vault_FindCollectionByHash) can't resolve OTS inheritance and derefs
/// NULL trying to follow the chain back to dlc_&lt;framework&gt;.
public static class OtsFrameworkOwnTheSpotsRow
{
    public static CollectionBlob Build(string frameworkKey, BinPoolBuilder bin, List<(uint, uint)> binFixups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(binFixups);

        // ── Bin pool entries ──
        uint emptyStr        = bin.AddString("");
        uint halChalComplete = bin.AddString("ID_CHALLENGE_CENTRAL_MESSAGE_CHALLENGECOMPLETE");
        uint halOtsLeftArea  = bin.AddString("ID_CHALLENGE_MODIFIEDCENTRALMESSAGE_OTS_LEFTAREA");
        uint halScoredLow    = bin.AddString("ID_CHALLENGE_CENTRAL_MESSAGE_MODIFIED_1UP_SCOREDLOW");
        // Speech IDs declared but unused on disk (data=0); kept for symmetry
        // with retail's bin pool inventory.
        bin.AddString("Online_Celeb_03");
        bin.AddString("Sk3_Win_Med_3_Stoked");
        bin.AddString("TestFlythrough1");

        uint triggerVolStub      = bin.AddBlob(new byte[16]);
        uint triggerVolEmptyArr  = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        uint chalPresEvtArr      = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(16));
        uint nisDefStub          = bin.AddBlob(new byte[24]);

        // TrackUniqueTrickGroup RefSpec — DW ships
        // mClassKey=challenge_trick_group, mCollectionKey=Hash_0. 16-byte
        // payload only (no path-string fixup needed; it's a typed RefSpec).
        uint refSpecStub = bin.AddBlob(VltPayload.Build(w =>
        {
            w.WriteBE(Lookup8Hashing.Hash("challenge_trick_group"));
            w.WriteBE(0UL);
        }));

        // tVisualEditorData[] — typeSize=80 (sizeof tVisualEditorData), NOT 16.
        // DW dlc_dwgh_local_data_framework's dlc_dwgh_own_the_spots row ships
        // hdr1=0x00500000 byte-for-byte. typeSize=16 miscomputes element
        // strides during inheritance walks even when count=0.
        uint visIndicatorsArr = bin.AddBlob(VltBinHelpers.BuildEmptyArrayHeader(80));

        // 48-byte layout blob — DW PtrN-patches this row's layoutOff to a
        // 48-byte schema-mapped struct. A 4-byte stub made the engine read 44
        // bytes of adjacent StrE strings as schema fields → crash.
        uint layoutOff = bin.AddBlob(new byte[48]);

        var attrs = new[]
        {
            VltAttribute.Inline        ("AllowAltStartLocation",                "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("AllowObjectiveFailure",                "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("AllowSessionMarkersToBeSet",           "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.Inline        ("AllowSessionMarkersToBeUsed",          "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.Inline        ("AudioCharacterOverride",               "Sk8::Audio::eSk8Characters",                    0x00000068u),
            VltAttribute.InlineRawHash ("Hash_9DDC567540219ACC",                "EA::Reflection::Bool",                          0x01000000u, 0x9DDC567540219ACCUL),
            VltAttribute.Inline        ("CentralMessageHALIDChallengeComplete", "EA::Reflection::Text",                          halChalComplete),
            VltAttribute.Inline        ("CentralMessageHALIDChallengeFailed",   "EA::Reflection::Text",                          halChalComplete),
            VltAttribute.Inline        ("CentralMessageHALIDReturnToChallengeArea", "EA::Reflection::Text",                      halOtsLeftArea),
            VltAttribute.Inline        ("Challenge_Index",                      "EA::Reflection::UInt8",                         0x01000000u),
            VltAttribute.Inline        ("ChallengeInfoShowCompetitors",         "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("ChallengeInfoShowDesc",                "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("ChallengeInfoShowLocationImage",       "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.Inline        ("ChallengeMusicType",                   "Sk8::Audio::eChallengeMusicType",               0x00000003u),
            VltAttribute.Inline        ("ChallengePart",                        "EA::Reflection::Text",                          emptyStr),
            VltAttribute.Inline        ("ChallengeStage",                       "EA::Reflection::UInt8",                         0u),
            VltAttribute.Inline        ("ChallengeUnlockableAward",             "EA::Reflection::Text",                          emptyStr),
            VltAttribute.Inline        ("ClearSessionMarkerOnExit",             "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.InlineRawHash ("DisableChallengeEntitiesOnStart",      "EA::Reflection::Bool",                          0u, Lookup8Hashing.Hash("DisableChallengeEntitiesOnStart")),
            VltAttribute.PointerNoFixup("DiscoveryBoundary",                    "Sk8::Challenge::tTriggerVolumeInstanceID",      triggerVolStub, 0x00),
            VltAttribute.Inline        ("DmoRedoOperationsOnRestart",           "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("HighlightKillItObjectiveNum",          "EA::Reflection::Int16",                         0u),
            VltAttribute.Inline        ("HighlightKillOnlyInMasterObjective",   "EA::Reflection::Bool",                          0u),
            // HostCharacter inline NF=0x40, data=0 — engine's schema-mode
            // iteration yields HostCharacter for every challenge_local_data
            // row. sub_733450 walks the parent chain checking inline records;
            // when found, returns non-NULL and the caller's `lbz r9, 0xf(r29)`
            // succeeds. data=0 means "no specific host character" at runtime.
            VltAttribute.Inline        ("HostCharacter",                        "Sk8::Audio::eSk8Characters",                    0u),
            VltAttribute.PointerNoFixup("IntroPresentationEvents",              "Sk8::Challenge::tChallengePresentationEvent",   chalPresEvtArr, 0x02),
            VltAttribute.Inline        ("IsDynamicCompetitorChallenge",         "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("KilledItScore",                        "EA::Reflection::Int32",                         0x0000012Cu),
            VltAttribute.PointerNoFixup("ManualWalkConnectInSpotVolumes",       "Sk8::Challenge::tTriggerVolumeInstanceID",      triggerVolEmptyArr, 0x02),
            VltAttribute.Inline        ("ModifiedCentralMessageHALIDScoredLow", "EA::Reflection::Text",                          halScoredLow),
            VltAttribute.PointerNoFixup("NISFlythroughDefinition",              "Sk8::tNISPlaybackDefinition",                   nisDefStub, 0x00),
            VltAttribute.Inline        ("ObjectiveNotificationShownOnHUD",      "Sk8::Challenge::tObjectiveStatusField",         0u),
            VltAttribute.Inline        ("ObjectiveShowActiveWithNotification",  "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.Inline        ("ObjectiveStatusShownOnHUD",            "Sk8::Challenge::tObjectiveStatusField",         0x0000001Eu),
            VltAttribute.PointerNoFixup("OTSScoringBoundary",                   "Sk8::Challenge::tTriggerVolumeInstanceID",      triggerVolStub, 0x00),
            VltAttribute.Inline        ("OutOfBoundsTime",                      "EA::Reflection::Int32",                         0x00000005u),
            VltAttribute.PointerNoFixup("OutroPresentationEvents",              "Sk8::Challenge::tChallengePresentationEvent",   chalPresEvtArr, 0x02),
            VltAttribute.Inline        ("Persistent",                           "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("PointRequirement",                     "EA::Reflection::Int32",                         0x00000064u),
            VltAttribute.Inline        ("PrefilmTime",                          "EA::Reflection::Float",                         0u),
            VltAttribute.Inline        ("ScoringMultiplier",                    "EA::Reflection::Bool",                          0u),
            VltAttribute.Inline        ("SessionMarkerResetObjectiveData",      "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.Inline        ("ShowAISkatersInMiniMap",               "EA::Reflection::Bool",                          0x01000000u),
            VltAttribute.Inline        ("StartLocation",                        "Sk8::Challenge::tLocationID",                   emptyStr),
            VltAttribute.Inline        ("TimeToWaitBeforeReplay",               "EA::Reflection::Float",                         0x40000000u),
            VltAttribute.PointerNoFixup("TrackUniqueTrickGroup",                "Attrib::RefSpec",                               refSpecStub, 0x08),
            VltAttribute.PointerNoFixup("VisualIndicators",                     "Sk8::Challenge::tVisualEditorData",             visIndicatorsArr, 0x0A),
        };

        string[] types =
        {
            "Sk8::Audio::eSk8Characters",
            "EA::Reflection::Bool",
            "EA::Reflection::Text",
            "EA::Reflection::UInt8",
            "Sk8::Audio::eChallengeMusicType",
            "Sk8::Challenge::tTriggerVolumeInstanceID",
            "EA::Reflection::Int16",
            "Sk8::Challenge::tChallengePresentationEvent",
            "EA::Reflection::Int32",
            "Sk8::tNISPlaybackDefinition",
            "Sk8::Challenge::tObjectiveStatusField",
            "EA::Reflection::Float",
            "Sk8::Challenge::tLocationID",
            "Attrib::RefSpec",
            "Sk8::Challenge::tVisualEditorData",
        };

        return VltCollectionBuilder.BuildCollection(
            "challenge_local_data", $"{frameworkKey}_own_the_spots", frameworkKey,
            layoutOff, attrs,
            explicitTypes: types,
            numTypesDup: 16);   // retail uses num_types_dup=16 (one extra slot for Hash_0)
    }
}
