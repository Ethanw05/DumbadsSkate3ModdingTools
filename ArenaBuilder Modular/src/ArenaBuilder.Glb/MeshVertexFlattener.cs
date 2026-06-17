using System.Numerics;
using System.Threading;
using SharpGLTF.Schema2;

namespace ArenaBuilder.Glb;

/// <summary>
/// Flattens a GLB mesh for mesh PSG. First mesh, first primitive.
/// Vertices are emitted 1:1 with the GLB's vertex buffer — no welding/merging.
/// Node world transforms are applied so tiling and bounds operate in world space.
/// </summary>
public static class MeshVertexFlattener
{
    public sealed record Result(
        IReadOnlyList<Vector3> Positions,
        IReadOnlyList<Vector3> Normals,
        IReadOnlyList<Vector2> Uvs,
        IReadOnlyList<Vector2>? Uvs1,
        IReadOnlyList<int> Indices,
        string MaterialName,
        (Vector3 Min, Vector3 Max) Bounds);

    /// <summary>
    /// Flattens first mesh, first primitive only. Used by single-GLB mesh PSG (e.g. MeshInputFromGlb).
    /// </summary>
    public static Result Flatten(string glbPath)
    {
        var model = ModelRoot.Load(glbPath);
        var list = FlattenAllInternal(model, enforceVertexLimit: true);
        return list[0];
    }

    private static IReadOnlyList<Result> FlattenAllInternal(
        ModelRoot model,
        bool enforceVertexLimit,
        CancellationToken cancellationToken = default)
    {
        if (model?.LogicalMeshes == null || model.LogicalMeshes.Count == 0)
            throw new InvalidOperationException("GLB has no meshes.");

        var list = new List<Result>();

        // Emit every primitive: first from each node's mesh (with node transform), then any mesh not referenced by any node.
        var nodesWithMeshes = model.LogicalNodes.Where(n => n.Mesh != null).ToList();
        var referencedMeshes = new HashSet<Mesh>(nodesWithMeshes.Select(n => n.Mesh!));

        foreach (var node in nodesWithMeshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = node.Mesh!;
            for (int p = 0; p < mesh.Primitives.Count; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(FlattenPrimitiveWithWorldTransform(
                    mesh.Primitives[p],
                    node.WorldMatrix,
                    enforceVertexLimit,
                    cancellationToken));
            }
        }

        // Include meshes that are not referenced by any node (orphan meshes) so every triangle in the GLB is emitted.
        foreach (var mesh in model.LogicalMeshes)
        {
            if (referencedMeshes.Contains(mesh))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            for (int p = 0; p < mesh.Primitives.Count; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(FlattenPrimitiveWithWorldTransform(
                    mesh.Primitives[p],
                    Matrix4x4.Identity,
                    enforceVertexLimit,
                    cancellationToken));
            }
        }

        if (list.Count == 0)
            throw new InvalidOperationException("GLB has no mesh primitives.");
        return list;
    }

    private const int MaxVerticesPerChunk = 65536;

    /// <summary>
    /// Returns one or more Results. If the primitive exceeds 65536 vertices, splits into chunks.
    /// Use for one-mesh-per-primitive PSG with overflow staying in the same file.
    /// </summary>
    public static IReadOnlyList<Result> ChunkResultIfOverflow(Result r)
    {
        if (r.Positions.Count <= MaxVerticesPerChunk)
            return new[] { r };
        return SplitByVertexBudget(r, MaxVerticesPerChunk);
    }

