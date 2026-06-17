using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

using ArenaBuilder.Core.Platforms.Common;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.AIPath;

/// <summary>
/// Wraps an Aipathdata blob (composed by <see cref="AiPathDataBuilder"/>) into a
/// complete RW4 PS3 arena PSG via <see cref="GenericArenaWriter.Write"/>.
///
/// Shape matches stock Skate 3 AIPath PSGs: three objects -- VersionData (dict 0),
/// Aipathdata (dict 1), TableOfContents (dict 2). Verified byte-for-byte against
/// DIST_Industrial/cSim_350_-50_high/A5249F6736ADC979.psg and 20 AIPath PSGs across
/// DLC_DW_MegaCompund's cSim_*_high tiles. Without the TOC, cAssetActivationManager
/// never binds the AiPathData and no AI traffic spawns.
/// </summary>
public static class AiPathPsgBuilder
{
    /// <summary>
    /// Standard 64-entry type registry shared by every PSG type in the engine
    /// (mesh / collision / WorldPainter / AIPath). AiPathData (0x00EB0014)
    /// occupies index 28.
    /// </summary>
    private static readonly uint[] TypeRegistry64 =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033, 0x00010034,
        0x00010010, 0x00EB0000, 0x00EB0001, 0x00EB0003, 0x00EB0004, 0x00EB0005,
        0x00EB0006, 0x00EB000A, 0x00EB000D, 0x00EB0019, 0x00EB0007, 0x00EB0008,
        0x00EB000C, 0x00EB0009, 0x00EB000B, 0x00EB000E, 0x00EB0011, 0x00EB000F,
        0x00EB0010, 0x00EB0012, 0x00EB0022, 0x00EB0013, 0x00EB0014, 0x00EB0015,
        0x00EB0016, 0x00EB001A, 0x00EB001C, 0x00EB001D, 0x00EB001B, 0x00EB001E,
        0x00EB001F, 0x00EB0021, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0023,
        0x00EB0025, 0x00EB0026, 0x00EB0027, 0x00EB0028, 0x00EB0029, 0x00EB0018,
        0x00EC0010, 0x00010000, 0x00010002, 0x000200EB, 0x000200EA, 0x000200E9,
        0x00020081, 0x000200E8, 0x00080002, 0x00080001, 0x00080006, 0x00080003,
        0x00080004, 0x00040006, 0x00040007, 0x0001000F
    };

    /// <summary>
    /// ArenaId value every stock AIPath PSG writes at header offset 0x1C
    /// (confirmed across 21 stock AIPath PSGs: DIST_Industrial + DLC_DW_MegaCompund).
    /// </summary>
    public const uint DefaultArenaId = 0x00000001;

    /// <summary>
    /// Compose a complete PSG byte stream from one Aipathdata blob. The asset GUID
    /// embedded in the TableOfContents must be unique per output PSG; stock content
    /// uses a content-hash style GUID per cSim tile.
    /// </summary>
    public static byte[] Build(byte[] aipathdataBlob, ulong tocAssetGuid, uint arenaId = DefaultArenaId, ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        if (aipathdataBlob is null || aipathdataBlob.Length == 0)
            throw new ArgumentException("Aipathdata blob is empty", nameof(aipathdataBlob));
        if (tocAssetGuid == 0)
            throw new ArgumentException("TOC asset GUID must be non-zero", nameof(tocAssetGuid));

        byte[] tocBlob = AiPathTocBuilder.Build(tocAssetGuid);

        var objects = new List<PsgObjectSpec>
        {
            new(VersionDataBuilder.Build(),  RwTypeIds.VersionData),       // dict 0
            new(aipathdataBlob,              RwTypeIds.AiPathData),        // dict 1
            new(tocBlob,                     RwTypeIds.TableOfContents)    // dict 2
        };

        // The TOC object pre-built above embeds the entry table directly. The spec-level
        // Toc field is ignored when we hand-roll the TableOfContents as an object
        // (mirrors WorldPainterPsgBuilder.ComposeMinimal). Pass an empty entries list
        // to keep GenericArenaWriter happy.
        var spec = new PsgArenaSpec
        {
            ArenaId       = arenaId,
            Objects       = objects,
            TypeRegistry  = TypeRegistry64,
            Toc           = new PsgTocSpec
            {
                Entries  = Array.Empty<PsgTocEntry>(),
                TypeMap  = null
            },
            Subrefs                = null,
            HeaderTypeIdAt0x70     = 1,
            UseFileSizeAt0x44      = true,
            DictRelocIsZero        = true
        };

        using var ms = new MemoryStream();
        GeneralArenaBuilder.Write(spec, ms, platform, "AIPath");
        return ms.ToArray();
    }

    /// <summary>
    /// One-shot: read a recorder .bin, build a single Aipathdata containing every
    /// path in the file, wrap it in a PSG. The TOC asset GUID is hashed from a
    /// deterministic seed (name stem + path count + first/last position) so two
    /// PSGs built from the same input always produce the same GUID, while two
    /// different recordings always produce different GUIDs.
    /// Filename selection / tile-bucketing happens in the caller (see
    /// <c>PsgBuildAiPathCommand</c> and <c>TileBuildPipeline</c>).
    /// </summary>
    /// <summary>
    /// Default per-side half-width clamp (units of 1/50 m = 2 cm; see
    /// <c>NODE_WIDTH_UNITS_PER_METER</c> @ Sk2 0x82d74340). 10 ⇒ 20 cm half-width,
    /// 40 cm total corridor. Sk2 path follower hard-steers when
    /// <c>|distanceFromPath / (width * 0.02)| ≥ 0.6</c>
    /// (see <c>PathController::GeneratePhysicsInput</c> @ 0x823fbcb0). Recorder.py
    /// writes 255/255 by default (≈ unbounded, 5.10 m) which lets AI wander; the
    /// PSG builder clamps to <c>DefaultWidthClamp</c> at emit time so old
    /// recordings tighten automatically without re-capture. Set to <c>null</c>
    /// in <see cref="BuildFromBin"/> to honor recorder values verbatim.
    /// </summary>
    public const byte DefaultWidthClamp = 10;

    public static byte[] BuildFromBin(
        AiPathBinFile.File bin,
        uint arenaId = DefaultArenaId,
        string? tocGuidSalt = null,
        byte? widthClampLeft  = DefaultWidthClamp,
        byte? widthClampRight = DefaultWidthClamp,
        ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        var specs = new List<AiPathDataBuilder.PathSpec>(bin.Paths.Count);
        for (int i = 0; i < bin.Paths.Count; i++)
        {
            var id = AiPathDataBuilder.DefaultIdentifier(bin.NameStem, i);
            specs.Add(new AiPathDataBuilder.PathSpec(id, bin.Paths[i].Nodes));
        }

        byte[] aipathdata = AiPathDataBuilder.Build(
            specs,
            bin.AllowedSkaters,
            bin.SkillLevel,
            bin.IsLoop,
            widthClampLeft,
            widthClampRight);

        ulong tocAssetGuid = DeriveTocGuid(bin, tocGuidSalt);
        return Build(aipathdata, tocAssetGuid, arenaId, platform);
    }

    /// <summary>
    /// Derive a stable, unique-per-PSG asset GUID for the TableOfContents entry.
    /// Combines name stem + per-path node counts + first/last positions + caller salt
    /// so each tile-bucketed PSG gets its own GUID even when path subsets overlap.
    /// </summary>
    private static ulong DeriveTocGuid(AiPathBinFile.File bin, string? salt)
    {
        var seed = new System.Text.StringBuilder();
        seed.Append("aipath_toc_");
        seed.Append(bin.NameStem ?? "");
        seed.Append('_').Append(bin.AllowedSkaters.ToString("X16"));
        seed.Append('_').Append(bin.SkillLevel);
        for (int p = 0; p < bin.Paths.Count; p++)
        {
            var nodes = bin.Paths[p].Nodes;
            seed.Append("|p").Append(p).Append('=').Append(nodes.Count);
            if (nodes.Count > 0)
            {
                var (x0, y0, z0) = AiPathBinFile.ReadPos(nodes[0]);
                var (xL, yL, zL) = AiPathBinFile.ReadPos(nodes[nodes.Count - 1]);
                seed.Append(System.FormattableString.Invariant(
                    $"[{x0:F3},{y0:F3},{z0:F3}]>[{xL:F3},{yL:F3},{zL:F3}]"));
            }
        }
        if (!string.IsNullOrEmpty(salt)) seed.Append('|').Append(salt);

        ulong guid = Lookup8Hash.HashString(seed.ToString());
        return guid == 0 ? 0x1000000000000001ul : guid;
    }
}
