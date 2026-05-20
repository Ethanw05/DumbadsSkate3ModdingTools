using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// RenderMaterialData object (0x00EB0005). Layout inferred from IDA Fixup/Unfix and Quick and Dirty IDA Export.h.
/// Header 0x14, Materials 0x0C each, Channels 0x20 each, StringList concatenated null-terminated.
/// Fixup (RenderMaterialData.txt): ptr+2,3,4 += ptr; each material m_pChannels += ptr; each channel m_ShaderInput += ptr,
/// m_StreamName += ptr only when !(m_uiFlags & 2); m_pStream is NOT fixed.
/// </summary>
public static class RenderMaterialDataBuilder
{
    public const uint KNone = 0x0000;
    public const uint KScalarConstant = 0x0002;
    // Legacy: kept for reference. Real game meshes derive Name channel GUID from material/texture name.
    public const ulong TemplateNameChannelGuid = 0xEC81C7C25A6C038CUL;

    /// <summary>
    /// Derives the Name channel GUID from material name. Real game meshes use unique GUIDs per material
    /// (e.g. 0xC3D7E499604DF81E for whitePlate) so the game can resolve texture PSGs in the cPres folder.
    /// Using a hardcoded GUID (TemplateNameChannelGuid) for all meshes causes crashes when the game
    /// cannot find a matching texture resource.
    /// </summary>
    public static ulong ComputeNameChannelGuid(string materialName)
    {
        if (string.IsNullOrEmpty(materialName)) materialName = "DefaultMaterial";
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"tex_{materialName}"));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// One material: numChannels, flags, channelsPtr (relative offset from RenderMaterialData base).
    /// Flags: we use 0. No double-sided / one-sided bit is documented for visual mesh in PSG; if discovered, set here.
    /// (Collision uses FLAG_TRIANGLEONESIDED in Edge_Collision_Enums.txt; that is for collision, not rendering.)
    /// </summary>
    public readonly record struct MaterialDef(uint NumChannels, uint Flags, uint ChannelsPtr);

    /// <summary>
    /// One channel (pegasus::tRMaterial::tChannel, 0x20 bytes). Per Quick and Dirty IDA Export.h:
    /// 0x00 m_ShaderInput (tStringPtr, += ptr), 0x04 m_uiFlags|m_uiImageChannel, 0x08 m_uiPad[2],
    /// 0x10 union: m_uiGuid+m_StreamName+m_pStream OR m_ShaderConstants[4]. m_StreamName fixed only when !(flags & 2).
    /// </summary>
    public readonly record struct ChannelDef(
        uint ShaderInputOffset,
        ushort Flags,
        ushort ImageChannel,
        ulong Guid,
        uint StreamNameOffset,
        float[]? ScalarConstants);

    /// <summary>
    /// Builds RenderMaterialData. Materials and channels use relative offsets from base.
    /// </summary>
    public static byte[] Build(
        IReadOnlyList<MaterialDef> materials,
        IReadOnlyList<ChannelDef> channels,
        IReadOnlyList<string> stringList)
    {
        if (materials == null || materials.Count == 0)
            throw new ArgumentException("At least one material required.", nameof(materials));
        if (channels == null)
            channels = Array.Empty<ChannelDef>();

        var stringBytes = new List<byte>();
        var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var s in stringList ?? Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (stringOffsets.ContainsKey(s))
                continue;
            uint off = (uint)stringBytes.Count;
            stringOffsets[s] = off;
            stringBytes.AddRange(Encoding.UTF8.GetBytes(s));
            stringBytes.Add(0);
        }

        uint baseOffset = 0x14;
        uint materialsOffset = baseOffset;
        uint materialsSize = (uint)(materials.Count * 0x0C);
        uint channelsOffset = materialsOffset + materialsSize;
        uint channelsSize = (uint)(channels.Count * 0x20);
        uint stringListOffset = channelsOffset + channelsSize;

        var buf = new List<byte>();

        buf.AddRange(BeU32((uint)materials.Count));
        buf.AddRange(BeU32((uint)channels.Count));
        buf.AddRange(BeU32(materialsOffset));
        buf.AddRange(BeU32(channelsOffset));
        buf.AddRange(BeU32(stringListOffset));

        uint channelCursor = 0;
        foreach (var mat in materials)
        {
            uint channelsPtr = channelsOffset + (channelCursor * 0x20);
            buf.AddRange(BeU32(mat.NumChannels));
            buf.AddRange(BeU32(mat.Flags));
            buf.AddRange(BeU32(channelsPtr));
            channelCursor += mat.NumChannels;
        }

        // Offsets relative to RenderMaterialData base (Fixup += ptr). ChannelDef uses string-list offsets; add stringListOffset.
        // m_uiPad[2] per IDA; real dumps use 0xDEADBEEF 0xDEADC0DE (F72E84DEFF51F7BF). m_pStream at 0x1C is NOT fixed (per IDA Unfix).
        const uint ChannelPad0 = 0xDEADBEEF;  // "dead beef"
        const uint ChannelPad1 = 0xDEADC0DE;  // "dead code"
        foreach (var ch in channels)
        {
            buf.AddRange(BeU32(ch.ShaderInputOffset + stringListOffset));
            buf.AddRange(BeU16(ch.Flags));
            buf.AddRange(BeU16(ch.ImageChannel));
            buf.AddRange(BeU32(ChannelPad0));
            buf.AddRange(BeU32(ChannelPad1));
            if ((ch.Flags & KScalarConstant) != 0 && ch.ScalarConstants != null && ch.ScalarConstants.Length >= 4)
            {
                buf.AddRange(BeF32(ch.ScalarConstants[0]));
                buf.AddRange(BeF32(ch.ScalarConstants[1]));
                buf.AddRange(BeF32(ch.ScalarConstants[2]));
                buf.AddRange(BeF32(ch.ScalarConstants[3]));
            }
            else
            {
                buf.AddRange(BeU64(ch.Guid));
                buf.AddRange(BeU32(ch.StreamNameOffset + stringListOffset));
                buf.AddRange(BeU32(0));
            }
        }

        buf.AddRange(stringBytes);

        return buf.ToArray();
    }

    /// <summary>
    /// Builds a single material with the standard six channels. Same as BuildGameCompatible.
    /// </summary>
    public static byte[] BuildMinimal(string materialName = "DefaultMaterial")
    {
        return BuildGameCompatible(materialName);
    }

    /// <summary>
    /// Optional per-channel texture GUID overrides. When set, mesh material channels use these GUIDs
    /// so they resolve to texture PSGs in the same cPres folder (same GUID in TOC). Null = use fallback defaults.
    /// NameChannelGuid: overrides the Name channel (Rendermaterialsubref); when null, derived from material name.
    /// NoiseGuid: when null, noise channel uses DiffuseGuid (animated.tree etc.). When set, uses this GUID.
    /// </summary>
    public sealed record MaterialTextureOverrides(
        ulong? NameChannelGuid = null,
        ulong? DiffuseGuid = null,
        ulong? NormalGuid = null,
        ulong? LightmapGuid = null,
        ulong? SpecularGuid = null,
        ulong? NoiseGuid = null);

    /// <summary>
    /// Default attributor stream when none is provided (e.g. StartPark). Use "environment.default" for SkateSchool-style static meshes.
    /// </summary>
    public const string DefaultAttributorStream = "environmentsimple.default";

    /// <summary>
    /// Channel config from BlenRose JSON. When provided, only these channels are built (textures + scalars).
    /// TextureChannelNames: PSG shader names (e.g. diffuse, lightmap, macrooverlay). Order preserved.
    /// ScalarValues: PSG scalar name -> value (e.g. detailNormalUVScale -> 8.0).
    /// </summary>
    public sealed record BlenroseChannelConfig(
        IReadOnlyList<string> TextureChannelNames,
        IReadOnlyDictionary<string, float> ScalarValues);

    /// <summary>
    /// Builds a single material with channels the game expects. Always outputs, in order (matches real mesh PSGs):
    /// Name → AttribulatorMaterialName → lightmap → specular → diffuse → normal.
    /// Name channel GUID is derived from <paramref name="materialName"/> (matches real game: unique per material).
    /// When <paramref name="overrides"/> is non-null, uses those GUIDs for the corresponding channels (mesh↔texture linkage).
    /// Uses fallback default texture GUIDs when overrides are null (game expects these to resolve to base assets).
    /// <paramref name="attributorStream"/>: AttributorMaterialName stream (e.g. from BlenRose JSON material_name). Null/empty = <see cref="DefaultAttributorStream"/>.
    /// </summary>
    public static byte[] BuildGameCompatible(
        string materialName = "DefaultMaterial",
        MaterialTextureOverrides? overrides = null,
        ulong? nameChannelGuidOverride = null,
        string? attributorStream = null,
        BlenroseChannelConfig? channelConfig = null)
    {
        if (channelConfig != null)
            return BuildFromChannelConfig(materialName, overrides, nameChannelGuidOverride, attributorStream, channelConfig);

        var channels = new List<ChannelDef>();
        var stringList = new List<string>();
        BuildGameCompatibleChannels(materialName, overrides, nameChannelGuidOverride, attributorStream, channels, stringList);
        var materials = new List<MaterialDef> { new MaterialDef((uint)channels.Count, 0, 0) };
        return Build(materials, channels, stringList);
    }

    /// <summary>
    /// Builds RenderMaterialData with <paramref name="numMaterials"/> identical material slots.
    /// Used for multi-mesh PSG where each mesh has its own material subref (0x14 + i*0x0C).
    /// <paramref name="attributorStream"/>: AttributorMaterialName stream (e.g. from BlenRose JSON). Null/empty = <see cref="DefaultAttributorStream"/>.
    /// </summary>
    public static byte[] BuildGameCompatibleMulti(
        int numMaterials,
        string materialName = "DefaultMaterial",
        MaterialTextureOverrides? overrides = null,
        ulong? nameChannelGuidOverride = null,
        string? attributorStream = null)
    {
        if (numMaterials < 1)
            throw new ArgumentOutOfRangeException(nameof(numMaterials), "At least one material required.");
        if (numMaterials == 1)
            return BuildGameCompatible(materialName, overrides, nameChannelGuidOverride, attributorStream);

        var singleChannels = new List<ChannelDef>();
        var stringList = new List<string>();
        BuildGameCompatibleChannels(materialName, overrides, nameChannelGuidOverride, attributorStream, singleChannels, stringList);

        var materials = new List<MaterialDef>();
        var channels = new List<ChannelDef>();
        for (int i = 0; i < numMaterials; i++)
        {
            materials.Add(new MaterialDef((uint)singleChannels.Count, 0, 0));
            channels.AddRange(singleChannels);
        }
        return Build(materials, channels, stringList);
    }

    /// <summary>
    /// Builds RenderMaterialData with one material slot per entry (multi-material from multiple GLBs).
    /// Each entry has its own channels. When ChannelConfig is non-null, only those channels are built.
    /// </summary>
    public static byte[] BuildGameCompatibleMultiFromParts(
        IReadOnlyList<(string MaterialName, MaterialTextureOverrides? Overrides, string? AttributorStream, BlenroseChannelConfig? ChannelConfig)> parts)
    {
        if (parts == null || parts.Count == 0)
            throw new ArgumentException("At least one part required.", nameof(parts));

        if (parts.Count == 1)
        {
            var p = parts[0];
            return BuildGameCompatible(p.MaterialName, p.Overrides, p.Overrides?.NameChannelGuid, p.AttributorStream, p.ChannelConfig);
        }

        var partChannels = new List<List<ChannelDef>>(parts.Count);
        var partStringLists = new List<List<string>>(parts.Count);
        var partOffsets = new List<Dictionary<string, uint>>(parts.Count);

        for (int i = 0; i < parts.Count; i++)
        {
            var (materialName, overrides, attributorStream, channelConfig) = parts[i];
            var channelsOut = new List<ChannelDef>();
            var stringListOut = new List<string>();
            if (channelConfig != null)
                BuildFromChannelConfigChannels(materialName, overrides, overrides?.NameChannelGuid, attributorStream, channelConfig, channelsOut, stringListOut);
            else
                BuildGameCompatibleChannels(materialName, overrides, overrides?.NameChannelGuid, attributorStream, channelsOut, stringListOut);
            partChannels.Add(channelsOut);
            partStringLists.Add(stringListOut);
            var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint pos = 0;
            foreach (var s in stringListOut)
            {
                offsets[s] = pos;
                pos += (uint)Encoding.UTF8.GetByteCount(s) + 1;
            }
            partOffsets.Add(offsets);
        }

        var globalStringList = new List<string>();
        var globalOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var list in partStringLists)
        {
            foreach (var s in list)
            {
                if (globalOffsets.ContainsKey(s)) continue;
                globalOffsets[s] = (uint)globalStringList.Count;
                globalStringList.Add(s);
            }
        }
        uint globalPos = 0;
        var globalByteOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var s in globalStringList)
        {
            globalByteOffsets[s] = globalPos;
            globalPos += (uint)Encoding.UTF8.GetByteCount(s) + 1;
        }

        var materials = new List<MaterialDef>();
        var channels = new List<ChannelDef>();
        for (int i = 0; i < parts.Count; i++)
        {
            var partCh = partChannels[i];
            var partOff = partOffsets[i];
            var offsetToStr = new Dictionary<uint, string>();
            foreach (var kv in partOff)
                offsetToStr[kv.Value] = kv.Key;

            materials.Add(new MaterialDef((uint)partCh.Count, 0, 0));
            foreach (var ch in partCh)
            {
                uint newShaderOff = offsetToStr.TryGetValue(ch.ShaderInputOffset, out var s1) ? globalByteOffsets[s1] : ch.ShaderInputOffset;
                uint newStreamOff = offsetToStr.TryGetValue(ch.StreamNameOffset, out var s2) ? globalByteOffsets[s2] : ch.StreamNameOffset;
                channels.Add(new ChannelDef(
                    newShaderOff,
                    ch.Flags,
                    ch.ImageChannel,
                    ch.Guid,
                    newStreamOff,
                    ch.ScalarConstants));
            }
        }
        return Build(materials, channels, globalStringList);
    }

    private static byte[] BuildFromChannelConfig(
        string materialName,
        MaterialTextureOverrides? overrides,
        ulong? nameChannelGuidOverride,
        string? attributorStream,
        BlenroseChannelConfig channelConfig)
    {
        var channelsOut = new List<ChannelDef>();
        var stringListOut = new List<string>();
        BuildFromChannelConfigChannels(materialName, overrides, nameChannelGuidOverride, attributorStream, channelConfig, channelsOut, stringListOut);
        var materials = new List<MaterialDef> { new((uint)channelsOut.Count, 0, 0) };
        return Build(materials, channelsOut, stringListOut);
    }

    private static void BuildFromChannelConfigChannels(
        string materialName,
        MaterialTextureOverrides? overrides,
        ulong? nameChannelGuidOverride,
        string? attributorStream,
        BlenroseChannelConfig channelConfig,
        List<ChannelDef> channelsOut,
        List<string> stringListOut)
    {
        const ulong defaultWhiteGuid = 0x2C70170A000B0040UL;
        const ulong defaultNormalGuid = 0x0000043D03E3870AUL;
        const ulong defaultSpecularGuid = 0x054A480902FD9A8DUL;
        const ulong defaultLightmapGuid = 0x2C70170A000B0040UL;
        const string defaultWhiteStream = "default_white_0x2c70170a000b0040";
        const string defaultNormalStream = "default_normal_0x0000043d03e3870a";
        const string defaultSpecularStream = "tex_0x054a480902fd9a8d";
        const string defaultLightmapStream = "tex_0x2c70170a000b0040";

        ulong lightmapGuid = overrides?.LightmapGuid ?? defaultLightmapGuid;
        ulong specularGuid = overrides?.SpecularGuid ?? defaultSpecularGuid;
        ulong diffuseGuid = overrides?.DiffuseGuid ?? defaultWhiteGuid;
        ulong normalGuid = overrides?.NormalGuid ?? defaultNormalGuid;
        string lightmapStream = lightmapGuid == defaultLightmapGuid ? defaultLightmapStream : $"tex_0x{lightmapGuid:x16}";
        string specularStream = specularGuid == defaultSpecularGuid ? defaultSpecularStream : $"tex_0x{specularGuid:x16}";
        string diffuseStream = diffuseGuid == defaultWhiteGuid ? defaultWhiteStream : $"tex_0x{diffuseGuid:x16}";
        string normalStream = normalGuid == defaultNormalGuid ? defaultNormalStream : $"tex_0x{normalGuid:x16}";

        string effectiveAttributor = !string.IsNullOrWhiteSpace(attributorStream) ? attributorStream! : DefaultAttributorStream;
        ulong nameGuid = nameChannelGuidOverride ?? overrides?.NameChannelGuid ?? ComputeNameChannelGuid(materialName);
        const ulong attribulatorGuid = 0;

        var stringList = new List<string> { "Name", "AttribulatorMaterialName", effectiveAttributor, materialName };
        foreach (var texName in channelConfig.TextureChannelNames)
            if (!stringList.Contains(texName)) stringList.Add(texName);
        stringList.Add(defaultWhiteStream);
        stringList.Add(defaultNormalStream);
        stringList.Add(defaultSpecularStream);
        stringList.Add(defaultLightmapStream);
        if (!stringList.Contains(lightmapStream)) stringList.Add(lightmapStream);
        if (!stringList.Contains(specularStream)) stringList.Add(specularStream);
        if (!stringList.Contains(diffuseStream)) stringList.Add(diffuseStream);
        if (!stringList.Contains(normalStream)) stringList.Add(normalStream);
        ulong noiseGuid = overrides?.NoiseGuid ?? diffuseGuid;
        string noiseStream = overrides?.NoiseGuid.HasValue == true ? $"tex_0x{noiseGuid:x16}" : diffuseStream;
        if (!stringList.Contains(noiseStream)) stringList.Add(noiseStream);
        foreach (var scalarName in channelConfig.ScalarValues.Keys)
            if (!stringList.Contains(scalarName)) stringList.Add(scalarName);

        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint pos = 0;
        foreach (var s in stringList)
        {
            if (offsets.ContainsKey(s)) continue;
            offsets[s] = pos;
            pos += (uint)Encoding.UTF8.GetByteCount(s) + 1;
        }

        channelsOut.Clear();
        channelsOut.Add(new ChannelDef(offsets["Name"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, nameGuid, offsets[materialName], null));
        channelsOut.Add(new ChannelDef(offsets["AttribulatorMaterialName"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, attribulatorGuid, offsets[effectiveAttributor], null));

        foreach (var texName in channelConfig.TextureChannelNames)
        {
            ulong guid = texName.ToLowerInvariant() switch
            {
                "diffuse" => diffuseGuid,
                "lightmap" => lightmapGuid,
                "specular" => specularGuid,
                "normal" => normalGuid,
                "noise" => noiseGuid,
                _ => defaultWhiteGuid
            };
            string stream = texName.ToLowerInvariant() switch
            {
                "diffuse" => diffuseStream,
                "lightmap" => lightmapStream,
                "specular" => specularStream,
                "normal" => normalStream,
                "noise" => noiseStream,
                _ => defaultWhiteStream
            };
            channelsOut.Add(new ChannelDef(offsets[texName], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, guid, offsets[stream], null));
        }

        foreach (var (scalarName, val) in channelConfig.ScalarValues)
        {
            var constants = new float[4] { val, 0, 0, 0 };
            channelsOut.Add(new ChannelDef(offsets[scalarName], (ushort)KScalarConstant, Ps3RenderWareConstants.DefaultChannelMask, 0, 0, constants));
        }

        stringListOut.Clear();
        stringListOut.AddRange(stringList);
    }

    private static void BuildGameCompatibleChannels(
        string materialName,
        MaterialTextureOverrides? overrides,
        ulong? nameChannelGuidOverride,
        string? attributorStream,
        List<ChannelDef> channelsOut,
        List<string> stringListOut)
    {
        const ulong defaultWhiteGuid = 0x2C70170A000B0040UL;
        const ulong defaultNormalGuid = 0x0000043D03E3870AUL;
        const ulong defaultSpecularGuid = 0x054A480902FD9A8DUL;
        const ulong defaultLightmapGuid = 0x2C70170A000B0040UL;
        ulong lightmapGuid = overrides?.LightmapGuid ?? defaultLightmapGuid;
        ulong specularGuid = overrides?.SpecularGuid ?? defaultSpecularGuid;
        ulong diffuseGuid = overrides?.DiffuseGuid ?? defaultWhiteGuid;
        ulong normalGuid = overrides?.NormalGuid ?? defaultNormalGuid;
        string defaultWhiteStream = "default_white_0x2c70170a000b0040";
        string defaultNormalStream = "default_normal_0x0000043d03e3870a";
        string defaultSpecularStream = "tex_0x054a480902fd9a8d";
        string defaultLightmapStream = "tex_0x2c70170a000b0040";
        string lightmapStream = lightmapGuid == defaultLightmapGuid ? defaultLightmapStream : $"tex_0x{lightmapGuid:x16}";
        string specularStream = specularGuid == defaultSpecularGuid ? defaultSpecularStream : $"tex_0x{specularGuid:x16}";
        string diffuseStream = diffuseGuid == defaultWhiteGuid ? defaultWhiteStream : $"tex_0x{diffuseGuid:x16}";
        string normalStream = normalGuid == defaultNormalGuid ? defaultNormalStream : $"tex_0x{normalGuid:x16}";

        string effectiveAttributor = !string.IsNullOrWhiteSpace(attributorStream) ? attributorStream! : DefaultAttributorStream;
        var stringList = new List<string>
        {
            "Name", "AttribulatorMaterialName", effectiveAttributor, materialName,
            "lightmap", "specular", "diffuse", "normal",
            defaultWhiteStream, defaultNormalStream, defaultSpecularStream, defaultLightmapStream
        };
        if (!stringList.Contains(lightmapStream)) stringList.Add(lightmapStream);
        if (!stringList.Contains(specularStream)) stringList.Add(specularStream);
        if (!stringList.Contains(diffuseStream)) stringList.Add(diffuseStream);
        if (!stringList.Contains(normalStream)) stringList.Add(normalStream);

        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint pos = 0;
        foreach (var s in stringList)
        {
            offsets[s] = pos;
            pos += (uint)Encoding.UTF8.GetByteCount(s) + 1;
        }

        ulong nameGuid = nameChannelGuidOverride ?? overrides?.NameChannelGuid ?? ComputeNameChannelGuid(materialName);
        const ulong attribulatorGuid = 0;

        channelsOut.Clear();
        channelsOut.Add(new ChannelDef(offsets["Name"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, nameGuid, offsets[materialName], null));
        channelsOut.Add(new ChannelDef(offsets["AttribulatorMaterialName"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, attribulatorGuid, offsets[effectiveAttributor], null));
        channelsOut.Add(new ChannelDef(offsets["lightmap"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, lightmapGuid, offsets[lightmapStream], null));
        channelsOut.Add(new ChannelDef(offsets["specular"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, specularGuid, offsets[specularStream], null));
        channelsOut.Add(new ChannelDef(offsets["diffuse"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, diffuseGuid, offsets[diffuseStream], null));
        channelsOut.Add(new ChannelDef(offsets["normal"], (ushort)KNone, Ps3RenderWareConstants.DefaultChannelMask, normalGuid, offsets[normalStream], null));

        stringListOut.Clear();
        stringListOut.AddRange(stringList);
    }
}

