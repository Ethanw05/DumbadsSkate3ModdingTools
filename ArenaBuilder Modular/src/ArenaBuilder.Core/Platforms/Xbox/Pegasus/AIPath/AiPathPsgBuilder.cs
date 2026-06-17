using ArenaBuilder.Core.Platforms.Common;
using ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.AIPath;

/// <summary>
/// Xbox 360 sibling of <c>ArenaBuilder.Core.Platforms.PS3.Pegasus.AIPath.AiPathPsgBuilder</c>.
/// Wraps an AiPathData blob in a complete RW4 X360 .rx2 arena via <see cref="XboxArenaWriter.Write"/>.
///
/// Shape: VersionData (dict 0), AiPathData (dict 1), TableOfContents (dict 2) — same three-object
/// layout as stock content. Cross-platform clean per docs/X360_Port_Deltas.md §7.
/// </summary>
public static class AiPathPsgBuilder
{
    public const uint DefaultArenaId = 0x00000001;
    public const byte DefaultWidthClamp = 10;

    public static byte[] Build(byte[] aipathdataBlob, ulong tocAssetGuid, uint arenaId = DefaultArenaId)
    {
        if (aipathdataBlob is null || aipathdataBlob.Length == 0)
            throw new ArgumentException("AiPathData blob is empty", nameof(aipathdataBlob));
        if (tocAssetGuid == 0)
            throw new ArgumentException("TOC asset GUID must be non-zero", nameof(tocAssetGuid));

        byte[] tocBlob = AiPathTocBuilder.Build(tocAssetGuid);

        var objects = new List<PsgObjectSpec>
        {
            new(VersionDataBuilder.Build(),  RwTypeIds.VersionData),
            new(aipathdataBlob,              RwTypeIds.AiPathData),
            new(tocBlob,                     RwTypeIds.TableOfContents)
        };

        var spec = new PsgArenaSpec
        {
            ArenaId            = arenaId,
            Objects            = objects,
            TypeRegistry       = PegasusRwConstants.CollisionTypeRegistry64,
            Toc                = new PsgTocSpec { Entries = Array.Empty<PsgTocEntry>(), TypeMap = null },
            Subrefs            = null,
            HeaderTypeIdAt0x70 = 1,
            UseFileSizeAt0x44  = true,
            DictRelocIsZero    = true
        };

        using var ms = new MemoryStream();
        GeneralArenaBuilder.Write(spec, ms, ArenaPlatform.Xbox360, "AIPath");
        return ms.ToArray();
    }

    public static byte[] BuildFromBin(
        AiPathBinFile.File bin,
        uint arenaId = DefaultArenaId,
        string? tocGuidSalt = null,
        byte? widthClampLeft  = DefaultWidthClamp,
        byte? widthClampRight = DefaultWidthClamp)
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
        return Build(aipathdata, tocAssetGuid, arenaId);
    }

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
