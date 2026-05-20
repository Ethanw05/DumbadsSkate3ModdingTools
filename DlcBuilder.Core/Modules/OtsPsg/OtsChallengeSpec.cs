using ArenaBuilder.Core;
using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.DlcManifest.Vlt;
using DlcBuilder.Modules.LocatorPsg;

namespace DlcBuilder.Modules.OtsPsg;

/// Declarative description of one Own-The-Spot challenge.
///
/// Drives every output the builder needs to emit for a single OTS challenge:
///   • cSim_Global PSG (trigger volumes + locators) via OtsPsgBuilder
///   • boundary/&lt;key&gt;.xml (polygon footprint)
///   • stream/&lt;key&gt;.xml (one StreamTile)
///   • content/missions/&lt;key&gt;/&lt;key&gt;_Sim.loc (locator transforms, XML)
///   • 12 stub manifest files (.pmm/.psm/.pss/.pst per type)
///   • Vault rows in db/challenge_local_data/&lt;key&gt;.vlt (per-instance, 2 rows)
///   • Vault rows in db/challengebanks/dlc_&lt;pkg&gt;.vlt (7 new rows + family templates)
///   • Vault rows in db/dlc_&lt;pkg&gt;_local_data_framework.vlt (family rows)
///   • Vault rows in db/challenge_local_data/dlc_&lt;pkg&gt;_own_the_spots.vlt (anchor)
///   • Language pack entries: ID_CHALLENGE_&lt;UPPER&gt;_TITLE / _DESC
public sealed record OtsChallengeSpec
{
    /// Unique challenge key, e.g. "ots_user_01". Drives every filename and
    /// vault row key.
    public required string ChallengeKey { get; init; }

    /// The map this challenge belongs to. Provides DistKey, WorldStreamName,
    /// and the world's MapCategory.
    public required DlcSpec Map { get; init; }

    /// Display title shown in the challenge menu, e.g. "Loading Dock OTS".
    public required string DisplayTitle { get; init; }

    /// Short description shown under the title.
    public required string Description { get; init; }

    /// Trigger volumes — `challengeboundary` (outer) and `scoringboundary`
    /// (inner). The scoring boundary doubles as the EnteredVolume gate that
    /// arms the run.
    public required IReadOnlyList<OtsTriggerVolume> Triggers { get; init; }

    /// Sub-locator named transforms (optional <c>{key}_chev_*</c>, startlocator,
    /// vis_*, waitlocator, etc.).
    public required IReadOnlyList<LocationDescDataBuilder.SubLocSpec> SubLocators { get; init; }

    /// Main anchor locator transform (used for the parent tLocationDesc).
    public required Transform44 AnchorTransform { get; init; }

    /// Points needed for the "Owned It" tier.
    public int OwnedPoints { get; init; } = 100;

    /// Points needed for the "Killed It" tier.
    public int KilledItPoints { get; init; } = 300;

    /// When non-null/non-empty, emits one <c>tRequiredChallengeHull</c> element: IDA defines this type as
    /// <c>sizeof 0x4</c> with a single <c>const char *HullName</c> — not <c>tTriggerVolumeInstanceID</c>
    /// (<c>VolumeName</c> + <c>u64 VolumeID</c>, <c>0x10</c>). We write HullName as <b>0 on disk</b> and
    /// register a <b>PtrN bin fixup</b> (blob+8 → <c>AddString</c>). Default <c>null</c> = empty array.
    public string? RequiredChallengeHullStringRef { get; init; }

    /// Stream tile coordinates (cx, cy). Falls back to (150, -50) if null.
    public (int Cx, int Cy)? StreamTileCenter { get; init; }

