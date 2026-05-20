using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChallengeEditor.Sk8;

/// <summary>
/// Reader for the .sk8 custom interchange format. Single-file container that
/// embeds meshes, base-color textures, lights, and splines in one binary
/// blob. Written by the Blender add-on
/// <c>sk8_blender_exporter.py</c>; consumed here.
///
/// File layout (binary, little-endian):
/// <code>
///   [0..4]    "SK8\0"  magic
///   [4..8]    uint32   format version (currently 1)
///   [8..12]   uint32   JSON manifest length (bytes)
///   [12..16]  uint32   binary data section length (bytes)
///   [16..16+jLen]                   UTF-8 JSON manifest
///   [16+jLen..16+jLen+bLen]         binary data section
/// </code>
/// JSON manifest describes every mesh / material / texture / light / spline
/// and references the binary section by <c>{offset, length}</c> pairs
/// relative to the start of that section. See the format-summary block in
/// the Blender add-on for the schema.
///
/// No Veldrid touched here — safe from <c>Task.Run</c>. The returned
/// <see cref="ParsedSk8"/> matches the shape the existing GLB import flow
/// in <c>EditorUi.cs</c> used so the rest of the editor (UploadMesh path,
/// alpha-mode handling, mip pipeline) is identical.
/// </summary>
public static class Sk8Importer
{
    private static ReadOnlySpan<byte> Magic => "SK8\0"u8;
    public const int CurrentVersion = 1;

    public sealed record ParsedMesh(
        string Name,
        string MaterialName,
        float[] PositionsXyz,
        float[] NormalsXyz,
        float[] TexCoordsUv,
        uint[]  Indices,
        Vector3 BoundsMin,
        Vector3 BoundsMax,
        byte[]? BaseColorImageBytes,
        Sk8AlphaMode AlphaMode,
        float AlphaCutoff);

    public sealed record ParsedSpline(string Name, int PointCount, IReadOnlyList<Vector3> Points);

    public sealed record ParsedSk8(
        IReadOnlyList<ParsedMesh> Meshes,
        IReadOnlyList<string> MaterialNames,
        IReadOnlyList<ParsedSpline> Splines,
        string Generator);

    public enum Sk8AlphaMode { Opaque, Mask, Blend }

    public static ParsedSk8 Parse(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 16) throw new InvalidDataException("Sk8 file is shorter than the 16-byte header.");

        // Header
        for (int i = 0; i < 4; i++)
            if (file[i] != Magic[i]) throw new InvalidDataException("Not a .sk8 file (magic mismatch).");
        uint version = BitConverter.ToUInt32(file, 4);
        uint jsonLen = BitConverter.ToUInt32(file, 8);
        uint binLen  = BitConverter.ToUInt32(file, 12);
        if (version != CurrentVersion)
            throw new InvalidDataException($"Sk8 version {version} not supported by this build (expected {CurrentVersion}).");
        if (16L + jsonLen + binLen > file.Length)
            throw new InvalidDataException("Sk8 header lengths overflow the file size.");

        // Manifest
        string json = Encoding.UTF8.GetString(file, 16, (int)jsonLen);
        ManifestDto manifest = JsonSerializer.Deserialize<ManifestDto>(json, JsonOpts)
            ?? throw new InvalidDataException("Sk8 manifest JSON is empty / null.");

        int binStart = 16 + (int)jsonLen;
        ReadOnlySpan<byte> bin = file.AsSpan(binStart, (int)binLen);

        // Resolve every mesh + material + texture
        var meshes = new List<ParsedMesh>(manifest.Meshes?.Count ?? 0);
        var materialOrder = new List<string>();
        var materialSet = new HashSet<string>(StringComparer.Ordinal);

        if (manifest.Meshes != null)
        {
            foreach (MeshDto mesh in manifest.Meshes)
            {
                ct.ThrowIfCancellationRequested();
                ParsedMesh? parsed = DecodeMesh(mesh, manifest, bin);
                if (parsed is null) continue;
                meshes.Add(parsed);
                if (materialSet.Add(parsed.MaterialName))
                    materialOrder.Add(parsed.MaterialName);
            }
        }

