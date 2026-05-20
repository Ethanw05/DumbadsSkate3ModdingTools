using System.Linq;
using System.Numerics;
using Veldrid;

namespace ChallengeEditor;

public sealed class EditorScene
{
    public string PackageName { get; set; } = "MyDLC";

    /// All DISTs the user has imported. None exist on startup — the user must
    /// load one before authoring or rendering anything (no empty placeholders).
    public List<Dist> Dists { get; } = new();

    /// All GLB-imported maps. Siblings of <see cref="Dists"/>; both kinds
    /// participate in <see cref="ActiveMap"/> resolution and the scene tree.
    public List<GlbMap> GlbMaps { get; } = new();

    /// Id of the map currently being edited / rendered. Guid.Empty if none.
    /// Resolves against both <see cref="Dists"/> and <see cref="GlbMaps"/> —
    /// whichever collection owns the matching Id is the active map.
    public Guid ActiveMapId { get; set; }

    /// Back-compat alias for callers that pre-date GlbMap. Routes to
    /// <see cref="ActiveMapId"/>; reads return Empty when a GlbMap is active.
    public Guid ActiveDistId
    {
        get => ActiveMap is Dist d ? d.Id : Guid.Empty;
        set => ActiveMapId = value;
    }

    public IMap? ActiveMap
    {
        get
        {
            foreach (Dist d in Dists)    if (d.Id == ActiveMapId) return d;
            foreach (GlbMap g in GlbMaps) if (g.Id == ActiveMapId) return g;
            return null;
        }
    }

    public Dist? ActiveDist => ActiveMap as Dist;
    public GlbMap? ActiveGlbMap => ActiveMap as GlbMap;

    public bool HasActiveMap => ActiveMap != null;
    public bool HasActiveDist => ActiveDist != null;

    public Dist CreateDist(string name, string? folderPath = null)
    {
        var d = new Dist { Name = name, FolderPath = folderPath };
        Dists.Add(d);
        return d;
    }

    public GlbMap CreateGlbMap(string name, string sourcePath)
    {
        var g = new GlbMap { Name = name, SourcePath = sourcePath };
        GlbMaps.Add(g);
        return g;
    }

    public bool RemoveDist(Guid id) => RemoveMap(id);

    /// Remove a map by id from whichever collection owns it. Promotes the next
    /// available map (Dist first, then GlbMap) so the editor stays in a usable
    /// state; clears the active id if no maps remain.
    public bool RemoveMap(Guid id)
    {
        bool removed = false;
        Dist? dist = Dists.FirstOrDefault(x => x.Id == id);
        if (dist != null) { Dists.Remove(dist); removed = true; }
        else
        {
            GlbMap? glb = GlbMaps.FirstOrDefault(x => x.Id == id);
            if (glb != null) { GlbMaps.Remove(glb); removed = true; }
        }
        if (!removed) return false;

        if (ActiveMapId == id)
        {
            // Prefer promoting another Dist (legacy primary kind); fall back
            // to any remaining GlbMap; else clear so HasActiveMap == false.
            IMap? fallback = Dists.Cast<IMap>().Concat(GlbMaps).FirstOrDefault();
            ActiveMapId = fallback?.Id ?? Guid.Empty;
        }
        return true;
    }

    /// Pass-throughs to the active map's collections so existing callers
    /// (renderer, picker, etc.) keep working without per-map plumbing.
    /// Returns shared empty lists when no map is active — callers that mutate
    /// these (AddVolume, AddLocator, …) MUST check <see cref="HasActiveMap"/> first.
    public List<TriggerVolume> TriggerVolumes => ActiveMap?.TriggerVolumes ?? _emptyTv;
    public List<Locator>       Locators       => ActiveMap?.Locators       ?? _emptyLoc;
    public List<Challenge>     Challenges     => ActiveMap?.Challenges     ?? _emptyCh;
    public List<ImportedMesh>  Meshes         => ActiveMap?.Meshes         ?? _emptyMesh;

    private static readonly List<TriggerVolume> _emptyTv  = new();
    private static readonly List<Locator>       _emptyLoc = new();
    private static readonly List<Challenge>     _emptyCh  = new();
    private static readonly List<ImportedMesh>  _emptyMesh = new();
}

/// A single imported/authored DIST. Owns all the trigger volumes, locators,
/// challenges and imported PSG meshes that belong to that DIST. Switching maps
/// changes which of these are visible & editable.
public sealed class Dist : IMap
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? FolderPath { get; set; }
    public List<TriggerVolume> TriggerVolumes { get; } = new();
    public List<Locator> Locators { get; } = new();
    public List<Challenge> Challenges { get; } = new();
    public List<ImportedMesh> Meshes { get; } = new();
}

