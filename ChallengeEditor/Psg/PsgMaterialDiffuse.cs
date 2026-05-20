using System;
using System.Collections.Generic;

namespace ChallengeEditor.Psg;

/// <summary>
/// Resolves the diffuse texture GUID for <see cref="PsgReader.TypeIds.RenderOptiMeshData"/> by parsing
/// <see cref="PsgReader.TypeIds.RenderMaterialData"/> exactly like
/// <c>MaterialExtractor.extract_materials</c> + <c>_resolve_material_pointer</c> in
/// <c>Dumping Tools/blender_psg_material_importer.py</c>. Prefer a <c>diffuse</c> texture channel, then
/// <c>detail</c>/<c>macrooverlay</c>/<c>decal</c>, then any non-metadata texture (aligned with Blender Base Color fallbacks).
/// Material slot failures included missing SubreferenceRecords <c>objectId=171</c> (material index) handling per
/// <c>documentation/PSG_STRUCTURE_CONNECTIONS.md</c> §4, wrong dict→RMD interpretation, and sequential channel fallback when <c>m_pChannels</c> does not align.
/// </summary>
public static class PsgMaterialDiffuse
{
    private const int ChannelSize = 0x20;
    private const int MaterialSize = 0x0C;
    private const ushort KScalarConstant = 0x0002;

    private readonly record struct ParsedChannel(string Shader, ushort Flags, ulong Guid);

    /// <summary>
    /// Looks up the diffuse texture GUID for <b>one</b> <see cref="PsgReader.TypeIds.RenderOptiMeshData"/> submesh.
    /// Call once per extracted mesh with that mesh's <c>OptiMeshDataOffset</c> — same as Python reading
    /// <c>material_ptr</c> at optimesh <c>+0x24</c> and resolving <c>material_index</c> via
    /// <c>_resolve_material_pointer</c> per entry (<c>blender_psg_material_importer.py</c> ~2926–3267).
    /// Multiple optimeshes in one PSG often point at different subreference records / material slots, so each
    /// gets its own diffuse without sharing state between calls.
    /// </summary>
    /// <param name="optiMeshDataOffset">Absolute offset of this RenderOptiMeshData blob (per dictionary entry).</param>
    public static bool TryGetDiffuseTextureGuid(PsgReader psg, long optiMeshDataOffset, out ulong diffuseGuid)
    {
        diffuseGuid = 0;
        if (!TryReadMaterialData(psg, out long rmb, out _, out uint numMaterials, out uint materialsOffsetField,
                out uint channelsOffsetField, out List<ParsedChannel> allChannels))
            return false;

        long materialsAbs = rmb + materialsOffsetField;

        uint matEnc = psg.U32Be((int)optiMeshDataOffset + 0x24);
        if (!TryResolveMaterialIndex(psg, rmb, materialsAbs, numMaterials, materialsOffsetField, matEnc, out int materialIndex))
            return false;

        return TryPickFromMaterialIndex(psg, materialsAbs, numMaterials, channelsOffsetField, allChannels, materialIndex, out diffuseGuid);
    }

    /// <summary>
    /// Blender importer fallback when material_ptr fails: mesh_index modulo material_count.
    /// </summary>
    public static bool TryGetDiffuseTextureGuidByMeshIndexFallback(PsgReader psg, int meshEntryIndex, out ulong diffuseGuid)
    {
        diffuseGuid = 0;
        if (!TryReadMaterialData(psg, out long rmb, out _, out uint numMaterials, out uint materialsOffsetField,
                out uint channelsOffsetField, out List<ParsedChannel> allChannels))
            return false;
        if (numMaterials == 0) return false;

        int materialIndex = (int)((uint)Math.Max(0, meshEntryIndex) % numMaterials);
        long materialsAbs = rmb + materialsOffsetField;
        return TryPickFromMaterialIndex(psg, materialsAbs, numMaterials, channelsOffsetField, allChannels, materialIndex, out diffuseGuid);
    }

    private static bool TryReadMaterialData(
        PsgReader psg,
        out long rmb,
        out ReadOnlySpan<byte> rmBlob,
        out uint numMaterials,
        out uint materialsOffsetField,
        out uint channelsOffsetField,
        out List<ParsedChannel> allChannels)
    {
        rmb = 0;
        rmBlob = default;
        numMaterials = 0;
        materialsOffsetField = 0;
        channelsOffsetField = 0;
        allChannels = new List<ParsedChannel>();
        if (psg.RenderMaterialBase is not long rb) return false;
        rmb = rb;

        var rmEntry = psg.FindFirstByType(PsgReader.TypeIds.RenderMaterialData);
        if (rmEntry is null) return false;

        int rmLen = (int)Math.Min(rmEntry.Size, psg.Data.Length - rmb);
        if (rmLen <= 0) return false;
        rmBlob = psg.Data.AsSpan((int)rmb, rmLen);

        numMaterials = psg.U32Be((int)rmb + 0x00);
        uint numChannelsHdr = psg.U32Be((int)rmb + 0x04);
        materialsOffsetField = psg.U32Be((int)rmb + 0x08);
        channelsOffsetField = psg.U32Be((int)rmb + 0x0C);
        if (numMaterials == 0 || numMaterials > 4096 || numChannelsHdr > 4096) return false;

        long channelsAbs = rmb + channelsOffsetField;
        return TryParseAllChannels(psg, rmBlob, channelsAbs, (int)numChannelsHdr, out allChannels);
    }

