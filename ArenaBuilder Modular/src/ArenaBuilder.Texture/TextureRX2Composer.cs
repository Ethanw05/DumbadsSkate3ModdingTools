using ArenaBuilder.Core.Platforms.Common.PsgFormat;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture.Dds;
using ArenaBuilder.Texture.RenderWare;
using ArenaBuilder.Texture.Xbox;
using System.Buffers.Binary;

namespace ArenaBuilder.Texture;

/// <summary>
/// Xbox 360 (.rx2) texture composer — sibling of <see cref="TexturePsgComposer"/>.
///
/// Produces a single-texture <see cref="PsgArenaSpec"/> consumed by
/// <see cref="GeneralArenaBuilder.Write(PsgArenaSpec, System.IO.Stream, ArenaPlatform, string?)"/>
/// with <see cref="ArenaPlatform.Xbox360"/>. Same arena framing and object order as the PS3 path; the
/// X360 deltas are isolated to: BaseResource type 0x00010031, the 9-entry type registry, the Xenos
/// tiled payload, and the 0x34-byte texture-info struct (fetch constant instead of TextureInformationPS3).
///
/// Layout reconciled byte-for-byte against stock <c>DIST_BlackBoxPark</c> cTex .rx2:
///   header 0xAC, sections to 0x144, texture-info @0x144, TOC @0x180, VersionData @0x1D0,
///   dictionary @0x1D8, tiled texture data @0x238.
/// </summary>
public static class TextureRX2Composer
{
    private const int VersionDataSize = 8;

    /// <summary>
    /// Composes an X360 texture arena spec from parsed DDS input and a texture GUID.
    /// </summary>
    /// <param name="ddsInput">Parsed DDS (DXT1/3/5). Re-tiled into Xenos layout here.</param>
    /// <param name="textureGuid">TOC m_uiGuid; must match the mesh material channel GUID.</param>
    /// <param name="tocName">Optional TOC name override; defaults to "0x{guid}.Texture".</param>
    /// <param name="versionDataRevision">Optional VersionData revision override (default 0x0D, as stock).</param>
    public static PsgArenaSpec Compose(
        DdsTextureInput ddsInput,
        ulong textureGuid,
        string? tocName = null,
        uint? versionDataRevision = null)
    {
        if (ddsInput == null) throw new ArgumentNullException(nameof(ddsInput));

        var tiled = XenosTextureTiler.Build(ddsInput);
        byte[] baseResourceData = tiled.TiledData;
        byte[] textureObject = XboxTextureInfoBuilder.Build(tiled.FetchConstant);
        byte[] tocObject = TextureTocBuilder.Build(textureGuid, tocName);
        byte[] versionData = BuildVersionData(versionDataRevision);

        // Object order matches the PS3 composer; file layout via DeferBaseResourceLayout places the
        // tiled payload last. Alignments reproduce the stock offsets (texture-info @0x144 align 4,
        // TOC @0x180 align 16, VersionData @0x1D0 align 16, base data @0x238 align 4).
        var objects = new List<PsgObjectSpec>
        {
            new(baseResourceData, XboxTextureConstants.TypeIdBaseResource) { Alignment = 4 },
            new(textureObject, TexturePsgConstants.TypeIdTexture) { Alignment = 4 },
            new(tocObject, TexturePsgConstants.TypeIdTableOfContents) { Alignment = 16 },
            new(versionData, TexturePsgConstants.TypeIdVersionData) { Alignment = 16 }
        };

        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(ComputeArenaId((uint)baseResourceData.Length, textureGuid));

        var tocSpec = new PsgTocSpec
        {
            Entries = new List<PsgTocEntry>
            {
                new PsgTocEntry((uint)(0x14 + 0x18), textureGuid, TexturePsgConstants.TocEntryTypeTexture, TexturePsgConstants.TocEntryObjectPointer)
            },
            TypeMap = Array.Empty<(uint, uint)>()
        };

        return new PsgArenaSpec
        {
            ArenaId = arenaId,
            Objects = objects,
            TypeRegistry = XboxTextureConstants.TextureTypeRegistry,
            Toc = tocSpec,
            Subrefs = null,
            HeaderTypeIdAt0x70 = 0x80,
            UseFileSizeAt0x44 = false,
            DictRelocIsZero = true,
            DeferBaseResourceLayout = true,
            CompactTextureSectionLayout = true
        };
    }

    /// <summary>Composes and writes an X360 texture arena (.rx2) to <paramref name="outputPath"/>.</summary>
    public static void Write(DdsTextureInput ddsInput, ulong textureGuid, string outputPath,
        string? tocName = null, uint? versionDataRevision = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var spec = Compose(ddsInput, textureGuid, tocName, versionDataRevision);
        using var fs = File.Create(fullPath);
        GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Xbox360, fullPath);
    }

    private static byte[] BuildVersionData(uint? revisionOverride)
    {
        var buf = new byte[VersionDataSize];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), TexturePsgConstants.VersionDataVersion);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), revisionOverride ?? TexturePsgConstants.VersionDataRevision);
        return buf;
    }

    private static uint ComputeArenaId(uint payloadSize, ulong guid)
    {
        uint lo = (uint)(guid & 0xFFFFFFFF);
        uint hi = (uint)(guid >> 32);
        return (payloadSize ^ lo) + hi;
    }
}