        // Splines — read the per-point Vector3 chunk so consumers can rebuild
        // the polyline directly (used by the DLC builder's tile-splitting
        // collision spline pipeline; also surfaceable in an authoring UI).
        // Each spline's binary chunk is `PointCount × 3 × float32` in xyz order.
        var splines = new List<ParsedSpline>(manifest.Splines?.Count ?? 0);
        if (manifest.Splines != null)
        {
            foreach (SplineDto s in manifest.Splines)
            {
                var points = new List<Vector3>(s.PointCount);
                if (s.Points != null && s.PointCount > 0)
                {
                    float[] flat = ReadFloats(bin, s.Points.Offset, s.Points.Length);
                    int safeCount = System.Math.Min(s.PointCount, flat.Length / 3);
                    for (int i = 0; i < safeCount; i++)
                        points.Add(new Vector3(flat[i * 3 + 0], flat[i * 3 + 1], flat[i * 3 + 2]));
                }
                splines.Add(new ParsedSpline(s.Name ?? "spline", s.PointCount, points));
            }
        }

        // Stitch fragmented splines back into maximal polylines. Many source
        // pipelines (and the Blender exporter, when the scene has one Curve
        // object per segment) emit every rail/path segment as its OWN 1–2-point
        // spline. Downstream, ArenaBuilder writes a separate spline header per
        // entry, so a path that should be ONE long spline explodes into
        // hundreds of tiny ones — huge PSG bloat. Re-join any fragments whose
        // endpoints coincide into single many-point splines before anyone sees
        // them; geometrically-disjoint splines stay separate.
        var mergedSplines = MergeConnectedSplines(splines);

        // Lights in the .sk8 manifest are intentionally ignored — the editor
        // viewport uses a fixed built-in directional light only.

