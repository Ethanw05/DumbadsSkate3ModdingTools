using ArenaBuilder.Glb;

namespace ArenaBuilder.Build;

/// <summary>
/// Budget-driven texture tier placement — port of BlenRose.py's
/// <c>_write_texture_psgs</c> promotion + per-cTex budget passes
/// (≈lines 5141-5495).
///
/// <para>The legacy ArenaBuilder policy emitted a small fallback into every
/// cPres tile + a full copy into a heuristic union of cTex tiles, with no
/// byte budget — a code comment even admitted "AABB alone can miss the
/// streamed collection." BlenRose instead:</para>
///
/// <list type="number">
/// <item><b>Promotion:</b> a texture used by ≥ <see cref="MinTilesToPromote"/>
/// distinct cPres tiles (and not a lightmap) is a candidate to live once in a
/// shared <c>cPres_Global/</c> collection. Candidates are ranked
/// <c>(-cPresTileCount, -payloadSize, guid)</c> and admitted while the
/// running total stays under <see cref="GlobalBudgetBytes"/>. A promoted
/// texture is written exactly once (full-res, full GUID) and never as a
/// per-tile small fallback nor into cTex.</item>
/// <item><b>Per-cTex budget:</b> for each cTex tile, the non-promoted
/// non-lightmap textures whose cTex-candidate set includes it are ranked
/// <c>(-localCPresUsers, -globalTileUsers, -payloadSize, guid)</c> and
/// admitted while the per-tile total stays under
/// <see cref="CTexBudgetBytes"/>. Overflow is <i>demoted</i> (full-res into
/// its cPres users) rather than dropped.</item>
/// <item><b>Resulting cPres mode:</b> a texture kept in ≥1 cTex tile that
/// covers a given cPres tile gets the small fallback there; a
/// promoted/demoted/no-cTex texture gets full-res in its cPres tiles.
/// Lightmaps are always full-res in every cPres tile, never promoted,
/// never cTex.</item>
/// </list>
/// </summary>
public static class TexturePlacementPlanner
{
    /// <summary>cPres_Global shared-collection byte budget. BlenRose
    /// <c>GLOBAL_BUDGET_BYTES</c> = 20 MiB (its log line says 15 MiB but the
    /// code uses 20 — we follow the code).</summary>
    public const long GlobalBudgetBytes = 20L * 1024 * 1024;

    /// <summary>Per-cTex tile byte budget. BlenRose <c>CTEX_BUDGET_BYTES</c> = 10 MiB.</summary>
    public const long CTexBudgetBytes = 10L * 1024 * 1024;

    /// <summary>A texture must be used by at least this many distinct cPres
    /// tiles to be a promotion candidate. BlenRose <c>MIN_TILES_TO_PROMOTE</c> = 2.</summary>
    public const int MinTilesToPromote = 2;

    /// <summary>One logical texture = one full GUID. Aggregated across every
    /// GLB / material / tile that references it.</summary>
    public sealed class LogicalTexture
    {
        public ulong FullGuid;
        public long PayloadSize;
        public bool IsLightmap;
        public readonly HashSet<WorldTileGrid.TileKey> CPresTiles = new();
        public readonly HashSet<WorldTileGrid.CTexTileKey> CTexCandidates = new();
    }

    public sealed class Plan
    {
        /// <summary>GUIDs emitted once into <c>cPres_Global/</c> (full-res,
        /// full GUID) and nowhere else.</summary>
        public readonly HashSet<ulong> Promoted = new();

        /// <summary>Per logical-texture GUID, the cTex tiles that keep a
        /// full-res copy (passed the per-cTex budget).</summary>
        public readonly Dictionary<ulong, HashSet<WorldTileGrid.CTexTileKey>> CTexKept = new();

        /// <summary>True when <paramref name="fullGuid"/> has a full-res copy
        /// in some cTex tile that covers <paramref name="presTile"/> — i.e.
        /// the engine's primary lookup can stream the full texture, so the
        /// resident cPres tile only needs the SMALL fallback. False → the
        /// cPres tile must carry the FULL texture (promoted textures excluded
        /// — they resolve from cPres_Global).</summary>
        public bool CPresWantsSmall(ulong fullGuid, WorldTileGrid.TileKey presTile)
        {
            if (Promoted.Contains(fullGuid)) return false;
            if (!CTexKept.TryGetValue(fullGuid, out var kept) || kept.Count == 0)
                return false;
            foreach (var ctex in WorldTileGrid.GetCTexCandidatesForPresTile(presTile))
                if (kept.Contains(ctex)) return true;
            return false;
        }