/// A map imported from a Blender-authored <c>.glb</c>. Mirrors <see cref="Dist"/>
/// in every authoring dimension so the scene tree, inspector, picker, and
/// rendering treat both kinds identically through <see cref="IMap"/>. The
/// per-material physics/audio/pattern assignments live here, along with any
/// spline data detected from named-empty knots in the GLB (used for rails /
/// AI paths / gates).
public sealed class GlbMap : IMap
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// Absolute path to the source <c>.glb</c>. Used to re-import meshes on
    /// scene reload (GPU buffers are not serialized) and to dedupe when the
    /// user picks the same file twice.
    public required string SourcePath { get; set; }
    public List<TriggerVolume> TriggerVolumes { get; } = new();
    public List<Locator> Locators { get; } = new();
    public List<Challenge> Challenges { get; } = new();
    public List<ImportedMesh> Meshes { get; } = new();
    /// One entry per unique material name in the GLB. Persisted in the
    /// <c>.cescn</c>; reconciled by name on re-import so user edits survive
    /// Blender re-exports that add/rename/remove materials.
    public List<GlbMaterialAssignment> Materials { get; } = new();
    /// One entry per spline detected from named-empty knots in the source
    /// (Blender empties named <c>&lt;prefix&gt;_pt_&lt;n&gt;</c>, or curve
    /// objects). Currently a read-only summary (name + point count)
    /// surfaced as "Splines (N)" under the materials list.
    public List<ImportedSpline> Splines { get; } = new();
}

/// One spline detected from named-empty knots in the source. Reset on every
/// re-import (we don't reconcile by name like materials — the source file
/// is the source of truth for spline geometry).
public sealed class ImportedSpline
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name;
    public int PointCount;
}

/// Per-material physics-and-audio metadata authored in the editor. Numeric
/// enum values match BlenRose's tables so emitting a sidecar JSON or feeding
/// ArenaBuilder later is a direct cast.
public sealed class GlbMaterialAssignment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string MaterialName { get; set; }
    public PhysicsSurface Physics { get; set; } = PhysicsSurface.Undefined;
    public AudioSurface Audio { get; set; } = AudioSurface.Undefined;
    public SurfacePattern Pattern { get; set; } = SurfacePattern.None;
    /// BlenRose's "AttributorMaterialName" class half (the part before the
    /// dot in e.g. <c>environmentsimple.default</c>). Defaults to
    /// EnvironmentSimple to match BlenRose's own fallback at BlenRose.py:2392.
    public MaterialClass MaterialClass { get; set; } = MaterialClass.EnvironmentSimple;
    public bool ExcludeCollision { get; set; }
    public bool ExcludePres { get; set; }
}

public sealed class ImportedMesh
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name;
    public required string SourcePath;
    public required uint VertexCount;
    public required uint IndexCount;
    public required Veldrid.DeviceBuffer VertexBuffer;
    public required Veldrid.DeviceBuffer IndexBuffer;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    /// <summary>World-space positions (same as GPU vertices) for CPU triangle ray tests.</summary>
    public Vector3[]? CpuPositions { get; init; }

    /// <summary>Triangle list (uint32 indices), length multiple of 3.</summary>
    public uint[]? CpuIndices { get; init; }

    /// <summary>Material name from the GLB primitive (when this mesh was imported
    /// via <c>GlbImporter</c>). Null for PSF-loaded DIST meshes. Drives
    /// viewport pick → material-assignment Inspector selection and the
    /// selected-mesh highlight tint in <see cref="Rendering.Renderer3D"/>.</summary>
    public string? GlbMaterialName { get; init; }

    /// <summary>Optional diffuse BC texture + sampler set (from a GUID-named texture <c>.psg</c> beside the DIST).</summary>
    public Texture? DiffuseTexture;
    public TextureView? DiffuseTextureView;
    public ResourceSet? DiffuseSamplerSet;

    public bool HasDiffuseTexture => DiffuseSamplerSet != null;
}

/// Which DlcSpec section an authored object belongs to. Drives the scene-tree
/// grouping and tells the manifest builder which row needs the object's data.
///   - Loose      = not yet placed under a section (sandbox / orphan).
///   - Freeskate  = used by the freeskate location row. Each Freeskate locator
///                  becomes one DLC menu entry — its Category drives BOTH the
///                  offline and online groupings (online entries auto-derive
///                  from each Freeskate locator at export time).
///   - Challenge  = used by a specific challenge (OwnerChallengeId set).
public enum OwnerKind { Loose, Freeskate, Challenge }

