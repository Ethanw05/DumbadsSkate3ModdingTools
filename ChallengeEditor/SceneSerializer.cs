using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChallengeEditor;

/// JSON save/load for `EditorScene`. Authoring data (Dists, GlbMaps, locators,
/// trigger volumes, challenges, per-material assignments, package name) round-
/// trips; GPU mesh buffers do NOT — DIST meshes are re-loaded on demand via
/// Load DIST and GLB meshes are re-imported on demand when the source .glb is
/// found.
///
/// Format: a single JSON file with extension `.cescn`. Versioned by the
/// `SchemaVersion` field on the root so future format changes can migrate.
/// Object Ids are preserved across save/load so cross-references
/// (Challenge → ScoringVolume etc.) stay intact.
public static class SceneSerializer
{
    /// v1 → v2: added GlbMaps[]; renamed root ActiveDistId → ActiveMapId.
    /// v1 files still load — ApplyTo accepts the old field name.
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(EditorScene scene, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        NormalizeChallengeStartLocatorRefs(scene);

        var dto = SceneDto.From(scene);
        string json = JsonSerializer.Serialize(dto, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// Ensure each challenge's StartLocatorId points to a ChallengeStart locator
    /// before we serialize. Older scenes (or accidental UI assignments) can
    /// leave StartLocatorId bound to a FreeskateAnchor, which later makes the
    /// file look like it "saved wrong".
    private static void NormalizeChallengeStartLocatorRefs(EditorScene scene)
    {
        IEnumerable<IMap> maps = scene.Dists.Cast<IMap>().Concat(scene.GlbMaps);
        foreach (IMap m in maps)
        {
            foreach (Challenge ch in m.Challenges)
            {
                bool valid = false;
                if (ch.StartLocatorId is Guid sid)
                {
                    var cur = m.Locators.FirstOrDefault(l => l.Id == sid);
                    valid = cur is not null && cur.Kind == LocatorKind.ChallengeStart;
                }
                if (valid) continue;

                // Prefer the challenge-owned start locator; fall back to any
                // ChallengeStart in this map.
                var fallback = m.Locators.FirstOrDefault(l =>
                    l.Kind == LocatorKind.ChallengeStart
                    && l.Owner == OwnerKind.Challenge
                    && l.OwnerChallengeId == ch.Id)
                    ?? m.Locators.FirstOrDefault(l => l.Kind == LocatorKind.ChallengeStart);

                ch.StartLocatorId = fallback?.Id;
            }
        }
    }

    /// Mutates `scene` in place — clears existing Dists/GlbMaps, then rebuilds
    /// from the file. Caller must dispose GPU resources on outgoing meshes BEFORE
    /// invoking this; we don't reach into Veldrid here.
    public static void Load(EditorScene scene, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<SceneDto>(json, JsonOpts)
            ?? throw new InvalidDataException($"Scene file is empty or invalid JSON: {path}");

        if (dto.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Scene file schema version {dto.SchemaVersion} is newer than this editor supports ({CurrentSchemaVersion}).");

        dto.ApplyTo(scene);
    }

    /// <summary>
    /// After <see cref="Load"/>, normalize each <see cref="Dist.FolderPath"/> so mesh re-import
    /// finds folders: full-path canonicalization, then (if still missing) resolve relative to the
    /// <c>.cescn</c> directory so portable scenes work when the file moves with its DIST tree.
    /// </summary>
    public static void ResolveDistFolderPaths(EditorScene scene, string sceneFilePath)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneFilePath);

        string? sceneDir = Path.GetDirectoryName(Path.GetFullPath(sceneFilePath));
        if (string.IsNullOrEmpty(sceneDir)) return;

        foreach (Dist d in scene.Dists)
        {
            if (string.IsNullOrWhiteSpace(d.FolderPath)) continue;
            d.FolderPath = ResolveExistingPath(d.FolderPath, sceneDir, isDirectory: true) ?? d.FolderPath;
        }

        foreach (GlbMap g in scene.GlbMaps)
        {
            if (string.IsNullOrWhiteSpace(g.SourcePath)) continue;
            g.SourcePath = ResolveExistingPath(g.SourcePath, sceneDir, isDirectory: false) ?? g.SourcePath;
        }
    }

    /// Canonicalize <paramref name="raw"/> as an absolute path (existing on disk).
    /// If <paramref name="raw"/> is rooted, prefer it; otherwise resolve against
    /// <paramref name="sceneDir"/>. Returns null when neither candidate exists —
    /// caller keeps the saved string so a missing-file diagnostic can show it.
    private static string? ResolveExistingPath(string raw, string sceneDir, bool isDirectory)
    {
        string trimmed = raw.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                string full = Path.GetFullPath(trimmed);
                bool exists = isDirectory ? Directory.Exists(full) : File.Exists(full);
                return exists ? full : null;
            }
            string relative = Path.GetFullPath(Path.Combine(sceneDir, trimmed));
            bool relExists = isDirectory ? Directory.Exists(relative) : File.Exists(relative);
            return relExists ? relative : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ── DTO shapes ────────────────────────────────────────────────────────
    // Mirror the EditorScene types but with stable JSON field names + Vector3
    // broken into X/Y/Z for human-readable / diff-friendly output.

    private sealed class SceneDto
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string PackageName { get; set; } = "";
        public Guid ActiveMapId { get; set; }
        /// v1-era field. Read on load (fallback for ActiveMapId) but never
        /// written — DefaultIgnoreCondition keeps it out of new files.
        public Guid? ActiveDistId { get; set; }
        public List<DistDto> Dists { get; set; } = new();
        public List<GlbMapDto> GlbMaps { get; set; } = new();

        public static SceneDto From(EditorScene s) => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            PackageName = s.PackageName ?? "",
            ActiveMapId = s.ActiveMapId,
            Dists = s.Dists.Select(DistDto.From).ToList(),
            GlbMaps = s.GlbMaps.Select(GlbMapDto.From).ToList(),
        };

