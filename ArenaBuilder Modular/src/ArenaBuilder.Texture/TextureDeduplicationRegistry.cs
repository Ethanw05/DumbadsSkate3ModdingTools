using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture.Dds;
using System.Collections.Concurrent;
using System.IO.Hashing;

namespace ArenaBuilder.Texture;

/// <summary>
/// Global texture registry for deduplicating identical texture payloads during export.
/// Textures are identified by hash of their final DDS payload; identical payloads share one PSG.
/// Lightmaps are not forced unique—they dedupe naturally when lighting is identical.
///
/// Also owns the per-build "encode-once" cache (<see cref="GetOrEncode"/>): byte-identical source
/// images encoded under the same flags reuse one parsed <see cref="DdsTextureInput"/>, so the
/// expensive BCn encode runs at most once per unique source/flag combination even when the same
/// texture is emitted into many cPres tiles under per-tile dedup scopes.
/// </summary>
public sealed class TextureDeduplicationRegistry
{
    private readonly ConcurrentDictionary<string, GlobalTexture> _texturesByKey = new(StringComparer.Ordinal);
    private readonly object _listLock = new();
    private readonly List<GlobalTexture> _globalTextures = new();
    /// <summary>
    /// Encode-once cache: SHA256(sourceBytes) + encoding flags → parsed DDS. Uses <see cref="Lazy{T}"/>
    /// with execution-and-publication safety so concurrent callers never run BCn encode more than once
    /// per key. Cleared in <see cref="ReleaseExportedPayloadBytes"/> at end of build.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<DdsTextureInput>> _encodedSourceCache = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;
    private int _nextIndex;
    private long _encodeCacheHits;
    private long _encodeCacheMisses;

