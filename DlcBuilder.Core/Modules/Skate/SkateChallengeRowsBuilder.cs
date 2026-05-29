using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest.Vlt;

namespace DlcBuilder.Modules.Skate;

/// Appends the per-instance challengebanks rows for ONE S.K.A.T.E. spot.
///
/// Per-instance rows parent to base-game `s_k_a_t_e` family (always loaded
/// via base challengebanks/main.vlt). Two collections per spot:
///
///   challenges/skate_&lt;key&gt;            3 attrs (Name, GlobalData, LocalData)
///   challenge_global_data/skate_&lt;key&gt; 7 or 8 attrs depending on profile
///
/// Two retail profiles (audited across all 10 base instances):
///   - `dwtn_01` profile (1/10): AvailableOnline=false, DebugOnly=true,
///     Description, Location, Title, World, WorldLocation (7 attrs)
///   - `rest` profile (9/10): Hash_2E4824C81FDAE87C(=2500), Description,
///     Location, OwnedItReward(2500cr), RequiredChallengeHull, Title, World
///     (+ optional WorldLocation on indu_*)
///
/// Base game never writes `ChallengeType`, `MapCategory`, or `MapStartLocation`
/// on per-instance rows; those inherit from family `s_k_a_t_e`.
public static class SkateChallengeRowsBuilder
{
    /// `eChallengeType` for S.K.A.T.E. (= 4). Read from base
    /// `challenge_global_data/s_k_a_t_e`.
    private const uint SkateChallengeType = 0x04;

    /// Stock retail Hash on the "rest" profile. Verified value 0x000009C4 = 2500
    /// across the 9 base instances using this profile.
    private const ulong HashAchievementCredits = 0x2E4824C81FDAE87CUL;

    public static void AppendChallengeRows(
        SkateChallengeSpec spec,
        BinPoolBuilder bin,
        List<(uint, uint)> binFixups,
        List<CollectionBlob> collections)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(binFixups);
        ArgumentNullException.ThrowIfNull(collections);

        string instanceKey = spec.ChallengeKey;
        const string familyKey = "s_k_a_t_e";

        // ── Row 1: challenges/<key> — 3 attrs ─────────────────────────────────
        uint rowNamePtr = bin.AddString(instanceKey);
        uint rowGlobalDataOff = bin.AddBlob(
            VltBinHelpers.BuildRefSpec24("challenge_global_data", instanceKey));
        uint rowLocalDataOff = VaultedRefSpecHelper.AddVaultedRefSpecWithPath(
            bin, binFixups, "challenge_local_data", instanceKey);

        collections.Add(VltCollectionBuilder.BuildCollection(
            "challenges", instanceKey, familyKey, 0u,
            new[]
            {
                VltAttribute.Inline        ("Name",       "EA::Reflection::Text",            rowNamePtr),
                VltAttribute.PointerNoFixup("GlobalData", "Attrib::RefSpec",                 rowGlobalDataOff, 0x08),
                VltAttribute.PointerNoFixup("LocalData",  "AttribSysUtils::tVaultedRefSpec", rowLocalDataOff,  0x08),
            },
            explicitTypes: new[]
            {
                "EA::Reflection::Text",
                "Attrib::RefSpec",
                "AttribSysUtils::tVaultedRefSpec",
            },
            numTypesDup: 4));

        // ── Row 2: challenge_global_data/<key> ─────────────────────────────────
        uint descPtr  = bin.AddString(spec.DescHalId);
        uint titlePtr = bin.AddString(spec.TitleHalId);
        // tLocationID = bin-pool offset to the locator name string.
        uint locPtr   = bin.AddString(spec.StartLocatorName);
        uint worldRef = bin.AddBlob(VltBinHelpers.BuildClassRefSpec(spec.Map.DistKey));

