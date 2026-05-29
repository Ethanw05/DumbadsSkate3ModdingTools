using System.IO;
using System.Text;
using DlcBuilder.Builders;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest;

namespace DlcBuilder.Modules.FeLang;

/// `LANGUAGE_English.bin` — the FE string table the engine reads to resolve
/// HAL ids to display text. Layout:
///
///   header (8 bytes LE):
///     u32 magic         (0x00039000)
///     u32 payload_len
///   payload:
///     u32 LE entry_count
///     u32 LE strings_pool_start (rel to payload start)
///     u32 LE strings_pool_end   (rel to payload start)
///     ... package_slug bytes (rounded up to 4-byte multiple, ≥16) ...
///     entry_count × { u32 LE hash; u32 LE rel_offset_in_strings_pool; }
///     ...string blob (cstrings)...
///
/// Hashes are <see cref="LangHashDjb2.Hash"/>. Entries are sorted ascending by
/// hash; offsets dedup when two HAL ids point at the same display text.
public static class FeEnglishLanguageBin
{
    /// Build the LANGUAGE_English.bin bytes for a package. Includes per-map
    /// HAL → display strings (DistKey, LocationHalName, WorldHalName,
    /// LocationDescHalName, ID_DLC_*_SUMMARY_TITLE), package category /
    /// helper / section labels, OTS challenge title/description pairs, and
    /// Race challenge title/description pairs.
    public static byte[] Build(
        PackageInput input,
        IReadOnlyList<DlcSpec> specs,
        IReadOnlyList<OtsChallengeEntry> otsChallenges,
        IReadOnlyList<RaceChallengeEntry>? raceChallenges = null,
        IReadOnlyList<SkateChallengeEntry>? skateChallenges = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(otsChallenges);

        // Package-level identifiers ALWAYS derive from PackageName — see
        // DlcManifestVltBuilder for the full rationale. The lang pack
        // filename + embedded slug + category HALIDs all need to align with
        // the manifest VLT's package-level keys, so they share the same
        // derivation here.
        string packageSlug = DlcSpec.ToSlug(input.PackageName);
        string categoryDisplayHal = "ID_MAP_CATEGORY_" + packageSlug.ToUpperInvariant();
        string filterHelperHal = "ID_MAP_HELPER_" + packageSlug.ToUpperInvariant();
        string categoryDescription = input.CategoryDescription
            ?? input.PackageName + " freeskate maps.";

        var entries = new List<(string HalId, string Display)>();
        foreach (var s in specs)
        {
            // fe_locations Layout slot 1 references DistKey directly; the FE
            // resolves it through the same DJB2 table as HAL ids.
            entries.Add((s.DistKey, s.DisplayName));
            entries.Add((s.LocationHalName, s.DisplayName));
            entries.Add((s.WorldHalName, s.DisplayName));
            entries.Add((s.LocationDescHalName, s.DescriptionText));
            // progression_summary_group/<slug>_dlc .Title points here. Without
            // this row the FE summary / listing strip can show a blank title.
            entries.Add(("ID_DLC_" + s.Slug.ToUpperInvariant() + "_SUMMARY_TITLE", s.DisplayName));
        }

        // Section sub-categories — only emit when the package mixes sections.
        bool useSubsections = specs.Any(s => !string.IsNullOrWhiteSpace(s.SectionLabel));
        if (useSubsections)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in specs)
            {
                string key = string.IsNullOrWhiteSpace(s.SectionLabel) ? "default" : DlcSpec.ToSlug(s.SectionLabel);
                if (seen.ContainsKey(key)) continue;
                seen[key] = string.IsNullOrWhiteSpace(s.SectionLabel) ? "Maps" : s.SectionLabel.Trim();
            }
            foreach (var kv in seen.OrderBy(p => p.Key, StringComparer.Ordinal))
                entries.Add(($"ID_MAP_SECTION_{packageSlug.ToUpperInvariant()}_{kv.Key.ToUpperInvariant()}", kv.Value));
        }

        // Package-level entries.
        entries.Add((filterHelperHal, categoryDescription));
        // map_category.DisplayText is always CategoryDisplayHal. Skip when CLI
        // single --dist already mapped CategoryDisplayHal == WorldHalName above.
        if (!specs.Any(s => string.Equals(s.WorldHalName, categoryDisplayHal, StringComparison.Ordinal)))
            entries.Add((categoryDisplayHal, input.PackageName));

        // OTS challenge HAL strings.
        foreach (var c in otsChallenges)
        {
            entries.Add((c.TitleHalId, c.DisplayTitle));
            entries.Add((c.DescHalId, c.Description));
        }

        // Race challenge HAL strings. Same shape as OTS — each race's
        // per-instance `challenge_global_data/<key>` row's Title attribute
        // points at ID_CHALLENGE_<KEY>_TITLE; the family row points at
        // ID_MISSION_TEMPLATE_DEATH_RACE_TITLE which is supplied by stock.
        // We only need to register the per-instance HALIDs here.
        if (raceChallenges != null)
        {
            foreach (var c in raceChallenges)
            {
                entries.Add((c.TitleHalId, c.DisplayTitle));
                entries.Add((c.DescHalId, c.Description));
            }
        }

        // Skate (Game of S.K.A.T.E.) challenge HAL strings. Per-spot
        // challenge_global_data row's Title attribute (emitted by
        // SkateChallengeRowsBuilder) points at ID_CHALLENGE_<KEY>_TITLE;
        // base-game `s_k_a_t_e` family supplies ID_MISSION_TEMPLATE_SKATE_*
        // for the menu template fallback. Only per-instance HALIDs needed
        // here.
        if (skateChallenges != null)
        {
            foreach (var c in skateChallenges)
            {
                entries.Add((c.TitleHalId, c.DisplayTitle));
                // Per-spot Description empty in our SkateChallengeSpec by
                // design (matches base — instance rows don't set DESC). Emit
                // a single-space placeholder so FE lookup never returns NULL.
                entries.Add((c.DescHalId, string.IsNullOrEmpty(c.Description) ? " " : c.Description));
            }
        }

        // Encode the package slug used by the engine to identify this
        // language pack (rounded up to ≥16 bytes / 4-byte multiple).
        byte[] slugBytes = Encoding.UTF8.GetBytes(packageSlug);
        int slugStorageLen = Math.Max(16, (slugBytes.Length + 3) / 4 * 4);
        byte[] slugStorage = new byte[slugStorageLen];
        Buffer.BlockCopy(slugBytes, 0, slugStorage, 0, slugBytes.Length);

        // Layout offsets (within the inner payload).
        uint stringsStart = (uint)(12 + slugStorageLen);
        uint entryCount = (uint)entries.Count;
        uint stringsEnd = stringsStart + entryCount * 8;     // each entry = 8B (hash + rel offset)

        // String pool — dedup identical display strings so the same offset is reused.
        var stringRelOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        using var stringPool = new MemoryStream();
        using (var sw = new BinaryWriter(stringPool, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var (_, display) in entries)
            {
                if (stringRelOffsets.ContainsKey(display)) continue;
                stringRelOffsets[display] = (uint)stringPool.Length;
                sw.Write(Encoding.ASCII.GetBytes(display));
                sw.Write((byte)0);
            }
        }
        // 4-byte alignment of the string pool tail.
        while ((stringPool.Length & 3) != 0) stringPool.WriteByte(0);

        // Sort entries by hash (engine binary-searches the table).
        var indexEntries = entries
            .Select(e => (Hash: LangHashDjb2.Hash(e.HalId), StrOffset: stringRelOffsets[e.Display]))
            .OrderBy(t => t.Hash)
            .ToArray();

        // Inner payload.
        using var payload = new MemoryStream();
        using (var pw = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
        {
            pw.WriteLE(entryCount);
            pw.WriteLE(stringsStart);
            pw.WriteLE(stringsEnd);
            pw.Write(slugStorage);
            foreach (var (hash, off) in indexEntries)
            {
                pw.WriteLE(hash);
                pw.WriteLE(off);
            }
            pw.Write(stringPool.ToArray());
        }
        byte[] payloadBytes = payload.ToArray();

        // Outer wrapper.
        using var outer = new MemoryStream();
        using (var ow = new BinaryWriter(outer, Encoding.ASCII, leaveOpen: true))
        {
            ow.WriteLE(0x00039000u);
            ow.WriteLE((uint)payloadBytes.Length);
            ow.Write(payloadBytes);
        }
        return outer.ToArray();
    }

    /// Minimum input shape needed for OTS HAL emission — keeps the FE module
    /// independent of the OTS module.
    public sealed record OtsChallengeEntry(string TitleHalId, string DescHalId, string DisplayTitle, string Description);

    /// Minimum input shape needed for Race HAL emission. Identical to
    /// <see cref="OtsChallengeEntry"/> structurally — distinct record so
    /// front-end producers and downstream registries (analytics, FE feature
    /// flags, etc.) can disambiguate race-typed entries without grepping
    /// HALID prefixes.
    public sealed record RaceChallengeEntry(string TitleHalId, string DescHalId, string DisplayTitle, string Description);

    /// One Skate (Game of S.K.A.T.E.) challenge to register two HAL entries
    /// for (title + description). Same shape as <see cref="OtsChallengeEntry"/>.
    public sealed record SkateChallengeEntry(string TitleHalId, string DescHalId, string DisplayTitle, string Description);
}