    /// Location-kind u32 at +0x08 inside each <c>Sk8::Challenge::tVisualEditorData</c>
    /// entry in <c>challenge_local_data</c> VisualIndicators. Retail OTS:
    /// Danny Way <c>ots_dwmc_01.xml</c> uses <c>0x3C</c>; stock city
    /// <c>ots_dwtn_01.xml</c> (documentation/AttribXMLDump_StockGame_ots_audit) uses <c>0x58</c>
    /// with a different ribbon-key pattern — see <see cref="StockOtsStyleAllArrowRibbonKeys"/>.
    /// Other challenge types use other kinds on their rows (race <c>challenge_local_data</c> often
    /// <c>0x08</c>, photo <c>0x60</c>) — run <c>Dumping Tools/visual_indicators_attrib_audit.py</c> on
    /// <c>documentation/**/*.xml</c> for a full histogram.
    public uint VisualIndicatorLocationKind { get; init; } = 0x3Cu;

    /// When <see langword="false"/> (default), <c>OtsLocalDataVltBuilder</c> emits VisualIndicators for
    /// <c>{key}_chev_*</c> with <c>ribbon_indicator</c> + retail secondary key hash
    /// (<see cref="VltBinHelpers.RibbonIndicatorSecondarySpotKeyHash"/>), and <c>{key}_vis_2..</c> with
    /// <c>ribbon_indicator</c>/<c>arrow</c> (<c>{key}_vis_1</c> signup is excluded — see SignUpIndicator on global rows).
    /// Per-sub string override:
    /// <see cref="LocationDescDataBuilder.SubLocSpec.RibbonIndicatorCollectionKey"/>.
    /// When <see langword="true"/>, every VisualIndicators slot uses <c>arrow</c> (stock
    /// <c>ots_dwtn_01.xml</c>-style; location-kind often <c>0x58</c>).
    public bool StockOtsStyleAllArrowRibbonKeys { get; init; }

    // ── Derived names / hashes ─────────────────────────────────────────────

    /// Locator name used by row D's Location/MapStartLocation. Convention:
    /// `&lt;challengeKey&gt;_challengelocator_01`, matching retail DW's
    /// `ots_dwmc_01_challengelocator_01`.
    public string AnchorName => $"{ChallengeKey}_challengelocator_01";

    /// HAL ID for the FE Title field.
    public string TitleHalId => $"ID_CHALLENGE_{ChallengeKey.ToUpperInvariant()}_TITLE";

    /// HAL ID for the FE Description field.
    public string DescHalId => $"ID_CHALLENGE_{ChallengeKey.ToUpperInvariant()}_DESC";

    /// Stable per-tier hash key for the Owned objectives_group row.
    public ulong OwnedTierHash => Lookup8Hash.HashString($"{ChallengeKey}_objectives_owned");

    /// Stable per-tier hash key for the KilledIt objectives_group row.
    public ulong KilledItTierHash => Lookup8Hash.HashString($"{ChallengeKey}_objectives_killedit");

    /// Stable hash key for the per-tier objective definition.
    public ulong ObjectiveDefHash => Lookup8Hash.HashString($"{ChallengeKey}_objective_def");

    /// World-XZ AABB enclosing all trigger polygons. Used for boundary.xml +
    /// stream tile fallback.
    public (float MinX, float MinZ, float MaxX, float MaxZ) WorldAabbXZ()
    {
        if (Triggers.Count == 0) return (0, 0, 0, 0);
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var t in Triggers)
            foreach (var p in t.Polygon)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        return (minX, minZ, maxX, maxZ);
    }

    /// Polygon used for `boundary/&lt;key&gt;.xml`. Picks the volume whose
    /// canonical name contains "challengeboundary" (the outer "must stay
    /// inside" zone). Falls back to the first trigger if no name matches.
    public IReadOnlyList<(float X, float Z)> BoundaryPolygon
    {
        get
        {
            if (Triggers.Count == 0) return Array.Empty<(float, float)>();
            foreach (var t in Triggers)
                if (t.Name != null && t.Name.Contains("challengeboundary", StringComparison.OrdinalIgnoreCase))
                    return t.Polygon;
            return Triggers[0].Polygon;
        }
    }
}
