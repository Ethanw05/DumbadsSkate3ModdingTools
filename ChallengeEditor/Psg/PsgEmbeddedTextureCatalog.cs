using System;
using System.Collections.Generic;

namespace ChallengeEditor.Psg;

/// <summary>
/// Texture PSGs packed inside PSF chunks (same RefPack/decompress path as meshes).
/// Indexes GUID → raw PSG bytes by parsing each chunk's TOC (matches
/// <c>TextureIndexer</c> in <c>Dumping Tools/blender_psg_material_importer.py</c>).
/// </summary>
public static class PsgEmbeddedTextureCatalog
{
    private const uint TocEntryMarker = 0x9B0F1678;
    private const uint TocEntryTypeTexture = 0xAC462E4A;
    
    public readonly record struct TextureRef(byte[] PsgBytes, int? TextureDictIndex);

    /// <summary>
    /// True when this PSG chunk should be considered for texture GUID indexing.
    ///
    /// Matches the Blender importer: it indexes any PSG that has at least one <c>TEXTURE</c> entry and a TOC,
    /// not only "pure" texture archives. PSF chunks often mix types, but still include valid texture TOC entries.
    /// </summary>
    public static bool IsTextureArchivePsg(PsgReader psg)
    {
        bool hasTexture = false;
        bool hasToc = false;
        foreach (var e in psg.DictEntries)
        {
            if (e.TypeId == PsgReader.TypeIds.Texture) hasTexture = true;
            if (e.TypeId == PsgReader.TypeIds.TableOfContents) hasToc = true;
        }

        return hasTexture && hasToc;
    }

    /// <summary>
    /// Parses the TOC and registers every non-zero GUID → PSG bytes.
    /// Matches the Blender importer behavior: any PSG chunk that contains at least one <c>TEXTURE</c> dict entry
    /// is eligible; we then scan for a TOC and index all GUIDs found there. Multiple PSGs can advertise the same GUID;
    /// like the importer, we preserve discovery order (first seen wins at lookup time).
    /// </summary>
    public static void RegisterChunk(Dictionary<ulong, List<TextureRef>> guidToPsgBytes, byte[] psgBytes)
    {
        if (psgBytes.Length < 12 || !PsgReader.LooksLikePsg(psgBytes)) return;

        PsgReader psg;
        try
        {
            psg = new PsgReader(psgBytes);
            psg.Parse();
        }
        catch
        {
            return;
        }

        // Blender importer: consider a PSG a "texture PSG" if it has any TEXTURE dict entry.
        bool hasTextureEntry = false;
        foreach (var e in psg.DictEntries)
        {
            if (e.TypeId == PsgReader.TypeIds.Texture)
            {
                hasTextureEntry = true;
                break;
            }
        }
        if (!hasTextureEntry) return;

        foreach ((ulong g, uint objectPtr) in EnumerateTocTextureEntries(psg))
        {
            if (!guidToPsgBytes.TryGetValue(g, out var list))
            {
                list = new List<TextureRef>();
                guidToPsgBytes[g] = list;
            }
            int? preferredTextureIndex = DecodeTextureObjectPointer(psg, objectPtr);
            list.Add(new TextureRef(psgBytes, preferredTextureIndex));
        }
    }

    /// <summary>
    /// Enumerate texture TOC entries as (guid, m_pObject).
    /// </summary>
    public static IEnumerable<(ulong Guid, uint ObjectPointer)> EnumerateTocTextureEntries(PsgReader psg)
    {
        PsgReader.DictEntry? tocEntry = null;
        foreach (var e in psg.DictEntries)
        {
            if (e.TypeId == PsgReader.TypeIds.TableOfContents)
            {
                tocEntry = e;
                break;
            }
        }

        if (tocEntry is null) yield break;

        long tocBase = tocEntry.IsBaseResource ? psg.MainBase + tocEntry.Ptr : tocEntry.Ptr;
        if (tocBase + 8 > psg.Data.Length) yield break;

        uint itemsCount = psg.U32Be((int)tocBase + 0x00);
        uint arrayOffset = psg.U32Be((int)tocBase + 0x04);
        if (itemsCount == 0) yield break;

        DetectArrayStart(psg, tocBase, arrayOffset, (int)itemsCount, out long arrayAbs, out bool builderLayout);

        const int stride = 0x18;
        for (int i = 0; i < itemsCount; i++)
        {
            long entryOff = arrayAbs + i * stride;
            if (entryOff + stride > psg.Data.Length) break;

            if (!TryParseTocEntry(psg, entryOff, builderLayout, out ulong guid, out uint typ, out uint objectPtr))
                continue;

            if (guid == 0) continue;
            // Blender importer does not filter by type; builder-layout TOC entries frequently use typ=0.

            yield return (guid, objectPtr);
        }
    }

    private static void DetectArrayStart(PsgReader psg, long tocBase, uint arrayOffset, int itemsCount, out long arrayAbs, out bool builderLayout)
    {
        long builderAbs = tocBase + arrayOffset;
        long legacyAbs = tocBase + arrayOffset + 4;

        builderLayout = false;
        if (builderAbs + 0x18 <= psg.Data.Length)
        {
            uint marker = psg.U32Be((int)builderAbs + 0x04);
            uint typ = psg.U32Be((int)builderAbs + 0x10);
            if (marker == TocEntryMarker && (typ == TocEntryTypeTexture || typ == 0))
                builderLayout = true;
        }

        arrayAbs = builderLayout ? builderAbs : legacyAbs;
    }

    private static bool TryParseTocEntry(
        PsgReader psg, long entryOff, bool builderLayout,
        out ulong guid, out uint type, out uint objectPtr)
    {
        guid = 0;
        type = 0;
        objectPtr = 0;
        if (builderLayout)
        {
            guid = psg.U64Be((int)entryOff + 0x08);
            type = psg.U32Be((int)entryOff + 0x10);
            objectPtr = psg.U32Be((int)entryOff + 0x14);
            return true;
        }

        guid = psg.U64Be((int)entryOff + 0x04);
        type = psg.U32Be((int)entryOff + 0x0C);
        objectPtr = psg.U32Be((int)entryOff + 0x10);
        return true;
    }

    /// <summary>
    /// Mirrors importer _decode_m_pobject behavior:
    /// - subreference-encoded pointers are not resolved for texture indexing;
    /// - direct pointers are treated as 0-based dictionary indices.
    /// </summary>
    private static int? DecodeTextureObjectPointer(PsgReader psg, uint encodedPtr)
    {
        if (encodedPtr == 0) return null;
        if ((encodedPtr & 0x00FF0000u) == 0x00800000u) return null;
        return encodedPtr < psg.DictEntries.Count ? (int)encodedPtr : null;
    }
}
