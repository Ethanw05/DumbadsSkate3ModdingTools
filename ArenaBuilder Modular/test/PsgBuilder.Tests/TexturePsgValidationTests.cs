using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture;
using ArenaBuilder.Texture.Dds;
using ArenaBuilder.Texture.RenderWare;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers.Binary;
using System.Text;

namespace ArenaBuilder.Tests;

/// <summary>
/// Validates texture PSG build: structure, header 0x44/0x6C, and that output is parseable.
/// For full round-trip (PSG2DDS export) use the dumper and PSG2DDS manually.
/// </summary>
public sealed class TexturePsgValidationTests
{
    /// <summary>Minimal 4x4 DXT1 DDS: magic + header 124 bytes + 8 bytes payload. FourCC at file offset 84.</summary>
    private static byte[] CreateMinimalDdsDxt1()
    {
        var dds = new byte[128 + 8];
        var s = dds.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4, 4), 124u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8, 4), 0x100Fu); // dwFlags
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12, 4), 4u);      // dwHeight
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16, 4), 4u);      // dwWidth
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28, 4), 1u);      // dwMipMapCount
        // DDS_PIXELFORMAT at file offset 76; fourCC at 84
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(76, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(80, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(84, 4), 0x31545844u); // DXT1
        return dds;
    }

    [Fact]
    public void BuildTexturePsg_FromMinimalDds_ProducesValidStructure()
    {
        byte[] dds = CreateMinimalDdsDxt1();
        DdsTextureInput input = DdsReader.Read(dds);
        Assert.Equal(4, input.Width);
        Assert.Equal(4, input.Height);
        Assert.Equal(1, input.MipCount);
        Assert.Equal(8, input.Payload.Length);

        ulong guid = TextureGuidStrategy.KeyToGuid(TextureGuidStrategy.BuildTextureKey("test", "mat", "diffuse", "img"));
        PsgArenaSpec spec = TexturePsgComposer.Compose(input, guid);

        using var ms = new MemoryStream();
        GenericArenaWriter.Write(spec, ms);
        byte[] psg = ms.ToArray();

        Assert.True(psg.Length >= 0x200, "Texture PSG should have header + sections + objects + dict + payload");
        // Magic RW4ps3
        Assert.Equal(0x89, psg[0]);
        Assert.Equal((byte)'R', psg[1]);
        Assert.Equal((byte)'W', psg[2]);
        Assert.Equal((byte)'4', psg[3]);
        Assert.Equal((byte)'p', psg[4]);
        Assert.Equal((byte)'s', psg[5]);
        Assert.Equal((byte)'3', psg[6]);

        uint valueAt0x44 = BinaryPrimitives.ReadUInt32BigEndian(psg.AsSpan(0x44, 4));
        uint valueAt0x6C = BinaryPrimitives.ReadUInt32BigEndian(psg.AsSpan(0x6C, 4));
        Assert.True(valueAt0x44 > 0, "Header 0x44 (main_base) must be non-zero");
        Assert.True(valueAt0x6C > 0, "Header 0x6C (BaseResource span) must be non-zero");
        Assert.True(valueAt0x44 + valueAt0x6C <= psg.Length, "main_base + span should not exceed file size");

        uint numEntries = BinaryPrimitives.ReadUInt32BigEndian(psg.AsSpan(0x20, 4));
        Assert.Equal(4u, numEntries);
    }

    [Fact]
    public void TextureGuidStrategy_KeyToGuid_IsDeterministic()
    {
        string key = TextureGuidStrategy.BuildTextureKey("a", "b", "diffuse", "c");
        ulong g1 = TextureGuidStrategy.KeyToGuid(key);
        ulong g2 = TextureGuidStrategy.KeyToGuid(key);
        Assert.Equal(g1, g2);
    }

    [Fact]
    public void ImageToDdsConverter_CanEncodeDxt1AndDxt5()
    {
        byte[] png = CreateSolidWhitePng();

        byte[] dxt1 = ImageToDdsConverter.ConvertToDds(png, generateMipMaps: false, hasAlpha: false);
        byte[] dxt5 = ImageToDdsConverter.ConvertToDds(png, generateMipMaps: false, hasAlpha: true);

        DdsTextureInput input1 = DdsReader.Read(dxt1);
        DdsTextureInput input5 = DdsReader.Read(dxt5);

        Assert.Equal(TexturePsgConstants.FormatDxt1, input1.Ps3Format);
        Assert.Equal(TexturePsgConstants.FormatDxt5, input5.Ps3Format);
    }

    [Fact]
    public void ImageToDdsConverter_CanDiscardSourceAlpha_ForOpaqueChannels()
    {
        byte[] png = CreateTransparentPng();

        byte[] dxt1 = ImageToDdsConverter.ConvertToDds(
            png,
            generateMipMaps: false,
            hasAlpha: false,
            forceOpaqueAlpha: true);

        DdsTextureInput input = DdsReader.Read(dxt1);
        Assert.Equal(TexturePsgConstants.FormatDxt1, input.Ps3Format);
    }

    [Fact]
    public void DerivedTextureGenerator_IsDeterministic_ForNormalAndSpecular()
    {
        byte[] png = CreateSolidWhitePng();

        byte[] normal1 = DerivedTextureGenerator.GenerateNormalMapPngFromImage(png);
        byte[] normal2 = DerivedTextureGenerator.GenerateNormalMapPngFromImage(png);
        byte[] spec1 = DerivedTextureGenerator.GenerateSpecularMapPngFromImage(png);
        byte[] spec2 = DerivedTextureGenerator.GenerateSpecularMapPngFromImage(png);

        Assert.Equal(normal1, normal2);
        Assert.Equal(spec1, spec2);
        Assert.NotEqual(normal1, spec1);
    }

    [Fact]
    public void RenderMaterialDataBuilder_UsesProvidedOverrideGuids()
    {
        const ulong diffuseGuid = 0x1000000000000001UL;
        const ulong normalGuid = 0x1000000000000002UL;
        const ulong specularGuid = 0x1000000000000003UL;
        const ulong lightmapGuid = 0x1000000000000004UL;

        byte[] bytes = RenderMaterialDataBuilder.BuildGameCompatible(
            materialName: "TestMaterial",
            overrides: new RenderMaterialDataBuilder.MaterialTextureOverrides(
                DiffuseGuid: diffuseGuid,
                NormalGuid: normalGuid,
                SpecularGuid: specularGuid,
                LightmapGuid: lightmapGuid));

        Assert.True(ContainsUInt64BigEndian(bytes, diffuseGuid));
        Assert.True(ContainsUInt64BigEndian(bytes, normalGuid));
        Assert.True(ContainsUInt64BigEndian(bytes, specularGuid));
        Assert.True(ContainsUInt64BigEndian(bytes, lightmapGuid));
    }

    [Fact]
    public void TextureTocBuilder_LongFeLocationName_FullDotTexture_NotTruncated()
    {
        // Regression: Fixed 72B TOC clipped long names like DLC_Location_Skate1xgames.Texture → ".Te".
        ulong guid = 0x0123456789ABCDEFUL;
        string tocName = "DLC_Location_Skate1xgames.Texture";
        byte[] toc = TextureTocBuilder.Build(guid, tocName);

        Assert.Equal(80, toc.Length);
        Assert.Equal(80u, BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(16, 4)));

        uint nameOffset = BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(8, 4));
        string roundTrip = Encoding.ASCII.GetString(toc.AsSpan((int)nameOffset)).TrimEnd('\0');
        Assert.Equal(tocName, roundTrip);
        Assert.Equal(0, toc[(int)nameOffset + tocName.Length]);

        byte[] tocShort = TextureTocBuilder.Build(guid, "DLC_Location_Danny.Texture");
        Assert.Equal(72, tocShort.Length); // Matches stock Danny TOC pack size when name fits.
    }

    [Fact]
    public void GuidToTocNameString_MatchesObservedFormat()
    {
        ulong guid = 0x2C70170A000D002A;
        string name = TextureGuidStrategy.GuidToTocNameString(guid);
        Assert.StartsWith("0x", name);
        Assert.EndsWith(".Texture", name);
        Assert.Contains("2c70170a000d002a", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FeLocationBaseNameToGuid_MatchesStockDannyAndMap1024()
    {
        // EBOOT path uses 64-bit FNV-1a over "<BaseName>.Texture".
        ulong dannyGuid = TextureGuidStrategy.FeLocationBaseNameToGuid("DLC_Location_Danny");
        ulong map1024Guid = TextureGuidStrategy.FeLocationBaseNameToGuid("map1024");

        Assert.Equal(0x7DEE9F4A124B90EEUL, dannyGuid);
        Assert.Equal(0x658DF3832445CF6FUL, map1024Guid);
    }

    private static bool ContainsUInt64BigEndian(byte[] data, ulong value)
    {
        Span<byte> expected = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(expected, value);
        for (int i = 0; i <= data.Length - 8; i++)
        {
            if (data.AsSpan(i, 8).SequenceEqual(expected))
                return true;
        }

        return false;
    }

    private static byte[] CreateSolidWhitePng()
    {
        using var image = new Image<Rgba32>(4, 4);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
                image[x, y] = new Rgba32(255, 255, 255, 255);
        }

        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] CreateTransparentPng()
    {
        using var image = new Image<Rgba32>(4, 4);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
                image[x, y] = new Rgba32(255, 255, 255, (byte)(x == y ? 0 : 128));
        }

        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}
