using System;
using System.IO;
using System.Linq;

namespace ChallengeEditor.Psg;

// Headless smoke-test for PsfReader + RefPack. Invoked via `ChallengeEditor.exe --psf-probe [path]`.
// Walks a small set of PSFs and prints chunk metadata + RefPack decode summary.
public static class PsfReaderProbe
{
    public static int Run(string[] args)
    {
        string? root = args.Length >= 2 ? args[1] : DefaultSampleRoot();
        if (root == null || !Directory.Exists(root))
        {
            Console.Error.WriteLine($"PSF sample root not found: {root ?? "<none>"}");
            return 2;
        }

        // Pick the largest 8 PSFs under the root — small stubs (256 bytes) carry no chunks.
        var samples = new DirectoryInfo(root)
            .EnumerateFiles("*.psf", SearchOption.AllDirectories)
            .OrderByDescending(f => f.Length)
            .Take(8)
            .Select(f => f.FullName)
            .ToArray();
        if (samples.Length == 0)
        {
            Console.Error.WriteLine($"No .psf files found under {root}");
            return 2;
        }

        int ok = 0, fail = 0;
        foreach (string path in samples)
        {
            Console.WriteLine();
            Console.WriteLine($"== {path}");
            try
            {
                var file = PsfReader.Read(path);
                Console.WriteLine($"   version={file.Version} checksum=0x{file.Checksum:X8} firstChunk=0x{file.FirstChunkOffset:X} groups={file.GroupCount} chunkHint={file.ChunkCountHint} payloadHint=0x{file.PayloadSizeHint:X}");
                Console.WriteLine($"   chunks parsed: {file.Chunks.Count}");

                int idx = 0;
                int chunksToShow = Math.Min(file.Chunks.Count, 6);
                int fileMeshCount = 0;
                long fileVerts = 0;
                long fileTris = 0;
                foreach (var c in file.Chunks)
                {
                    bool show = idx < chunksToShow;
                    string encTag = c.Encoding switch
                    {
                        PsfReader.ChunkEncoding.Raw     => "raw",
                        PsfReader.ChunkEncoding.RefPack => $"refpack@+0x{c.RefPackOffset:X}",
                        _ => "unknown",
                    };
                    int decoded = -1;
                    string error = "";
                    string firstBytes = "";
                    byte[]? psgBytes = null;
                    try
                    {
                        psgBytes = PsfReader.DecompressChunk(c);
                        decoded = psgBytes.Length;
                        int n = Math.Min(psgBytes.Length, 12);
                        var hex = string.Concat(psgBytes.Take(n).Select(b => $"{b:X2} "));
                        var ascii = string.Concat(psgBytes.Take(n).Select(b => (b >= 0x20 && b < 0x7F) ? (char)b : '.'));
                        firstBytes = $" head=[{hex.TrimEnd()}] '{ascii}'";
                    }
                    catch (Exception ex) { error = $" decode-fail: {ex.Message}"; }

                    if (show) Console.WriteLine($"   [{idx}] asset=0x{c.AssetId:X16} chunk@0x{c.ChunkStart:X} payload@0x{c.PayloadStart:X} src=0x{c.SrcSize:X} dataOff=0x{c.DataOffset:X} meta=0x{c.MetaField:X} {encTag} decoded={decoded}{firstBytes}{error}");

                    if (psgBytes != null && PsgReader.LooksLikePsg(psgBytes))
                    {
                        try
                        {
                            var psg = new PsgReader(psgBytes);
                            psg.Parse();
                            if (show)
                            {
                                var typeBuckets = new Dictionary<uint, int>();
                                foreach (var e in psg.DictEntries)
                                {
                                    typeBuckets.TryGetValue(e.TypeId, out int count);
                                    typeBuckets[e.TypeId] = count + 1;
                                }
                                var topTypes = string.Join(", ", typeBuckets.OrderByDescending(kv => kv.Value).Take(6)
                                    .Select(kv => $"0x{kv.Key:X8}x{kv.Value}"));
                                Console.WriteLine($"        PSG: entries={psg.DictEntries.Count} mainBase=0x{psg.MainBase:X} typesRemap={psg.TypesList.Count} subRefs={psg.SubreferenceRecords.Count} top=[{topTypes}]");
                            }

                            var meshes = PsgMeshExtractor.ExtractMeshes(psg);
                            if (meshes.Count > 0)
                            {
                                int totalVerts = 0, totalTris = 0;
                                foreach (var m in meshes) { totalVerts += m.Positions.Length / 3; totalTris += m.Indices.Length / 3; }
                                if (show) Console.WriteLine($"        Meshes: {meshes.Count} (verts={totalVerts}, tris={totalTris})");
                                fileMeshCount += meshes.Count;
                                fileVerts += totalVerts;
                                fileTris += totalTris;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (show) Console.WriteLine($"        PSG parse failed: {ex.Message}");
                        }
                    }
                    idx++;
                }
                if (file.Chunks.Count > chunksToShow)
                    Console.WriteLine($"   ... {file.Chunks.Count - chunksToShow} more chunks elided");
                Console.WriteLine($"   TOTAL meshes from this file: {fileMeshCount} (verts={fileVerts}, tris={fileTris})");
                ok++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"   FAIL: {ex.Message}");
                fail++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Done: ok={ok} fail={fail}");
        return fail == 0 ? 0 : 1;
    }

    private static string? DefaultSampleRoot()
    {
        string[] candidates =
        {
            @"C:\Users\ethan\Desktop\PsgTools\.big_gui tool made by Ulfednyer\ArtGalleryDLC\content\missions",
            @"C:\Users\ethan\Desktop\PsgTools",
        };
        foreach (var c in candidates) if (Directory.Exists(c)) return c;
        return null;
    }
}