        return new ParsedSk8(meshes, materialOrder, mergedSplines,
            Generator: manifest.Generator ?? "(unknown)");
    }

    // ── Spline fragment stitching ──────────────────────────────────────────
    //
    // Greedy endpoint-chaining: treat every ≥2-point spline as an open
    // polyline and concatenate any whose endpoints share a position (within
    // <paramref name="eps"/> metres), in either orientation, into the longest
    // possible polyline. O(n) overall via an endpoint→fragment index. Splines
    // with <2 points (degenerate) and any fragment that doesn't connect to
    // anything pass through unchanged, so genuinely separate splines are
    // preserved. A junction where >2 fragments meet resolves greedily — the
    // result is still one continuous polyline per chosen path, which is all
    // ArenaBuilder's per-spline header needs.
    private static List<ParsedSpline> MergeConnectedSplines(
        List<ParsedSpline> input, float eps = 1e-3f)
    {
        if (input.Count <= 1) return input;

        var frags = new List<List<Vector3>>();
        var fragNames = new List<string>();
        var passthrough = new List<ParsedSpline>();
        foreach (ParsedSpline s in input)
        {
            if (s.Points.Count >= 2)
            {
                frags.Add(new List<Vector3>(s.Points));
                fragNames.Add(s.Name);
            }
            else
            {
                passthrough.Add(s);
            }
        }

        int n = frags.Count;
        if (n <= 1)
            return input;

        (long, long, long) Key(Vector3 v) => (
            (long)MathF.Round(v.X / eps),
            (long)MathF.Round(v.Y / eps),
            (long)MathF.Round(v.Z / eps));

        float epsSq = eps * eps * 4f; // small slack over the quantization cell
        bool Close(Vector3 a, Vector3 b) => (a - b).LengthSquared() <= epsSq;

        var node = new Dictionary<(long, long, long), List<int>>();
        void Index((long, long, long) k, int i)
        {
            if (!node.TryGetValue(k, out var l)) { l = new List<int>(); node[k] = l; }
            l.Add(i);
        }
        for (int i = 0; i < n; i++)
        {
            Index(Key(frags[i][0]), i);
            Index(Key(frags[i][^1]), i);
        }

        var used = new bool[n];
        // Find an unused fragment touching `at` (an open polyline endpoint).
        // Scans every candidate sharing the quantized cell and verifies an
        // actual coincidence, so two distinct points that happen to round into
        // the same cell don't shadow a real join.
        int FindAttach(Vector3 at)
        {
            if (!node.TryGetValue(Key(at), out var l)) return -1;
            for (int li = 0; li < l.Count; li++)
            {
                int idx = l[li];
                if (used[idx]) continue;
                var fp = frags[idx];
                if (Close(fp[0], at) || Close(fp[^1], at)) return idx;
            }
            return -1;
        }

        var output = new List<ParsedSpline>(n);
        for (int seed = 0; seed < n; seed++)
        {
            if (used[seed]) continue;
            used[seed] = true;

            var poly = new LinkedList<Vector3>(frags[seed]);
            string baseName = StripFragmentSuffix(fragNames[seed]);
            int contributors = 1;

            // Extend the tail.
            while (true)
            {
                Vector3 tail = poly.Last!.Value;
                int next = FindAttach(tail);
                if (next < 0) break;
                var fp = frags[next];
                if (Close(fp[0], tail))
                {
                    for (int j = 1; j < fp.Count; j++) poly.AddLast(fp[j]);
                }
                else // Close(fp[^1], tail)
                {
                    for (int j = fp.Count - 2; j >= 0; j--) poly.AddLast(fp[j]);
                }
                used[next] = true;
                contributors++;
            }

            // Extend the head.
            while (true)
            {
                Vector3 head = poly.First!.Value;
                int prev = FindAttach(head);
                if (prev < 0) break;
                var fp = frags[prev];
                if (Close(fp[^1], head))
                {
                    for (int j = fp.Count - 2; j >= 0; j--) poly.AddFirst(fp[j]);
                }
                else // Close(fp[0], head)
                {
                    for (int j = 1; j < fp.Count; j++) poly.AddFirst(fp[j]);
                }
                used[prev] = true;
                contributors++;
            }

            var pts = new List<Vector3>(poly);
            string name = contributors > 1 ? baseName : fragNames[seed];
            output.Add(new ParsedSpline(name, pts.Count, pts));
        }

        output.AddRange(passthrough);
        return output;
    }

    private static readonly Regex FragmentSuffix =
        new(@"(?:[/_.\-]pt[/_.\-]?\d+|[/_.\-]\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Strips the exporter's per-fragment suffix (e.g. <c>rail/3</c>,
    /// <c>rail_pt_12</c>, <c>rail.07</c>) so a stitched polyline carries the
    /// shared base name rather than whichever fragment happened to seed it.
    private static string StripFragmentSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return "spline";
        string stripped = FragmentSuffix.Replace(name, "");
        return string.IsNullOrWhiteSpace(stripped) ? name : stripped;
    }

    private static ParsedMesh? DecodeMesh(MeshDto mesh, ManifestDto manifest, ReadOnlySpan<byte> bin)
    {
        if (mesh.VertexCount <= 0 || mesh.IndexCount <= 0) return null;
        if (mesh.Positions is null || mesh.Indices is null) return null;

        // Vertex attributes — positions and indices are required; normals
        // and UVs are optional (we generate fallbacks below if missing).
        float[] positions = ReadFloats(bin, mesh.Positions.Offset, mesh.Positions.Length);
        float[] normals = mesh.Normals != null
            ? ReadFloats(bin, mesh.Normals.Offset, mesh.Normals.Length)
            : new float[positions.Length];
        float[] uvs = mesh.Uvs != null
            ? ReadFloats(bin, mesh.Uvs.Offset, mesh.Uvs.Length)
            : new float[mesh.VertexCount * 2];
        uint[] indices = ReadUInts(bin, mesh.Indices.Offset, mesh.Indices.Length);

        // Bounds
        Vector3 bMin = new(float.PositiveInfinity);
        Vector3 bMax = new(float.NegativeInfinity);
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Vector3 p = new(positions[i * 3 + 0], positions[i * 3 + 1], positions[i * 3 + 2]);
            bMin = Vector3.Min(bMin, p);
            bMax = Vector3.Max(bMax, p);
        }

        // Material → texture lookup
        string materialName = "UNKNOWN";
        byte[]? textureBytes = null;
        Sk8AlphaMode alphaMode = Sk8AlphaMode.Opaque;
        float alphaCutoff = 0.5f;
        if (mesh.Material >= 0 && manifest.Materials != null && mesh.Material < manifest.Materials.Count)
        {
            MaterialDto mat = manifest.Materials[mesh.Material];
            materialName = string.IsNullOrWhiteSpace(mat.Name) ? "UNKNOWN" : mat.Name!;
            alphaMode = ParseAlphaMode(mat.AlphaMode);
            alphaCutoff = mat.AlphaCutoff;
            int tex = mat.BaseColorTexture ?? -1;
            if (tex >= 0 && manifest.Textures != null && tex < manifest.Textures.Count)
            {
                TextureDto td = manifest.Textures[tex];
                if (td.Length > 0 && td.Offset + td.Length <= bin.Length)
                    textureBytes = bin.Slice((int)td.Offset, (int)td.Length).ToArray();
            }
        }

        return new ParsedMesh(
            Name: mesh.Name ?? "mesh",
            MaterialName: materialName,
            PositionsXyz: positions,
            NormalsXyz: normals,
            TexCoordsUv: uvs,
            Indices: indices,
            BoundsMin: bMin,
            BoundsMax: bMax,
            BaseColorImageBytes: textureBytes,
            AlphaMode: alphaMode,
            AlphaCutoff: alphaCutoff);
    }

    private static float[] ReadFloats(ReadOnlySpan<byte> bin, long offset, long byteLength)
    {
        int count = (int)(byteLength / 4);
        var arr = new float[count];
        ReadOnlySpan<byte> src = bin.Slice((int)offset, count * 4);
        for (int i = 0; i < count; i++)
            arr[i] = BitConverter.ToSingle(src.Slice(i * 4, 4));
        return arr;
    }

    private static uint[] ReadUInts(ReadOnlySpan<byte> bin, long offset, long byteLength)
    {
        int count = (int)(byteLength / 4);
        var arr = new uint[count];
        ReadOnlySpan<byte> src = bin.Slice((int)offset, count * 4);
        for (int i = 0; i < count; i++)
            arr[i] = BitConverter.ToUInt32(src.Slice(i * 4, 4));
        return arr;
    }

    private static Sk8AlphaMode ParseAlphaMode(string? raw) => raw?.ToLowerInvariant() switch
    {
        "mask"  => Sk8AlphaMode.Mask,
        "blend" => Sk8AlphaMode.Blend,
        _       => Sk8AlphaMode.Opaque,
    };

    private static Vector3 Vec3FromArray(float[]? a, float defaultX = 0f, float defaultY = 0f, float defaultZ = 0f, float defaultR = 0f, float defaultG = 0f, float defaultB = 0f)
    {
        if (a is null || a.Length < 3) return new Vector3(defaultX, defaultY, defaultZ);
        return new Vector3(a[0], a[1], a[2]);
    }

    // ── Manifest DTOs (snake_case is what the Blender side writes) ─────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class ManifestDto
    {
        public int Version { get; set; }
        public string? Generator { get; set; }
        public List<MeshDto>? Meshes { get; set; }
        public List<MaterialDto>? Materials { get; set; }
        public List<TextureDto>? Textures { get; set; }
        public List<SplineDto>? Splines { get; set; }
    }

    private sealed class MeshDto
    {
        public string? Name { get; set; }
        public int Material { get; set; } = -1;
        public int VertexCount { get; set; }
        public int IndexCount { get; set; }
        public ChunkRef? Positions { get; set; }
        public ChunkRef? Normals { get; set; }
        public ChunkRef? Uvs { get; set; }
        public ChunkRef? Indices { get; set; }
    }

    private sealed class MaterialDto
    {
        public string? Name { get; set; }
        public int? BaseColorTexture { get; set; }
        public string? AlphaMode { get; set; }
        public float AlphaCutoff { get; set; } = 0.5f;
    }

    private sealed class TextureDto
    {
        public string? Name { get; set; }
        public string? Mime { get; set; }
        public long Offset { get; set; }
        public long Length { get; set; }
    }

    private sealed class SplineDto
    {
        public string? Name { get; set; }
        public bool IsClosed { get; set; }
        public int PointCount { get; set; }
        public ChunkRef? Points { get; set; }
    }

    private sealed class ChunkRef
    {
        public long Offset { get; set; }
        public long Length { get; set; }
    }
}