        /// <summary>Does <paramref name="fullGuid"/> keep a full-res copy in
        /// cTex tile <paramref name="ctex"/>?</summary>
        public bool CTexKeepsFull(ulong fullGuid, WorldTileGrid.CTexTileKey ctex)
            => !Promoted.Contains(fullGuid)
               && CTexKept.TryGetValue(fullGuid, out var kept)
               && kept.Contains(ctex);
    }

    /// <summary>
    /// Compute the placement plan from the aggregated logical-texture set.
    /// Pure function — no I/O. <paramref name="log"/> receives one summary
    /// line per pass.
    /// </summary>
    public static Plan Build(IReadOnlyCollection<LogicalTexture> textures, Action<string> log)
    {
        var plan = new Plan();

        // ── Pass 1: promotion to cPres_Global ─────────────────────────────
        var promoteCandidates = new List<LogicalTexture>();
        foreach (var t in textures)
            if (!t.IsLightmap && t.CPresTiles.Count >= MinTilesToPromote)
                promoteCandidates.Add(t);

        promoteCandidates.Sort((a, b) =>
        {
            int c = b.CPresTiles.Count.CompareTo(a.CPresTiles.Count); // -tileCount
            if (c != 0) return c;
            c = b.PayloadSize.CompareTo(a.PayloadSize);               // -payloadSize
            if (c != 0) return c;
            return a.FullGuid.CompareTo(b.FullGuid);                  // guid asc (stable)
        });

        long globalTotal = 0;
        int promoted = 0;
        foreach (var t in promoteCandidates)
        {
            if (globalTotal + t.PayloadSize > GlobalBudgetBytes) continue;
            globalTotal += t.PayloadSize;
            plan.Promoted.Add(t.FullGuid);
            promoted++;
        }
        log($"[TexPlan] Promotion: {promoted}/{promoteCandidates.Count} candidate(s) into cPres_Global " +
            $"({globalTotal / 1024} KiB / {GlobalBudgetBytes / 1024} KiB budget).");

        // ── Pass 2: per-cTex budget pack ──────────────────────────────────
        // Gather, per cTex tile, the non-promoted non-lightmap textures whose
        // candidate set includes it.
        var byCTex = new Dictionary<WorldTileGrid.CTexTileKey, List<LogicalTexture>>();
        foreach (var t in textures)
        {
            if (t.IsLightmap) continue;
            if (plan.Promoted.Contains(t.FullGuid)) continue;
            foreach (var ctex in t.CTexCandidates)
            {
                if (!byCTex.TryGetValue(ctex, out var list))
                {
                    list = new List<LogicalTexture>();
                    byCTex[ctex] = list;
                }
                list.Add(t);
            }
        }

        int ctexKeptTotal = 0, ctexDemotedTotal = 0;
        foreach (var (ctex, list) in byCTex)
        {
            // localCPresUsers = how many of this texture's cPres tiles this
            // cTex tile actually covers (higher = more useful to keep here).
            int LocalUsers(LogicalTexture t)
            {
                int n = 0;
                foreach (var pt in t.CPresTiles)
                    if (WorldTileGrid.CTexCoversPresTile(ctex, pt)) n++;
                return n;
            }

            list.Sort((a, b) =>
            {
                int c = LocalUsers(b).CompareTo(LocalUsers(a));        // -localCPresUsers
                if (c != 0) return c;
                c = b.CPresTiles.Count.CompareTo(a.CPresTiles.Count);  // -globalTileUsers
                if (c != 0) return c;
                c = b.PayloadSize.CompareTo(a.PayloadSize);            // -payloadSize
                if (c != 0) return c;
                return a.FullGuid.CompareTo(b.FullGuid);
            });

            long tileTotal = 0;
            foreach (var t in list)
            {
                if (tileTotal + t.PayloadSize > CTexBudgetBytes)
                {
                    ctexDemotedTotal++;
                    continue; // overflow → demoted (full-res into its cPres users)
                }
                tileTotal += t.PayloadSize;
                if (!plan.CTexKept.TryGetValue(t.FullGuid, out var keptSet))
                {
                    keptSet = new HashSet<WorldTileGrid.CTexTileKey>();
                    plan.CTexKept[t.FullGuid] = keptSet;
                }
                keptSet.Add(ctex);
                ctexKeptTotal++;
            }
        }
        log($"[TexPlan] cTex budget: {ctexKeptTotal} keep slot(s), {ctexDemotedTotal} demotion(s) " +
            $"to full-res-in-cPres ({CTexBudgetBytes / 1024} KiB per-cTex budget).");

        return plan;
    }
}