        public void ApplyTo(EditorScene s)
        {
            s.Dists.Clear();
            s.GlbMaps.Clear();
            s.PackageName = PackageName;
            foreach (var dDto in Dists) s.Dists.Add(dDto.ToDist());
            foreach (var gDto in GlbMaps) s.GlbMaps.Add(gDto.ToGlbMap());

            // v2 stores ActiveMapId; v1 stored ActiveDistId. Accept either —
            // whichever is present and resolves against the loaded maps wins.
            Guid wanted = ActiveMapId != Guid.Empty ? ActiveMapId : (ActiveDistId ?? Guid.Empty);
            bool found =
                s.Dists.Any(d => d.Id == wanted)
                || s.GlbMaps.Any(g => g.Id == wanted);
            s.ActiveMapId = found ? wanted : (s.Dists.FirstOrDefault()?.Id ?? s.GlbMaps.FirstOrDefault()?.Id ?? Guid.Empty);
        }
    }

    private sealed class DistDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? FolderPath { get; set; }
        public List<TriggerVolumeDto> TriggerVolumes { get; set; } = new();
        public List<LocatorDto> Locators { get; set; } = new();
        public List<ChallengeDto> Challenges { get; set; } = new();

        public static DistDto From(Dist d) => new()
        {
            Id = d.Id,
            Name = d.Name,
            FolderPath = d.FolderPath,
            TriggerVolumes = d.TriggerVolumes.Select(TriggerVolumeDto.From).ToList(),
            Locators = d.Locators.Select(LocatorDto.From).ToList(),
            Challenges = d.Challenges.Select(ChallengeDto.From).ToList(),
        };

        public Dist ToDist()
        {
            // Object initializer preserves the saved Id (which is `init`-only)
            // so cross-references inside the dist (Challenge → volume Id, etc.)
            // continue to resolve after a round-trip.
            var d = new Dist { Id = Id, Name = Name, FolderPath = FolderPath };
            foreach (var v in TriggerVolumes) d.TriggerVolumes.Add(v.ToVolume());
            foreach (var l in Locators)       d.Locators.Add(l.ToLocator());
            foreach (var c in Challenges)     d.Challenges.Add(c.ToChallenge());
            return d;
        }
    }

    private sealed class GlbMapDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public List<TriggerVolumeDto> TriggerVolumes { get; set; } = new();
        public List<LocatorDto> Locators { get; set; } = new();
        public List<ChallengeDto> Challenges { get; set; } = new();
        public List<GlbMaterialAssignmentDto> Materials { get; set; } = new();
        /// Splines list is regenerated from the source GLB on every re-import
        /// (named-empty knots), but we still persist it so the user sees the
        /// spline count immediately on scene load (before the background
        /// re-import lands). Null = pre-spline scene file; treated as empty
        /// by ToGlbMap.
        public List<ImportedSplineDto>? Splines { get; set; }

        public static GlbMapDto From(GlbMap g) => new()
        {
            Id = g.Id,
            Name = g.Name,
            SourcePath = g.SourcePath,
            TriggerVolumes = g.TriggerVolumes.Select(TriggerVolumeDto.From).ToList(),
            Locators = g.Locators.Select(LocatorDto.From).ToList(),
            Challenges = g.Challenges.Select(ChallengeDto.From).ToList(),
            Materials = g.Materials.Select(GlbMaterialAssignmentDto.From).ToList(),
            Splines = g.Splines.Select(ImportedSplineDto.From).ToList(),
        };

        public GlbMap ToGlbMap()
        {
            var g = new GlbMap { Id = Id, Name = Name, SourcePath = SourcePath };
            foreach (var v in TriggerVolumes) g.TriggerVolumes.Add(v.ToVolume());
            foreach (var l in Locators)       g.Locators.Add(l.ToLocator());
            foreach (var c in Challenges)     g.Challenges.Add(c.ToChallenge());
            foreach (var m in Materials)      g.Materials.Add(m.ToAssignment());
            foreach (var s in Splines ?? new()) g.Splines.Add(s.ToSpline());
            return g;
        }
    }

    private sealed class ImportedSplineDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int PointCount { get; set; }

        public static ImportedSplineDto From(ImportedSpline s) => new()
        {
            Id = s.Id,
            Name = s.Name,
            PointCount = s.PointCount,
        };

        public ImportedSpline ToSpline() => new()
        {
            Id = Id,
            Name = Name,
            PointCount = PointCount,
        };
    }

    private sealed class GlbMaterialAssignmentDto
    {
        public Guid Id { get; set; }
        public string MaterialName { get; set; } = "";
        public PhysicsSurface Physics { get; set; }
        public AudioSurface Audio { get; set; }
        public SurfacePattern Pattern { get; set; }
        /// Nullable so v2-era scenes (saved before MaterialClass existed) keep
        /// loading; the DTO default to null lets <c>ToAssignment</c> fall back
        /// to the GlbMaterialAssignment model's own default.
        public MaterialClass? MaterialClass { get; set; }
        public bool ExcludeCollision { get; set; }
        public bool ExcludePres { get; set; }

        public static GlbMaterialAssignmentDto From(GlbMaterialAssignment a) => new()
        {
            Id = a.Id,
            MaterialName = a.MaterialName,
            Physics = a.Physics,
            Audio = a.Audio,
            Pattern = a.Pattern,
            MaterialClass = a.MaterialClass,
            ExcludeCollision = a.ExcludeCollision,
            ExcludePres = a.ExcludePres,
        };

        public GlbMaterialAssignment ToAssignment()
        {
            var ma = new GlbMaterialAssignment
            {
                Id = Id,
                MaterialName = MaterialName,
                Physics = Physics,
                Audio = Audio,
                Pattern = Pattern,
                ExcludeCollision = ExcludeCollision,
                ExcludePres = ExcludePres,
            };
            if (MaterialClass is MaterialClass mc) ma.MaterialClass = mc;
            return ma;
        }
    }

    private sealed class TriggerVolumeDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Vec3 Center { get; set; }
        public Vec3 HalfExtents { get; set; }
        public Vec3 RotationDegrees { get; set; }
        public OwnerKind Owner { get; set; }
        public Guid? OwnerChallengeId { get; set; }

        public static TriggerVolumeDto From(TriggerVolume v) => new()
        {
            Id = v.Id,
            Name = v.Name,
            Center = Vec3.From(v.Center),
            HalfExtents = Vec3.From(v.HalfExtents),
            RotationDegrees = Vec3.From(v.RotationDegrees),
            Owner = v.Owner,
            OwnerChallengeId = v.OwnerChallengeId,
        };

        public TriggerVolume ToVolume() => new()
        {
            Id = Id,
            Name = Name,
            Center = Center.ToVector3(),
            HalfExtents = HalfExtents.ToVector3(),
            RotationDegrees = RotationDegrees.ToVector3(),
            Owner = Owner,
            OwnerChallengeId = OwnerChallengeId,
        };
    }

    private sealed class LocatorDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Vec3 Position { get; set; }
        public Vec3 RotationDegrees { get; set; }
        public LocatorKind Kind { get; set; }
        public OwnerKind Owner { get; set; }
        public Guid? OwnerChallengeId { get; set; }
        public string Category { get; set; } = "";

        public static LocatorDto From(Locator l) => new()
        {
            Id = l.Id,
            Name = l.Name,
            Position = Vec3.From(l.Position),
            RotationDegrees = Vec3.From(l.RotationDegrees),
            Kind = l.Kind,
            Owner = l.Owner,
            OwnerChallengeId = l.OwnerChallengeId,
            Category = l.Category,
        };

        public Locator ToLocator() => new()
        {
            Id = Id,
            Name = Name,
            Position = Position.ToVector3(),
            RotationDegrees = RotationDegrees.ToVector3(),
            Kind = Kind,
            Owner = Owner,
            OwnerChallengeId = OwnerChallengeId,
            Category = Category ?? "",
        };
    }

    private sealed class ChallengeDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public ChallengeType Type { get; set; }
        public Guid? StartLocatorId { get; set; }
        public Guid? ScoringVolumeId { get; set; }
        public Guid? DiscoveryBoundaryId { get; set; }
        public Guid? ChallengeBoundaryId { get; set; }
        public Guid? VisualSignupLocatorId { get; set; }

        /// <summary>Obsolete on disk — migrated into <see cref="InChallengeRibbonArrowLocatorIds"/>.</summary>
        public Guid? VisualInChallengeLocatorId { get; set; }

        public List<Guid>? InChallengeRibbonArrowLocatorIds { get; set; }
        public List<Guid>? ChevronLocatorIds { get; set; }
        public int OwnedPoints { get; set; }
        public int KilledItPoints { get; set; }
        public int OnlineBonusXp { get; set; }

        // Race-only fields. Nullable on the DTO so older scene files (saved
        // before race authoring existed) round-trip cleanly — `null` → use
        // the Challenge model's default.
        public List<Guid>? RaceGateVolumeIds { get; set; }
        public int? RaceTimeLimitSeconds { get; set; }
        public float? RaceKilledItSeconds { get; set; }
        public bool? RaceGateSkipable { get; set; }
        public bool? IsDeathRace { get; set; }

        // Skate-only fields. Nullable on the DTO for pre-Skate-authoring
        // scene files.
        public List<Guid>? SkateSpotVolumeIds { get; set; }
        public Guid? SkateTurnBasedStartVolumeId { get; set; }
        public Guid? SkateWaitLocatorId { get; set; }
        public List<Guid>? SkateVisualIndicatorLocatorIds { get; set; }
        public float? SkateTimeLimitSeconds { get; set; }
        public bool? SkateUseDwtn01Profile { get; set; }
        public int? SkateOwnedItRewardCredits { get; set; }

        public static ChallengeDto From(Challenge c) => new()
        {
            Id = c.Id,
            Name = c.Name,
            Type = c.Type,
            StartLocatorId = c.StartLocatorId,
            ScoringVolumeId = c.ScoringVolumeId,
            DiscoveryBoundaryId = c.DiscoveryBoundaryId,
            ChallengeBoundaryId = c.ChallengeBoundaryId,
            VisualSignupLocatorId = c.VisualSignupLocatorId,
            InChallengeRibbonArrowLocatorIds = c.InChallengeRibbonArrowLocatorIds.ToList(),
            ChevronLocatorIds = c.ChevronLocatorIds.ToList(),
            OwnedPoints = c.OwnedPoints,
            KilledItPoints = c.KilledItPoints,
            OnlineBonusXp = c.OnlineBonusXp,
            RaceGateVolumeIds = c.RaceGateVolumeIds.ToList(),
            RaceTimeLimitSeconds = c.RaceTimeLimitSeconds,
            RaceKilledItSeconds = c.RaceKilledItSeconds,
            RaceGateSkipable = c.RaceGateSkipable,
            IsDeathRace = c.IsDeathRace,
            SkateSpotVolumeIds = c.SkateSpotVolumeIds.ToList(),
            SkateTurnBasedStartVolumeId = c.SkateTurnBasedStartVolumeId,
            SkateWaitLocatorId = c.SkateWaitLocatorId,
            SkateVisualIndicatorLocatorIds = c.SkateVisualIndicatorLocatorIds.ToList(),
            SkateTimeLimitSeconds = c.SkateTimeLimitSeconds,
            SkateUseDwtn01Profile = c.SkateUseDwtn01Profile,
            SkateOwnedItRewardCredits = c.SkateOwnedItRewardCredits,
        };

        public Challenge ToChallenge()
        {
            var c = new Challenge
            {
                Id = Id,
                Name = Name,
                Type = Type,
                StartLocatorId = StartLocatorId,
                ScoringVolumeId = ScoringVolumeId,
                DiscoveryBoundaryId = DiscoveryBoundaryId,
                ChallengeBoundaryId = ChallengeBoundaryId,
                VisualSignupLocatorId = VisualSignupLocatorId,
                OwnedPoints = OwnedPoints,
                KilledItPoints = KilledItPoints,
                OnlineBonusXp = OnlineBonusXp,
            };
            foreach (Guid g in InChallengeRibbonArrowLocatorIds ?? [])
                c.InChallengeRibbonArrowLocatorIds.Add(g);
            foreach (Guid g in ChevronLocatorIds ?? [])
                c.ChevronLocatorIds.Add(g);
            if (VisualInChallengeLocatorId is Guid legacy
                && !c.InChallengeRibbonArrowLocatorIds.Contains(legacy))
                c.InChallengeRibbonArrowLocatorIds.Insert(0, legacy);
            foreach (Guid g in RaceGateVolumeIds ?? [])
                c.RaceGateVolumeIds.Add(g);
            if (RaceTimeLimitSeconds is int tl) c.RaceTimeLimitSeconds = tl;
            if (RaceKilledItSeconds is float kit) c.RaceKilledItSeconds = kit;
            if (RaceGateSkipable is bool skip) c.RaceGateSkipable = skip;
            if (IsDeathRace is bool dr) c.IsDeathRace = dr;
            foreach (Guid g in SkateSpotVolumeIds ?? []) c.SkateSpotVolumeIds.Add(g);
            c.SkateTurnBasedStartVolumeId = SkateTurnBasedStartVolumeId;
            c.SkateWaitLocatorId = SkateWaitLocatorId;
            foreach (Guid g in SkateVisualIndicatorLocatorIds ?? []) c.SkateVisualIndicatorLocatorIds.Add(g);
            if (SkateTimeLimitSeconds is float stl) c.SkateTimeLimitSeconds = stl;
            if (SkateUseDwtn01Profile is bool sd) c.SkateUseDwtn01Profile = sd;
            if (SkateOwnedItRewardCredits is int sor) c.SkateOwnedItRewardCredits = sor;
            return c;
        }
    }

    private struct Vec3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public static Vec3 From(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
        public Vector3 ToVector3() => new(X, Y, Z);
    }
}
