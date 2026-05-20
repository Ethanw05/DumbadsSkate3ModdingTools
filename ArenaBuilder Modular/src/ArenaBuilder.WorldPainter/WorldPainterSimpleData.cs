namespace ArenaBuilder.WorldPainter;

/// <summary>Simple paint cell: (Lo, Hi) WPDICT Lookup8 halves. (0,0) = unpainted/void.</summary>
public readonly record struct WpCell(uint Lo, uint Hi)
{
    public bool IsEmpty => Lo == 0 && Hi == 0;
}

/// <summary>
/// Binary save/load for WorldPainter paint data.
/// <para>
/// File: <c>worldpainter.bin</c><br/>
/// All multi-byte values are little-endian.
/// </para>
/// <code>
/// [0x00] Magic      : 8 bytes  "WPPAINT\0"
/// [0x08] Version    : u16      = 1 or 2
/// [0x0A] Cols       : u16
/// [0x0C] Rows       : u16
/// [0x0E] Reserved   : u16      = 0 (v2; v1 read as padding)
/// [0x10] MinX       : f64
/// [0x18] MinZ       : f64
/// [0x20] MaxX       : f64
/// [0x28] MaxZ       : f64      (header = 48 bytes)
/// [0x30] LayerCount : u16
/// Per layer:
///   LayerGuid  : u64
///   CellCount  : u32           (non-empty cells only)
///   Per cell:
///     Idx : u32                (row * cols + col, row 0 = south)
///     Lo  : u32
///     Hi  : u32
/// </code>
/// </summary>
public static class WpSimpleFile
{
    public const string FileName = "worldpainter.bin";

    private static readonly byte[] Magic = "WPPAINT\0"u8.ToArray();
    private const ushort CurrentVersion = 2;
    private const ushort MinSupportedVersion = 1;

    public static void Save(string directory, WpSimpleDocument doc)
    {
        string path = Path.Combine(directory, FileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: false);

        w.Write(Magic);
        w.Write(CurrentVersion);
        w.Write((ushort)doc.Cols);
        w.Write((ushort)doc.Rows);
        w.Write((ushort)0); // reserved
        w.Write(doc.MinX);
        w.Write(doc.MinZ);
        w.Write(doc.MaxX);
        w.Write(doc.MaxZ);
        w.Write((ushort)doc.Layers.Count);

        foreach (var layer in doc.Layers)
        {
            w.Write(layer.Guid);
            w.Write((uint)layer.Painted.Count);
            foreach (var cell in layer.Painted)
            {
                w.Write((uint)cell.Idx);
                w.Write(cell.Lo);
                w.Write(cell.Hi);
            }
        }
    }

    public static WpSimpleDocument? TryLoad(string directory, out string? error)
    {
        error = null;
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) { error = "File not found."; return null; }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: false);

            var magic = r.ReadBytes(8);
            if (!magic.AsSpan().SequenceEqual(Magic))
            { error = "Invalid magic — not a worldpainter.bin file."; return null; }

            ushort version = r.ReadUInt16();
            if (version < MinSupportedVersion || version > CurrentVersion)
            { error = $"Unsupported version {version}."; return null; }

            int cols = r.ReadUInt16();
            int rows = r.ReadUInt16();
            _ = r.ReadUInt16(); // reserved / legacy flags — ignored
            double minX = r.ReadDouble();
            double minZ = r.ReadDouble();
            double maxX = r.ReadDouble();
            double maxZ = r.ReadDouble();

            int layerCount = r.ReadUInt16();
            var layers = new List<WpSimpleLayer>(layerCount);
            for (int li = 0; li < layerCount; li++)
            {
                ulong guid = r.ReadUInt64();
                uint cellCount = r.ReadUInt32();
                var painted = new List<WpSparseCell>((int)cellCount);
                for (uint ci = 0; ci < cellCount; ci++)
                    painted.Add(new WpSparseCell(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
                layers.Add(new WpSimpleLayer(guid, painted));
            }

            return new WpSimpleDocument(cols, rows, minX, minZ, maxX, maxZ, layers);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }
}

// ── Value types ───────────────────────────────────────────────────────────────

public sealed record WpSimpleDocument(
    int Cols,
    int Rows,
    double MinX,
    double MinZ,
    double MaxX,
    double MaxZ,
    List<WpSimpleLayer> Layers);

public sealed record WpSimpleLayer(ulong Guid, List<WpSparseCell> Painted);

/// <param name="Idx">Flat cell index: row * cols + col (row 0 = south / min Z).</param>
public readonly record struct WpSparseCell(uint Idx, uint Lo, uint Hi);
