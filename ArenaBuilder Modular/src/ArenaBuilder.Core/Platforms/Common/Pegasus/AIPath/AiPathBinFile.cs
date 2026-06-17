using System.Buffers.Binary;
using System.Text;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

/// <summary>
/// Parses an <c>AIPNODE3</c> .bin file produced by <c>AIPathRecorder/recorder.py</c>.
/// One .bin = one recording session = one or more paths (each respawn during recording
/// closes the current path and opens a new one). All metadata the AIPath PSG builder
/// needs (allowed_skaters / skill / is_loop / name_stem) is baked into the .bin header
/// so the builder has no sidecar dependencies.
///
/// Per-node bytes are passed through verbatim -- the recorder already writes the engine
/// layouts (44 B tAIPathNode + optional 40 B tAIPathNodeExtData). The builder will
/// patch each node's m_pExtData to its Aipathdata-relative offset at layout time.
///
/// File layout (little-endian everywhere, matches recorder.py _save):
///   "AIPNODE3"           magic (8 B)
///   u32 LE schema_version (= 3)
///   u64 LE allowed_skaters
///   u8  skill_level
///   u8  is_loop
///   u8  reserved[6]
///   u16 LE name_stem_len
///   utf8 name_stem
///   u32 LE path_count
///   For each path:
///     u32 LE node_count
///     For each node:
///       44 B tAIPathNode  (passed through as-is)
///       u8  has_ext
///       40 B tAIPathNodeExtData (if has_ext, passed through as-is)
/// </summary>
public static class AiPathBinFile
{
    public const int  CurrentSchemaVersion = 3;
    public const int  NodeBytes            = 44;
    public const int  ExtDataBytes         = 40;
    private static readonly byte[] Magic   = Encoding.ASCII.GetBytes("AIPNODE3");

    public sealed record File(
        ulong AllowedSkaters,
        byte  SkillLevel,
        bool  IsLoop,
        string NameStem,
        IReadOnlyList<Path> Paths);

    public sealed record Path(IReadOnlyList<Node> Nodes);

    /// <summary>
    /// One node from the recording. <see cref="NodeBytes"/> is the raw 44-B tAIPathNode
    /// blob the recorder wrote; m_pExtData (offset 0x20) is zero in this blob and the
    /// builder rewrites it at layout time to the Aipathdata-relative offset of the
    /// associated ExtData (if any).
    /// </summary>
    public sealed record Node(byte[] NodeBytes, byte[]? ExtBytes);

    public static File Read(string path)
    {
        using var fs = System.IO.File.OpenRead(path);
        using var br = new BinaryReader(fs);
        return Read(br);
    }

    public static File Read(BinaryReader br)
    {
        Span<byte> magic = stackalloc byte[8];
        if (br.Read(magic) != 8 || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("not an AIPNODE3 file (bad magic)");

        uint version = br.ReadUInt32();   // recorder writes LE
        if (version != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"unsupported AIPNODE schema version {version}, expected {CurrentSchemaVersion}");

        ulong allowedSkaters = br.ReadUInt64();
        byte  skillLevel     = br.ReadByte();
        byte  isLoopByte     = br.ReadByte();
        br.ReadBytes(6);                  // reserved

        ushort nameLen = br.ReadUInt16();
        string nameStem = Encoding.UTF8.GetString(br.ReadBytes(nameLen));

        uint pathCount = br.ReadUInt32();
        var paths = new List<Path>((int)pathCount);
        for (int pi = 0; pi < pathCount; pi++)
        {
            uint nodeCount = br.ReadUInt32();
            var nodes = new List<Node>((int)nodeCount);
            for (int ni = 0; ni < nodeCount; ni++)
            {
                byte[] nodeBytes = br.ReadBytes(NodeBytes);
                if (nodeBytes.Length != NodeBytes)
                    throw new EndOfStreamException(
                        $"truncated node {ni} of path {pi}: got {nodeBytes.Length} B, expected {NodeBytes}");
                byte hasExt = br.ReadByte();
                byte[]? extBytes = null;
                if (hasExt != 0)
                {
                    extBytes = br.ReadBytes(ExtDataBytes);
                    if (extBytes.Length != ExtDataBytes)
                        throw new EndOfStreamException(
                            $"truncated ext-data on node {ni} of path {pi}");
                }
                nodes.Add(new Node(nodeBytes, extBytes));
            }
            paths.Add(new Path(nodes));
        }

        return new File(allowedSkaters, skillLevel, isLoopByte != 0, nameStem, paths);
    }

    /// <summary>Per-node position read from the raw blob (big-endian three floats at +0x00).</summary>
    public static (float X, float Y, float Z) ReadPos(Node node)
    {
        var s = node.NodeBytes.AsSpan();
        return (
            BinaryPrimitives.ReadSingleBigEndian(s.Slice(0x00, 4)),
            BinaryPrimitives.ReadSingleBigEndian(s.Slice(0x04, 4)),
            BinaryPrimitives.ReadSingleBigEndian(s.Slice(0x08, 4)));
    }
}