public sealed class TriggerVolume
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "volume";
    public Vector3 Center { get; set; }
    public Vector3 HalfExtents { get; set; } = new(2, 2, 1);
    /// X = pitch, Y = yaw, Z = roll (degrees). Composed via CreateFromYawPitchRoll(Y, X, Z).
    public Vector3 RotationDegrees { get; set; }

    /// Section this volume belongs to. Drives the scene tree grouping.
    public OwnerKind Owner { get; set; } = OwnerKind.Loose;
    /// Set when Owner == Challenge. Null otherwise.
    public Guid? OwnerChallengeId { get; set; }
}

public sealed class Locator
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "locator";
    public Vector3 Position { get; set; }
    /// X = pitch, Y = yaw, Z = roll (degrees).
    public Vector3 RotationDegrees { get; set; }
    public LocatorKind Kind { get; set; } = LocatorKind.Spawn;

    /// Section this locator belongs to.
    public OwnerKind Owner { get; set; } = OwnerKind.Loose;
    /// Set when Owner == Challenge. Null otherwise.
    public Guid? OwnerChallengeId { get; set; }

    /// FE menu category (only meaningful when Owner == Freeskate). Each
    /// Freeskate locator becomes its own DLC location entry — the Name shows
    /// up in the menu and the Category groups locators that share a value
    /// (e.g. "Street" gathers "Rails" + "Downtown" under one heading).
    /// Empty → grouped under the default "Maps" catch-all.
    public string Category { get; set; } = "";
}

public enum LocatorKind { Spawn, ChallengeStart, FreeskateAnchor, Sub }

public sealed class Challenge
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "challenge";
    public ChallengeType Type { get; set; } = ChallengeType.Ots;
    public Guid? StartLocatorId { get; set; }
    public Guid? ScoringVolumeId { get; set; }
    public Guid? DiscoveryBoundaryId { get; set; }
    public Guid? ChallengeBoundaryId { get; set; }
    /// <summary>World/signup-phase ribbon arrow locator (<c>{challengeKey}_vis_1</c> at export).</summary>
    public Guid? VisualSignupLocatorId { get; set; }

    /// <summary>Additional ribbon-arrow markers during the run (same style as signup ribbon).</summary>
    public List<Guid> InChallengeRibbonArrowLocatorIds { get; } = new();

    /// <summary>Chevron trail locators (ordered).</summary>
    public List<Guid> ChevronLocatorIds { get; } = new();

    public bool ReferencesVisualLocator(Guid locatorId) =>
        VisualSignupLocatorId == locatorId
        || InChallengeRibbonArrowLocatorIds.Contains(locatorId)
        || ChevronLocatorIds.Contains(locatorId);
    public int OwnedPoints { get; set; } = 250;
    public int KilledItPoints { get; set; } = 500;
    public int OnlineBonusXp { get; set; } = 1000;

    // ─── Race-only authoring ─────────────────────────────────────────────
    // Used when Type == ChallengeType.Race. Ignored for every other kind.
    //
    // Minimum-viable shape: one heat with one leg containing N gates. Stock
    // single-player race / multiplayer death-race both ship 1 heat with 1
    // leg; multi-heat/multi-leg authoring can be layered on later.
    // SceneToPackageInput expands this flat gate list into the builder's
    // `RaceHeats[1].Legs[1].Gates[N]` shape.

    /// Ordered list of trigger-volume Ids the player passes through during
    /// the race. The last entry is treated as the FINISH gate by the
    /// builder's `RaceVolumeNaming.BareGateName`.
    public List<Guid> RaceGateVolumeIds { get; } = new();

    /// Heat time limit (whole seconds). Maps to
    /// `RaceHeatInput.TimeLimitSeconds` → `challenge_race_heats.<i>.TimeLimit`.
    public int RaceTimeLimitSeconds { get; set; } = 180;

    /// Time-to-beat for the "Killed It" tier (seconds, fractional). Maps to
    /// `RaceHeatInput.KilledItSeconds` → `challenge_race_heats.<i>.KilledItTime`.
    public float RaceKilledItSeconds { get; set; } = 90f;

    /// When true (default), the engine lets a player who skips a gate keep
    /// racing instead of failing the heat. Maps to
    /// `ChallengeInput.RaceGateSkipable` →
    /// `challenge_local_data.<key>.RaceGateSkipable`.
    public bool RaceGateSkipable { get; set; } = true;

    /// When true, this is the online "death race" variant. The DLC race
    /// folder's challenge_local_data row gains the `_ol` key suffix and
    /// IntroNIS/OnlineOutroNIS audio fields. Maps to
    /// `ChallengeInput.IsDeathRace`.
    public bool IsDeathRace { get; set; }
}

public enum ChallengeType { Ots, Otl, Photo, Film, Race }