    private static bool TryPickFromMaterialIndex(
        PsgReader psg,
        long materialsAbs,
        uint numMaterials,
        uint channelsOffsetField,
        List<ParsedChannel> allChannels,
        int wantedMaterialIndex,
        out ulong diffuseGuid)
    {
        diffuseGuid = 0;
        int channelCursor = 0;
        for (int matIdx = 0; matIdx < numMaterials; matIdx++)
        {
            long matStruct = materialsAbs + matIdx * MaterialSize;
            if (matStruct + MaterialSize > psg.Data.Length) break;

            uint numMatCh = psg.U32Be((int)matStruct + 0x00);
            uint matChPtrRel = psg.U32Be((int)matStruct + 0x08);
            if (numMatCh > 256) break;

            IReadOnlyList<ParsedChannel> matSlice = SliceMaterialChannels(
                allChannels, channelsOffsetField, numMatCh, matChPtrRel, channelCursor);
            channelCursor += (int)numMatCh;
            if (matIdx == wantedMaterialIndex)
                return TryPickBaseColorTextureGuid(matSlice, out diffuseGuid);
        }
        return false;
    }

    /// <summary>
    /// Mirrors BlenderMaterialBuilder: prefer <c>diffuse</c>, then other channels wired to Base Color,
    /// then first non-metadata texture (unknown names still drive viewport in Blender via fallback node).
    /// </summary>
    private static bool TryPickBaseColorTextureGuid(IReadOnlyList<ParsedChannel> matSlice, out ulong diffuseGuid)
    {
        diffuseGuid = 0;
        static bool IsMeta(string s) =>
            s.Equals("name", StringComparison.OrdinalIgnoreCase)
            || s.Equals("attribulatormaterialname", StringComparison.OrdinalIgnoreCase);

        static bool IsNonBaseTexture(string s)
        {
            return s.Equals("normal", StringComparison.OrdinalIgnoreCase)
                || s.Equals("lightmap", StringComparison.OrdinalIgnoreCase)
                || s.Equals("specular", StringComparison.OrdinalIgnoreCase)
                || s.Equals("transparent", StringComparison.OrdinalIgnoreCase)
                || s.Equals("alpha", StringComparison.OrdinalIgnoreCase)
                || s.Equals("noise", StringComparison.OrdinalIgnoreCase);
        }

        bool TryPass(Func<string, bool> predicate, out ulong g)
        {
            g = 0;
            foreach (ParsedChannel ch in matSlice)
            {
                if ((ch.Flags & KScalarConstant) != 0 || ch.Guid == 0) continue;
                if (IsMeta(ch.Shader)) continue;
                if (!predicate(ch.Shader)) continue;
                g = ch.Guid;
                return true;
            }
            return false;
        }

        if (TryPass(s => s.Equals("diffuse", StringComparison.OrdinalIgnoreCase), out diffuseGuid)) return true;
        if (TryPass(s => s.Equals("detail", StringComparison.OrdinalIgnoreCase), out diffuseGuid)) return true;
        if (TryPass(s => s.Equals("macrooverlay", StringComparison.OrdinalIgnoreCase), out diffuseGuid)) return true;
        if (TryPass(s => s.Equals("decal", StringComparison.OrdinalIgnoreCase), out diffuseGuid)) return true;

        foreach (var ch in matSlice)
        {
            if ((ch.Flags & KScalarConstant) != 0 || ch.Guid == 0) continue;
            if (IsMeta(ch.Shader)) continue;
            if (IsNonBaseTexture(ch.Shader)) continue;
            diffuseGuid = ch.Guid;
            return true;
        }

        return false;
    }

    private static bool TryParseAllChannels(
        PsgReader psg, ReadOnlySpan<byte> rmBlob, long channelsAbs, int numChannels, out List<ParsedChannel> list)
    {
        list = new List<ParsedChannel>(numChannels);
        for (int i = 0; i < numChannels; i++)
        {
            int ch = (int)channelsAbs + i * ChannelSize;
            if (ch + ChannelSize > psg.Data.Length) break;

            ushort flags = psg.U16Be(ch + 0x04);
            ulong guid = psg.U64Be(ch + 0x10);
            uint shaderRel = psg.U32Be(ch + 0x00);
            string shader = ResolveShaderString(rmBlob, shaderRel);
            list.Add(new ParsedChannel(shader, flags, guid));
        }

        return list.Count > 0;
    }