    public TextureDeduplicationRegistry(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>Number of encode cache hits — i.e. BCn encodes skipped because a matching source had already been encoded in this build.</summary>
    public long EncodeCacheHits => Interlocked.Read(ref _encodeCacheHits);

    /// <summary>Number of unique encode results stored in the cache (one per source/flag combination encoded so far).</summary>
    public long EncodeCacheMisses => Interlocked.Read(ref _encodeCacheMisses);

    /// <summary>
    /// Returns a parsed <see cref="DdsTextureInput"/> for the given source image, encoding it on first
    /// use and caching the result. Subsequent calls with byte-identical <paramref name="encodedImageBytes"/>
    /// AND identical encoding flags return the cached <c>DdsTextureInput</c> without re-running BCn.
    ///
    /// This is the single biggest tile-build speedup: when a base texture is referenced by N cPres tiles
    /// under per-tile dedup scopes, we previously ran <see cref="ImageToDdsConverter.ConvertToDds"/> N
    /// times to produce N byte-identical DDS payloads. Now we encode once and let the per-tile
    /// <see cref="RegisterOrReuse"/> calls compose+write distinct PSGs from the same parsed bytes.
    /// </summary>
    /// <param name="encodedImageBytes">Source image bytes (PNG/JPG/DDS).</param>
    /// <param name="sourceIsDds">True when <paramref name="encodedImageBytes"/> is already DDS (skips BCn encode entirely).</param>
    /// <param name="generateMipMaps">Whether the BCn encode should generate a mip chain.</param>
    /// <param name="preserveAlpha">When true, encode as BC3 (DXT5); otherwise BC1 (DXT1).</param>
    /// <param name="forceOpaqueAlpha">Discard source alpha before encoding (used for normal channels).</param>
    /// <param name="reduceBc1NormalArtifacts">Slow but high-quality BC1 path used when encoding normals.</param>
    public DdsTextureInput GetOrEncode(
        byte[] encodedImageBytes,
        bool sourceIsDds,
        bool generateMipMaps,
        bool preserveAlpha,
        bool forceOpaqueAlpha,
        bool reduceBc1NormalArtifacts,
        int? maxDimension = null)
    {
        if (encodedImageBytes == null || encodedImageBytes.Length == 0)
            throw new ArgumentException("Image bytes are required.", nameof(encodedImageBytes));

        // xxHash128 (non-cryptographic) — process-local cache key only, never
        // persisted across builds. ~10-20x faster than SHA256 on multi-MB
        // PNG/DDS payloads with no collision-resistance concerns (a true
        // 128-bit collision is astronomically unlikely in a single build).
        // 16-byte hash → 32-char hex; same string-key shape as before.
        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(encodedImageBytes, hash);
        string srcHashHex = Convert.ToHexString(hash);
        string sizeTag = maxDimension is int md && md > 0 ? $"|s={md}" : "";
        string key = sourceIsDds
            ? "dds|" + srcHashHex + sizeTag
            : $"img|{srcHashHex}|m={(generateMipMaps ? 1 : 0)}|a={(preserveAlpha ? 1 : 0)}|o={(forceOpaqueAlpha ? 1 : 0)}|n={(reduceBc1NormalArtifacts ? 1 : 0)}{sizeTag}";

        bool createdNewLazy = false;
        var lazy = _encodedSourceCache.GetOrAdd(key, _ =>
        {
            createdNewLazy = true;
            return new Lazy<DdsTextureInput>(() =>
            {
                Interlocked.Increment(ref _encodeCacheMisses);
                if (sourceIsDds && maxDimension is null)
                    return DdsReader.Read(encodedImageBytes);

                byte[] ddsBytes = ImageToDdsConverter.ConvertToDds(
                    encodedImageBytes,
                    generateMipMaps,
                    preserveAlpha,
                    forceOpaqueAlpha,
                    reduceBc1NormalArtifacts,
                    maxDimension);
                return DdsReader.Read(ddsBytes);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        });

        if (!createdNewLazy)
            Interlocked.Increment(ref _encodeCacheHits);

        return lazy.Value;
    }

    /// <summary>
    /// Format for dedupe key: <c>scope|format|width|height|mipCount|payloadHashHex</c> when
    /// <paramref name="scope"/> is non-empty, otherwise <c>format|width|height|mipCount|payloadHashHex</c>.
    /// Ensures textures with identical bytes but different metadata do not collide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The optional <paramref name="scope"/> isolates dedup namespaces. Two textures with byte-identical
    /// payloads but different scopes get different keys → different GUIDs → independent PSG files.
    /// In <see cref="ArenaBuilder.Build.TileBuildPipeline"/> we use this for per-cPres-tile scoping:
    /// each cPres_X_Y_high tile is a fully self-contained collection (mesh + textures), so byte-identical
    /// texture content used across multiple cPres tiles gets a UNIQUE GUID per tile. This way the
    /// engine's asset manager (<c>cAssetStreamManager</c>, ~31 bits of HIDWORD entropy) never sees the
    /// same texture GUID re-registered from a sibling tile during a load/unload/reload cycle, which is
    /// what caused the texture-shuffling on screen with the previous shared-GUID schemes.
    /// </para>
    /// </remarks>
    public static string BuildDedupeKey(
        byte ps3Format,
        int width,
        int height,
        int mipCount,
        byte[] payload,
        string? scope = null)
    {
        // xxHash128 — same rationale as GetOrEncode: in-memory cache key,
        // no cryptographic property required, much faster on the multi-MB
        // DDS payloads we hash here per registered texture.
        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(payload, hash);
        string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
        string scopePrefix = string.IsNullOrEmpty(scope) ? "" : scope + "|";
        return $"{scopePrefix}{ps3Format:X2}|{width}|{height}|{mipCount}|{hashHex}";
    }

    private readonly ConcurrentDictionary<string, object> _pathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _writtenLogicalPaths = new(StringComparer.OrdinalIgnoreCase);
    private long _logicalWritesAttempted;
    private long _logicalWritesSkippedExists;

    /// <summary>Number of <see cref="WriteLogicalGuidPsg"/> calls made.</summary>
    public long LogicalWritesAttempted => Interlocked.Read(ref _logicalWritesAttempted);

    /// <summary>Number of logical-GUID PSG writes skipped because the same path was already written this build.</summary>
    public long LogicalWritesSkipped => Interlocked.Read(ref _logicalWritesSkippedExists);

    /// <summary>
    /// Logical-GUID write path used for the dual-tier (cPres-small + cTex-full) texture scheme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bypasses the payload-hash dedup map. The caller passes a pre-computed
    /// <paramref name="logicalGuid"/>: either the FULL-variant GUID <c>G</c>
    /// (bit 62 clear, used in cTex tiles and GlobalOnly) or the SMALL-variant GUID
    /// <c>G | 0x4000_0000_0000_0000</c> (bit 62 set, used in cPres tiles for the fallback copy).
    /// Mesh material channels always reference <c>G</c>; the engine retries with bit 62 set on a
    /// primary lookup miss to resolve the cPres-side fallback.
    /// </para>
    /// <para>
    /// Idempotent per (path) within a build: re-emitting the same GUID into the same directory
    /// is a no-op after the first write, so multiple meshes in the same cPres tile that reference
    /// the same texture only write the PSG once.
    /// </para>
    /// </remarks>
    public GlbTextureAutoBuilder.BuiltTexturePsg WriteLogicalGuidPsg(
        DdsTextureInput ddsInput,
        ulong logicalGuid,
        string channelName,
        string sourceImageName,
        string outputDirectory)
    {
        Interlocked.Increment(ref _logicalWritesAttempted);
        string psgPath = Path.Combine(outputDirectory, $"{logicalGuid:X16}.psg");

        // Track first-writer per path to avoid redundant compose+write when the same logical GUID
        // is requested multiple times for the same directory (multiple meshes referencing the same
        // texture inside one cPres tile).
        if (!_writtenLogicalPaths.TryAdd(psgPath, 0))
        {
            Interlocked.Increment(ref _logicalWritesSkippedExists);
            return new GlbTextureAutoBuilder.BuiltTexturePsg(
                channelName,
                sourceImageName,
                ddsInput.Width,
                ddsInput.Height,
                logicalGuid,
                psgPath);
        }

        var spec = TexturePsgComposer.Compose(ddsInput, logicalGuid);
        object pathLock = _pathLocks.GetOrAdd(psgPath, _ => new object());
        lock (pathLock)
        {
            using var fs = File.Create(psgPath);
            GenericArenaWriter.Write(spec, fs);
        }

        return new GlbTextureAutoBuilder.BuiltTexturePsg(
            channelName,
            sourceImageName,
            ddsInput.Width,
            ddsInput.Height,
            logicalGuid,
            psgPath);
    }

    /// <summary>
    /// Registers a texture or reuses an existing one with identical payload (and matching <paramref name="scope"/>).
    /// Returns the BuiltTexturePsg; writes the PSG file only when the texture is new in this scope.
    /// </summary>
    /// <param name="ddsInput">Parsed DDS (payload is the final exported bytes).</param>
    /// <param name="channelName">Channel name for logging (e.g. diffuse, normal).</param>
    /// <param name="sourceImageName">Source image name for logging.</param>
    /// <param name="outputDirectory">Directory to write PSG when texture is new.</param>
    /// <param name="scope">
    /// Optional dedup scope. Textures sharing identical payloads dedupe only within the same scope.
    /// Used by the tile builder to give each cPres tile its own GUID-set so the engine never re-registers
    /// the same texture GUID from sibling cPres tiles. Pass <c>null</c> for global dedup (e.g. GlobalOnly mode).
    /// </param>
    public GlbTextureAutoBuilder.BuiltTexturePsg RegisterOrReuse(
        DdsTextureInput ddsInput,
        string channelName,
        string sourceImageName,
        string outputDirectory,
        string? scope = null)
    {
        string key = BuildDedupeKey(
            ddsInput.Ps3Format,
            ddsInput.Width,
            ddsInput.Height,
            ddsInput.MipCount,
            ddsInput.Payload,
            scope);

        if (_texturesByKey.TryGetValue(key, out var existing))
        {
            _log?.Invoke($"[TextureDeduper] Duplicate texture detected. Source: {sourceImageName}. Reusing texture index {existing.Index} (GUID 0x{existing.Guid:X16}).");
            string resultPath = existing.PsgPath;
            string targetPath = Path.Combine(outputDirectory, $"{existing.Guid:X16}.psg");
            string existingDir = Path.GetFullPath(Path.GetDirectoryName(resultPath) ?? "");
            string targetDir = Path.GetFullPath(outputDirectory);
            if (!string.Equals(existingDir, targetDir, StringComparison.OrdinalIgnoreCase) && File.Exists(resultPath))
            {
                Directory.CreateDirectory(outputDirectory);
                File.Copy(resultPath, targetPath, overwrite: true);
                resultPath = targetPath;
            }
            return new GlbTextureAutoBuilder.BuiltTexturePsg(
                channelName,
                sourceImageName,
                existing.Width,
                existing.Height,
                existing.Guid,
                resultPath);
        }

        ulong guid = TextureGuidStrategy.KeyToGuid(key);
        string psgPath = Path.Combine(outputDirectory, $"{guid:X16}.psg");

        var globalTex = new GlobalTexture
        {
            Index = Interlocked.Increment(ref _nextIndex) - 1,
            Key = key,
            Guid = guid,
            PsgPath = psgPath,
            Width = ddsInput.Width,
            Height = ddsInput.Height,
            MipCount = ddsInput.MipCount,
            Format = ddsInput.Ps3Format,
            DdsInput = ddsInput
        };

        if (!_texturesByKey.TryAdd(key, globalTex))
        {
            var raced = _texturesByKey[key];
            _log?.Invoke($"[TextureDeduper] Duplicate texture detected (race). Source: {sourceImageName}. Reusing texture index {raced.Index} (GUID 0x{raced.Guid:X16}).");
            string resultPath = raced.PsgPath;
            string targetPath = Path.Combine(outputDirectory, $"{raced.Guid:X16}.psg");
            string existingDir = Path.GetFullPath(Path.GetDirectoryName(resultPath) ?? "");
            string targetDir = Path.GetFullPath(outputDirectory);
            if (!string.Equals(existingDir, targetDir, StringComparison.OrdinalIgnoreCase) && File.Exists(resultPath))
            {
                Directory.CreateDirectory(outputDirectory);
                File.Copy(resultPath, targetPath, overwrite: true);
                resultPath = targetPath;
            }
            return new GlbTextureAutoBuilder.BuiltTexturePsg(
                channelName,
                sourceImageName,
                raced.Width,
                raced.Height,
                raced.Guid,
                resultPath);
        }

        lock (_listLock)
        {
            _globalTextures.Add(globalTex);
        }

        var spec = TexturePsgComposer.Compose(ddsInput, guid);
        object pathLock = _pathLocks.GetOrAdd(psgPath, _ => new object());
        lock (pathLock)
        {
            using var fs = File.Create(psgPath);
            GenericArenaWriter.Write(spec, fs);
        }

        return new GlbTextureAutoBuilder.BuiltTexturePsg(
            channelName,
            sourceImageName,
            ddsInput.Width,
            ddsInput.Height,
            guid,
            psgPath);
    }

    /// <summary>
    /// Total number of unique textures registered.
    /// </summary>
    public int UniqueCount => _texturesByKey.Count;

    /// <summary>
    /// Releases DDS payload bytes retained for each unique texture after PSG files are on disk and
    /// clears the encode-once cache. Call at end of a large build so the process does not keep
    /// hundreds of MB–GB of texture data alive.
    /// </summary>
    public void ReleaseExportedPayloadBytes()
    {
        lock (_listLock)
        {
            foreach (var gt in _globalTextures)
                gt.DdsInput.ReleasePayloadBytes();
        }
        // Encode cache may hold the only refs to DdsTextureInputs that came from the logical-GUID
        // pathway (no entry in _globalTextures). Release their payload bytes before clearing.
        foreach (var lazy in _encodedSourceCache.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value.ReleasePayloadBytes();
        }
        _encodedSourceCache.Clear();
        _writtenLogicalPaths.Clear();
    }

    private sealed class GlobalTexture
    {
        public int Index { get; init; }
        public string Key { get; init; } = "";
        public ulong Guid { get; init; }
        public string PsgPath { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public int MipCount { get; init; }
        public byte Format { get; init; }
        public DdsTextureInput DdsInput { get; init; } = null!;
    }
}