    /// <summary>
    /// Splits a single Result into chunks whose unique vertex count stays within the given budget.
    /// Never splits a triangle across chunks.
    /// </summary>
    public static IReadOnlyList<Result> SplitByVertexBudget(Result r, int maxVerticesPerChunk)
    {
        if (maxVerticesPerChunk <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxVerticesPerChunk), "Vertex budget must be > 0.");
        if (r.Positions.Count <= maxVerticesPerChunk)
            return new[] { r };
        return SplitLargeResult(r, maxVerticesPerChunk);
    }

    /// <summary>
    /// Flattens all primitives; each that exceeds 65536 vertices is split into chunks.
    /// One Result per mesh in the final PSG (one per primitive, plus overflow splits).
    /// </summary>
    public static IReadOnlyList<Result> FlattenAllWithOverflowSplits(string glbPath, CancellationToken cancellationToken = default)
    {
        var model = ModelRoot.Load(glbPath);
        return FlattenAllWithOverflowSplits(model, cancellationToken);
    }

    public static IReadOnlyList<Result> FlattenAllWithOverflowSplits(ModelRoot model, CancellationToken cancellationToken = default)
    {
        // Oversized primitives are expected here; they are split below.
        var all = FlattenAllInternal(model, enforceVertexLimit: false, cancellationToken);
        var result = new List<Result>();
        foreach (var r in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AddRange(ChunkResultIfOverflow(r));
        }
        return result;
    }

    /// <summary>
    /// Splits a single Result that exceeds 65536 vertices into chunks by triangle batches.
    /// Never splits a triangle across chunks: each chunk has valid indices and every triangle is included.
    /// </summary>
    private static IReadOnlyList<Result> SplitLargeResult(Result r, int maxVerticesPerChunk)
    {
        var chunks = new List<Result>();
        // Materialize the IReadOnlyList inputs to concrete arrays. Result's
        // input buffers are always arrays (built via `outPos.ToArray()` in the
        // flatten path), so `AsArray` takes the zero-copy fast branch. Array
        // indexing is direct memory addressing — the JIT can inline and hoist
        // bounds checks across the triangle loop, which a virtual
        // `IReadOnlyList<T>` indexer blocks. For a 1M-triangle chunk pass
        // this is ~6M fewer virtual calls. Arrays (not spans) because the
        // `AddVertLocal` local function below captures them — span is a
        // ref struct and cannot cross a capture boundary.
        Vector3[] pos = AsArray(r.Positions);
        Vector3[] norm = AsArray(r.Normals);
        Vector2[] uv = AsArray(r.Uvs);
        Vector2[]? uv1 = r.Uvs1 != null ? AsArray(r.Uvs1) : null;
        int[] idx = AsArray(r.Indices);

        // Preallocate to the chunk's vertex budget so the inner Add loops
        // don't re-double the backing array 16+ times per chunk. Same for
        // index list (worst case = entire input fits in one chunk). The
        // Dictionary capacity hint avoids rebucketing as we approach budget.
        int idxCount = idx.Length;
        int triCount = idxCount / 3;
        var outPos = new List<Vector3>(maxVerticesPerChunk);
        var outNorm = new List<Vector3>(maxVerticesPerChunk);
        var outUv = new List<Vector2>(maxVerticesPerChunk);
        var outUv1 = uv1 != null ? new List<Vector2>(maxVerticesPerChunk) : null;
        var outIdx = new List<int>(triCount * 3);
        var oldToNew = new Dictionary<int, int>(maxVerticesPerChunk);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        // Inlined emit/add — local methods over byref-captured locals (the
        // previous shape) synthesize a display class and add closure
        // allocations to every call. Inlining keeps everything on the stack.
        for (int i = 0; i < idxCount; i += 3)
        {
            int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
            // Count how many new vertices this triangle would add (so we never split a triangle across chunks).
            int newVerts = (oldToNew.ContainsKey(i0) ? 0 : 1) + (oldToNew.ContainsKey(i1) ? 0 : 1) + (oldToNew.ContainsKey(i2) ? 0 : 1);
            if (outPos.Count + newVerts > maxVerticesPerChunk && outPos.Count != 0)
            {
                chunks.Add(new Result(
                    outPos.ToArray(),
                    outNorm.ToArray(),
                    outUv.ToArray(),
                    outUv1?.ToArray(),
                    outIdx.ToArray(),
                    r.MaterialName,
                    (min, max)));
                outPos.Clear();
                outNorm.Clear();
                outUv.Clear();
                outUv1?.Clear();
                outIdx.Clear();
                oldToNew.Clear();
                min = new Vector3(float.MaxValue);
                max = new Vector3(float.MinValue);
            }
            outIdx.Add(AddVertLocal(i0));
            outIdx.Add(AddVertLocal(i1));
            outIdx.Add(AddVertLocal(i2));
        }
        if (outPos.Count != 0)
        {
            chunks.Add(new Result(
                outPos.ToArray(),
                outNorm.ToArray(),
                outUv.ToArray(),
                outUv1?.ToArray(),
                outIdx.ToArray(),
                r.MaterialName,
                (min, max)));
        }
        return chunks;

        // Local function — captures only the buffers (which are reference
        // types, so no display-class allocation needed when local-function
        // captures are reference-only). Marked here so it stays adjacent
        // to the loop.
        int AddVertLocal(int vi)
        {
            if (oldToNew.TryGetValue(vi, out int n)) return n;
            n = outPos.Count;
            oldToNew[vi] = n;
            outPos.Add(pos[vi]);
            outNorm.Add(norm[vi]);
            outUv.Add(uv[vi]);
            if (outUv1 != null && uv1 != null && vi < uv1.Length)
                outUv1.Add(uv1[vi]);
            var p = pos[vi];
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            return n;
        }
    }

    private static Result FlattenPrimitiveWithWorldTransform(
        MeshPrimitive prim,
        Matrix4x4 world,
        bool enforceVertexLimit,
        CancellationToken cancellationToken = default)
    {
        if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
            throw new InvalidOperationException("Mesh must use TRIANGLES.");

        var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array().ToArray()
            ?? throw new InvalidOperationException("Mesh has no POSITION.");
        var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array().ToArray();

        // BlenRose assigns UVs per texture channel using UV Map nodes.
        // In exported glTF/GLB, each material texture channel can reference a specific TEXCOORD_n via texCoord.
        // We only have two UV streams in our packed vertex layout (TEX0 + TEX1), so we map:
        // - TEX0 := the texCoord used by BaseColor (diffuse) when present, else TEXCOORD_0
        // - TEX1 := the texCoord used by Emissive/Occlusion (lightmap) when present, else TEXCOORD_1 (or TEX0 fallback)
        int diffuseUvIndex = TryGetMaterialChannelTexCoordIndex(prim.Material, "BaseColor") ?? 0;
        int lightmapUvIndex =
            TryGetMaterialChannelTexCoordIndex(prim.Material, "Emissive") ??
            TryGetMaterialChannelTexCoordIndex(prim.Material, "Occlusion") ??
            1;

        Vector2[]? TryReadUvSet(int uvIndex)
        {
            // glTF UVs are TEXCOORD_0, TEXCOORD_1, ...
            var arr = prim.GetVertexAccessor($"TEXCOORD_{uvIndex}")?.AsVector2Array().ToArray();
            if (arr == null || arr.Length < positions.Length)
                return null;
            return arr;
        }

        var uvs = TryReadUvSet(diffuseUvIndex);
        var uvs1 = TryReadUvSet(lightmapUvIndex);

        // Indices — avoid the LINQ Select(i => (int)i).ToArray() pattern which
        // allocates an enumerator on top of the per-element box. Manual fill
        // is one allocation of the right size and a tight loop.
        int[] indices;
        var idxAccessor = prim.GetIndexAccessor();
        if (idxAccessor != null)
        {
            var src = idxAccessor.AsIndicesArray();
            indices = new int[src.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = (int)src[i];
        }
        else
        {
            // Trivial 0..N-1 fill — was Enumerable.Range.ToArray which also
            // allocates the LINQ pipeline objects on top of the array.
            indices = new int[positions.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
        }

        if (world.GetDeterminant() < 0f)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        if (normals == null || normals.Length < positions.Length)
            normals = GenerateDefaultNormals(positions, indices);
        if (uvs == null || uvs.Length < positions.Length)
            // CLR zero-inits new arrays — Vector2 default is (0,0). No need for
            // `Enumerable.Range(...).Select(_ => Vector2.Zero).ToArray()` which
            // allocates the LINQ pipeline AND walks N elements explicitly.
            uvs = new Vector2[positions.Length];
        if (uvs1 == null || uvs1.Length < positions.Length)
            uvs1 = null;

        // 1:1 emission — no welding/merging. Each GLB vertex slot becomes one output vertex,
        // transformed into world space. Input indices already reference these slots, so they
        // pass through unchanged (modulo the winding flip applied above for negative-determinant
        // transforms).
        int posCount = positions.Length;
        int idxCount = indices.Length;
        var outPos = new List<Vector3>(posCount);
        var outNorm = new List<Vector3>(posCount);
        var outUv = new List<Vector2>(posCount);
        var outUv1 = uvs1 != null ? new List<Vector2>(posCount) : null;

        for (int vi = 0; vi < posCount; vi++)
        {
            if ((vi & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var p = positions[vi];
            var n = normals != null && vi < normals.Length ? normals[vi] : Vector3.UnitY;
            var u = uvs != null && vi < uvs.Length ? uvs[vi] : Vector2.Zero;
            outPos.Add(Vector3.Transform(p, world));
            outNorm.Add(Vector3.Normalize(TransformNormal(n, world)));
            outUv.Add(u);
            if (outUv1 != null && uvs1 != null)
                outUv1.Add(vi < uvs1.Length ? uvs1[vi] : u);
        }

        var outIdx = new List<int>(idxCount);
        for (int i = 0; i < indices.Length; i++) outIdx.Add(indices[i]);

        if (enforceVertexLimit && outPos.Count > MaxVerticesPerChunk)
            throw new InvalidOperationException($"Mesh has {outPos.Count} vertices; PSG uint16 indices support max 65536. Consider simplifying the mesh.");

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in outPos)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        string matName = prim.Material?.Name ?? "DefaultMaterial";
        return new Result(outPos, outNorm, outUv, outUv1, outIdx, matName, (min, max));
    }

    private static int? TryGetMaterialChannelTexCoordIndex(Material? material, string glbChannelName)
    {
        if (material == null) return null;
        object? channel = material.FindChannel(glbChannelName);
        if (channel == null) return null;

        // SharpGLTF stores the texCoord index on the channel's texture info, not on the Texture itself.
        // Use reflection so we don't hard-depend on a specific SharpGLTF API shape.
        // Common property names across versions: TextureCoordinate, TexCoord, TexCoordIndex.
        var t = channel.GetType();
        foreach (var propName in new[] { "TextureCoordinate", "TexCoord", "TexCoordIndex" })
        {
            var p = t.GetProperty(propName);
            if (p == null) continue;
            try
            {
                var v = p.GetValue(channel);
                if (v is int i) return i;
                if (v is byte b) return b;
                if (v is short s) return s;
            }
            catch
            {
                // ignore and keep trying
            }
        }

        // Some SharpGLTF versions expose the texture info object, which then contains TexCoord.
        var textureInfoProp = t.GetProperty("TextureInfo") ?? t.GetProperty("Texture");
        if (textureInfoProp != null)
        {
            try
            {
                var ti = textureInfoProp.GetValue(channel);
                if (ti != null)
                {
                    var tiType = ti.GetType();
                    var p = tiType.GetProperty("TexCoord") ?? tiType.GetProperty("TextureCoordinate") ?? tiType.GetProperty("TexCoordIndex");
                    if (p != null)
                    {
                        var v = p.GetValue(ti);
                        if (v is int i) return i;
                        if (v is byte b) return b;
                        if (v is short s) return s;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the underlying <c>T[]</c> when an <see cref="IReadOnlyList{T}"/>
    /// is array-backed (the common case for <see cref="Result"/>'s buffers,
    /// which are constructed via <c>outPos.ToArray()</c>). Falls back to
    /// materialization for any other implementation. Used to convert the
    /// public IReadOnlyList surface into a concrete array so hot triangle
    /// loops can use array indexers (which the JIT can inline) instead of
    /// the virtual <c>IReadOnlyList&lt;T&gt;.this[int]</c> dispatch.
    /// </summary>
    private static T[] AsArray<T>(IReadOnlyList<T> list)
    {
        if (list is T[] arr) return arr;
        var copy = new T[list.Count];
        if (list is List<T> l)
            l.CopyTo(copy);
        else
            for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
        return copy;
    }

private static Vector3[] GenerateDefaultNormals(Vector3[] positions, int[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            var p0 = positions[i0]; var p1 = positions[i1]; var p2 = positions[i2];
            var n = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            normals[i0] += n;
            normals[i1] += n;
            normals[i2] += n;
        }
        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared() > 1e-9f)
                normals[i] = Vector3.Normalize(normals[i]);
            else
                normals[i] = Vector3.UnitY;
        }
        return normals;
    }

    private static Vector3 TransformNormal(Vector3 n, Matrix4x4 world)
    {
        return Vector3.Normalize(new Vector3(
            n.X * world.M11 + n.Y * world.M21 + n.Z * world.M31,
            n.X * world.M12 + n.Y * world.M22 + n.Z * world.M32,
            n.X * world.M13 + n.Y * world.M23 + n.Z * world.M33));
    }

}