    /// <summary>
    /// Python <c>_resolve_material_pointer</c>: subreference → index from offset; dict → RMD means 0;
    /// direct dict resolve to RMD blob → 0; else absolute material struct → derive index.
    /// </summary>
    private static bool TryResolveMaterialIndex(
        PsgReader psg,
        long rmb,
        long materialsAbs,
        uint numMaterials,
        uint materialsOffsetField,
        uint matEnc,
        out int materialIndex)
    {
        materialIndex = 0;

        if (matEnc == 0) return false;

        // SubreferenceRecords[low byte], objectId == RenderMaterialData
        // Encoded subreference: PSG_STRUCTURE_CONNECTIONS.md §4 — objectId 1 = offset within RenderMaterialData;
        // objectId 171 = material slot index (offset field is the index).
        if ((matEnc & 0x00FF0000u) == 0x00800000u)
        {
            int recordIndex = (int)(matEnc & 0xFFu);
            if (recordIndex >= psg.SubreferenceRecords.Count) return false;
            var rec = psg.SubreferenceRecords[recordIndex];

            if (rec.ObjectId == 171)
            {
                if (rec.Offset >= numMaterials) return false;
                materialIndex = (int)rec.Offset;
                return materialIndex >= 0;
            }

            if (rec.ObjectId != 1) return false;

            uint relOffset = rec.Offset;
            if (relOffset < materialsOffsetField) return false;
            long delta = relOffset - materialsOffsetField;
            if (delta % MaterialSize != 0) return false;
            materialIndex = (int)(delta / MaterialSize);
            return materialIndex >= 0 && materialIndex < numMaterials;
        }

        // Direct dictionary index — RenderMaterialData entry → material 0 (Python mesh path)
        if (matEnc < psg.DictEntries.Count)
        {
            var e = psg.DictEntries[(int)matEnc];
            if (e.TypeId == PsgReader.TypeIds.RenderMaterialData)
            {
                materialIndex = 0;
                return numMaterials > 0;
            }
        }

        // Encoded pointer resolves to an absolute offset — either blob start (= mat 0) or a tRMaterial slot
        long? absOrNull = psg.ResolveEncodedPointer(matEnc);
        if (absOrNull is not long abs) return false;

        if (abs == rmb)
        {
            materialIndex = 0;
            return numMaterials > 0;
        }

        if (abs >= materialsAbs && (abs - materialsAbs) % MaterialSize == 0)
        {
            materialIndex = (int)((abs - materialsAbs) / MaterialSize);
            return materialIndex >= 0 && materialIndex < numMaterials;
        }

        return false;
    }

    /// <summary>
    /// Mirrors Python channel slicing: prefer <c>m_pChannels</c>; else sequential packing.
    /// </summary>
    private static IReadOnlyList<ParsedChannel> SliceMaterialChannels(
        List<ParsedChannel> allChannels,
        uint channelsOffsetField,
        uint numMatCh,
        uint matChPtrRel,
        int channelCursor)
    {
        if (numMatCh == 0) return Array.Empty<ParsedChannel>();

        if (matChPtrRel >= channelsOffsetField &&
            (matChPtrRel - channelsOffsetField) % ChannelSize == 0)
        {
            int startIdx = (int)((matChPtrRel - channelsOffsetField) / ChannelSize);
            int endIdx = startIdx + (int)numMatCh;
            if (startIdx >= 0 && endIdx <= allChannels.Count)
                return allChannels.GetRange(startIdx, (int)numMatCh);
        }

        int start = channelCursor;
        int take = (int)numMatCh;
        if (start >= allChannels.Count) return Array.Empty<ParsedChannel>();
        take = Math.Min(take, allChannels.Count - start);
        return take <= 0 ? Array.Empty<ParsedChannel>() : allChannels.GetRange(start, take);
    }

    private static string ResolveShaderString(ReadOnlySpan<byte> renderMaterialBlob, uint relativeOffset)
    {
        if (relativeOffset == 0 || relativeOffset >= renderMaterialBlob.Length) return string.Empty;
        int end = renderMaterialBlob.Slice((int)relativeOffset).IndexOf((byte)0);
        if (end < 0) end = Math.Min(512, renderMaterialBlob.Length - (int)relativeOffset);
        return System.Text.Encoding.ASCII.GetString(renderMaterialBlob.Slice((int)relativeOffset, end));
    }
}
