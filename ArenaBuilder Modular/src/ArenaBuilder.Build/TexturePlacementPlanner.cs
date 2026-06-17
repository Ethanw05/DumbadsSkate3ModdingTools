using ArenaBuilder.Glb;

namespace ArenaBuilder.Build;

/// <summary>
/// Texture placement planner. A texture used by ≥ <see cref="MinTilesToPromote"/>
/// distinct cPres tiles (and not a lightmap) is promoted to live once in the
/// shared <c>cPres_Global/</c> collection. Non-promoted textures are emitted
/// full-res into their owning cPres tile. Lightmaps are always full-res in
/// every owning cPres tile and never promoted.
/// </summary>
public static class TexturePlacementPlanner
{
    /// <summary>A texture must be used by at least this many distinct cPres
    /// tiles to be a promotion candidate.</summary>
    public const int MinTilesToPromote = 2;

    /// <summary>One logical texture = one full GUID. Aggregated across every
    /// GLB / material / tile that references it.</summary>
    public sealed class LogicalTexture
    {
        public ulong FullGuid;
        public long PayloadSize;
        public bool IsLightmap;
        public readonly HashSet<WorldTileGrid.TileKey> CPresTiles = new();
    }

    public sealed class Plan
    {
        /// <summary>GUIDs emitted once into <c>cPres_Global/</c> (full-res,
        /// full GUID) and nowhere else.</summary>
        public readonly HashSet<ulong> Promoted = new();
    }

    /// <summary>
    /// Compute the placement plan from the aggregated logical-texture set.
    /// Pure function — no I/O. <paramref name="log"/> receives one summary line.
    /// </summary>
    public static Plan Build(IReadOnlyCollection<LogicalTexture> textures, Action<string> log)
    {
        var plan = new Plan();

        int candidates = 0;
        long globalTotal = 0;
        foreach (var t in textures)
        {
            if (t.IsLightmap) continue;
            if (t.CPresTiles.Count < MinTilesToPromote) continue;
            candidates++;
            plan.Promoted.Add(t.FullGuid);
            globalTotal += t.PayloadSize;
        }
        log($"[TexPlan] Promotion: {plan.Promoted.Count}/{candidates} candidate(s) into cPres_Global " +
            $"({globalTotal / 1024} KiB).");

        return plan;
    }
}
