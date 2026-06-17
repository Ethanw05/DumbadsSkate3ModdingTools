using SharpGLTF.Schema2;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ArenaBuilder.Glb;

/// <summary>
/// Flattens a GLB scene into:
/// - a vertex list welded per node/mesh (no cross-node welding)
/// - a triangle index list referencing that vertex list
///
/// IMPORTANT:
/// Blender's collision exporter uses mesh vertex identity, not "positions within epsilon".
/// To avoid accidentally welding vertices across separate nodes/objects, we do not de-dupe
/// across the whole scene here. We only weld within a single node's mesh (across its primitives).
/// </summary>
public static class GlbMeshFlattener
{
    /// <summary>
    /// Load and flatten a GLB from disk.
    /// </summary>
    public static GlbFlattenResult Flatten(string glbPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(glbPath))
            throw new ArgumentException("GLB path is required.", nameof(glbPath));

        var model = ModelRoot.Load(glbPath);
        return Flatten(model, cancellationToken);
    }

    /// <summary>
    /// Flatten an already-loaded model.
    /// </summary>
    public static GlbFlattenResult Flatten(ModelRoot model, CancellationToken cancellationToken = default)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        var verts = new List<Vector3>();
        var faces = new List<(int, int, int)>();
        string materialName = model.LogicalMaterials.FirstOrDefault()?.Name ?? "UNKNOWN";

        foreach (var scene in model.LogicalScenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var node in scene.VisualChildren)
                FlattenNode(node, ref materialName, verts, faces, cancellationToken);
        }

        if (verts.Count == 0 || faces.Count == 0)
            throw new InvalidOperationException("GLB contained no triangles.");

        return new GlbFlattenResult(verts, faces, materialName);
    }

    private static void FlattenNode(
        Node node,
        ref string materialName,
        List<Vector3> verts,
        List<(int, int, int)> faces,
        CancellationToken cancellationToken)
    {
        var world = node.WorldMatrix;
        // Negative signed volume of transformed basis = reflection; flip winding for consistent face orientation.
        var bx = Vector3.TransformNormal(Vector3.UnitX, world);
        var by = Vector3.TransformNormal(Vector3.UnitY, world);
        var bz = Vector3.TransformNormal(Vector3.UnitZ, world);
        bool mirroredTransform = Vector3.Dot(Vector3.Cross(bx, by), bz) < 0f;
        if (node.Mesh != null)
        {
            // De-dupe vertices only within THIS node (object), across its primitives.
            // This better matches Blender mesh vertex identity than global welding.
            var localVertexMap = new Dictionary<AccessorIndexKey, int>(capacity: 4096);

            foreach (var prim in node.Mesh.Primitives)
            {
                // Only support triangle primitives (Blender GLB export should be TRIANGLES).
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                    continue;

                if (prim.Material != null && !string.IsNullOrWhiteSpace(prim.Material.Name))
                    materialName = prim.Material.Name;

                var pos = prim.GetVertexAccessor("POSITION");
                if (pos == null) continue;
                var posArr = pos.AsVector3Array();

                var idx = prim.GetIndexAccessor();
                if (idx == null) continue;
                var indices = idx.AsIndicesArray().ToArray();
                if (indices.Length % 3 != 0) continue;

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int i0 = GetOrAddVertex(posArr, indices[i], pos, world, verts, localVertexMap);
                    int i1 = GetOrAddVertex(posArr, indices[i + 1], pos, world, verts, localVertexMap);
                    int i2 = GetOrAddVertex(posArr, indices[i + 2], pos, world, verts, localVertexMap);
                    faces.Add(mirroredTransform ? (i0, i2, i1) : (i0, i1, i2));
                }
            }
        }

        foreach (var child in node.VisualChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlattenNode(child, ref materialName, verts, faces, cancellationToken);
        }
    }

    private static int GetOrAddVertex(
        IList<Vector3> posArr,
        long index,
        Accessor posAccessor,
        Matrix4x4 world,
        List<Vector3> verts,
        Dictionary<AccessorIndexKey, int> localVertexMap)
    {
        int i = checked((int)index);
        var key = new AccessorIndexKey(posAccessor, i);
        if (localVertexMap.TryGetValue(key, out int existing))
            return existing;

        var p = posArr[i];
        var tp = Vector3.Transform(p, world);
        int newIndex = verts.Count;
        verts.Add(tp);
        localVertexMap[key] = newIndex;
        return newIndex;
    }

    private readonly record struct AccessorIndexKey(Accessor Accessor, int Index)
    {
        public bool Equals(AccessorIndexKey other)
            => ReferenceEquals(Accessor, other.Accessor) && Index == other.Index;

        public override int GetHashCode()
            => HashCode.Combine(RuntimeHelpers.GetHashCode(Accessor), Index);
    }
}