        if (spec.UseDwtn01Profile)
        {
            // dwtn_01 profile (7 attrs): AvailableOnline, DebugOnly, Description,
            // Location, Title, World, WorldLocation.
            uint worldLocRef = bin.AddBlob(VltBinHelpers.BuildRefSpec24(
                "fe_locations", spec.Map.DistKey));

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenge_global_data", instanceKey, familyKey, 0u,
                new[]
                {
                    VltAttribute.Inline        ("AvailableOnline", "EA::Reflection::Bool",            0x00000000u),
                    VltAttribute.Inline        ("DebugOnly",       "EA::Reflection::Bool",            0x01000000u),
                    VltAttribute.Inline        ("Description",     "EA::Reflection::Text",            descPtr),
                    VltAttribute.Inline        ("Location",        "Sk8::Challenge::tLocationID",     locPtr),
                    VltAttribute.Inline        ("Title",           "EA::Reflection::Text",            titlePtr),
                    VltAttribute.PointerNoFixup("World",           "Attrib::Gen::ClassRefSpec_world", worldRef,    0x08),
                    VltAttribute.PointerNoFixup("WorldLocation",   "Attrib::RefSpec",                 worldLocRef, 0x08),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::Bool",
                    "EA::Reflection::Text",
                    "Sk8::Challenge::tLocationID",
                    "Attrib::Gen::ClassRefSpec_world",
                    "Attrib::RefSpec",
                }));
        }
        else
        {
            // "rest" profile (7 attrs): Hash_2E4824C81FDAE87C, Description,
            // Location, OwnedItReward, RequiredChallengeHull, Title, World.
            // OwnedItReward: 16B { uint32 type=0, uint32 amount, uint64 pad }.
            // Base value: 00000000 000009C4 0000000000000000 = type 0, amount 2500.
            uint rewardAmount = (uint)spec.OwnedItRewardCredits;
            uint ownedRewardOff = bin.AddBlob(VltPayload.Build(w =>
            {
                w.WriteBE(0u);            // type
                w.WriteBE(rewardAmount);  // amount
                w.WriteBE(0UL);           // pad
            }));

            // RequiredChallengeHull = 1-element array, element is a bin-pool
            // text ptr to the spot key. Base ships skate_dwtn_02 with
            // element value 0x0000C630 → "skate_dwtn_02".
            uint hullStrPtr = bin.AddString(spec.ChallengeKey);
            uint requiredHullArr = bin.AddBlob(VltPayload.Build(w =>
            {
                w.WriteBE((ushort)1);  // count
                w.WriteBE((ushort)1);  // capacity
                w.WriteBE((ushort)4);  // typeSize
                w.WriteBE((ushort)0);  // padding
                w.WriteBE(0u);         // PtrN — fixed below
            }));
            binFixups.Add((requiredHullArr + 8u, hullStrPtr));

            collections.Add(VltCollectionBuilder.BuildCollection(
                "challenge_global_data", instanceKey, familyKey, 0u,
                new[]
                {
                    VltAttribute.InlineRawHash ("Hash_2E4824C81FDAE87C",  "EA::Reflection::UInt32",                 0x000009C4u, HashAchievementCredits),
                    VltAttribute.Inline        ("Description",            "EA::Reflection::Text",                   descPtr),
                    VltAttribute.Inline        ("Location",               "Sk8::Challenge::tLocationID",            locPtr),
                    VltAttribute.PointerNoFixup("OwnedItReward",          "Observatory::tObservatoryProgressionReward", ownedRewardOff,  0x08),
                    VltAttribute.PointerNoFixup("RequiredChallengeHull",  "Sk8::Challenge::tRequiredChallengeHull", requiredHullArr, 0x02),
                    VltAttribute.Inline        ("Title",                  "EA::Reflection::Text",                   titlePtr),
                    VltAttribute.PointerNoFixup("World",                  "Attrib::Gen::ClassRefSpec_world",        worldRef,        0x08),
                },
                explicitTypes: new[]
                {
                    "EA::Reflection::UInt32",
                    "EA::Reflection::Text",
                    "Sk8::Challenge::tLocationID",
                    "Observatory::tObservatoryProgressionReward",
                    "Sk8::Challenge::tRequiredChallengeHull",
                    "Attrib::Gen::ClassRefSpec_world",
                }));
        }
    }
}
