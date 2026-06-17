using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture.Dds;
using ArenaBuilder.Texture.RenderWare;
using System.Buffers.Binary;

namespace ArenaBuilder.Texture;

/// <summary>
/// Composes PsgArenaSpec for a single-texture PS3 PSG from DDS input and texture GUID.
/// Object order: BaseResource, Texture, TableOfContents, VersionData (dict order; file layout uses DeferBaseResourceLayout).
/// </summary>
public static class TexturePsgComposer
{
    private const int VersionDataSizeTexture = 8;

    /// <summary>
    /// Composes a full texture PsgArenaSpec. One texture per PSG.
    /// </summary>
    /// <param name="ddsInput">Parsed DDS (payload at offset 128).</param>
    /// <param name="textureGuid">TOC m_uiGuid and name; must match mesh material channel GUID.</param>
    /// <param name="tocName">Optional override for the TOC name string (e.g. "milkfactory.Texture").
    /// When null, defaults to "0x{guid}.Texture". Real DLC textures use "{basename}.Texture".</param>
    /// <param name="versionDataRevision">When set, overrides <see cref="TexturePsgConstants.VersionDataRevision"/> in the VersionData object (use <see cref="TexturePsgConstants.VersionDataRevisionFeLocation"/> for FE <c>.rps3</c>).</param>
    public static PsgArenaSpec Compose(
        DdsTextureInput ddsInput,
        ulong textureGuid,
        string? tocName = null,
        uint? versionDataRevision = null)
    {
        if (ddsInput == null)
            throw new ArgumentNullException(nameof(ddsInput));

        byte[] baseResourceData = ddsInput.Payload;
        byte[] textureObject = TextureRwBuilder.Build(ddsInput);
        byte[] tocObject = TextureTocBuilder.Build(textureGuid, tocName);
        byte[] versionData = BuildVersionData(versionDataRevision);

        // Match real texture dump offsets:
        // Texture @ 0x15C (4-byte aligned), TOC @ 0x190 (16-byte aligned), VersionData @ 0x1E0 (16-byte aligned).
        var objects = new List<PsgObjectSpec>
        {
            new(baseResourceData, TexturePsgConstants.TypeIdBaseResource) { Alignment = 4 },
            new(textureObject, TexturePsgConstants.TypeIdTexture) { Alignment = 4 },
            new(tocObject, TexturePsgConstants.TypeIdTableOfContents) { Alignment = 16 },
            new(versionData, TexturePsgConstants.TypeIdVersionData) { Alignment = 16 }
        };

        uint arenaId = PsgUniqueIdAllocator.AcquireArenaId(ComputeArenaId((uint)baseResourceData.Length, textureGuid));

        var tocSpec = new PsgTocSpec
        {
            Entries = new List<PsgTocEntry> { new PsgTocEntry((uint)(0x14 + 0x18), textureGuid, TexturePsgConstants.TocEntryTypeTexture, TexturePsgConstants.TocEntryObjectPointer) },
            TypeMap = Array.Empty<(uint, uint)>()
        };

        return new PsgArenaSpec
        {
            ArenaId = arenaId,
            Objects = objects,
            TypeRegistry = TexturePsgConstants.TextureTypeRegistry,
            Toc = tocSpec,
            Subrefs = null,
            HeaderTypeIdAt0x70 = 0x80,
            UseFileSizeAt0x44 = false,
            DictRelocIsZero = true,
            DeferBaseResourceLayout = true,
            CompactTextureSectionLayout = true
        };
    }

    private static byte[] BuildVersionData(uint? revisionOverride)
    {
        var buf = new byte[VersionDataSizeTexture];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), TexturePsgConstants.VersionDataVersion);
        uint rev = revisionOverride ?? TexturePsgConstants.VersionDataRevision;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), rev);
        return buf;
    }

    private static uint ComputeArenaId(uint payloadSize, ulong guid)
    {
        uint lo = (uint)(guid & 0xFFFFFFFF);
        uint hi = (uint)(guid >> 32);
        return (payloadSize ^ lo) + hi;
    }
}
