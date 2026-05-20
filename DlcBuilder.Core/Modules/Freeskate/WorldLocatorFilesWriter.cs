using System.Globalization;
using System.Linq;
using System.Text;
using ArenaBuilder.Core;
using DlcBuilder.Builders;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.LocatorPsg;
using DlcBuilder.Modules.LocXml;
using DlcBuilder.Modules.OtsPsg;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.Freeskate;

/// Writes the per-world locator artifacts that let
/// `LocationManager::IsValidLocation("freeskate_&lt;world&gt;_locator")` succeed at
/// runtime. Without these files the online-freeskate registration fails with
/// "Cannot create Online Freeskate in this Area".
///
/// Outputs (per map):
///   • `content/global_locators/DLC_BAM/&lt;WorldStream&gt;/&lt;prefix&gt;_[0xHASH].psg`
///     — main freeskate locator + 6 sub-spawn slots (engine needs exactly 6
///     to populate the lobby's per-player spawn array).
///   • Sibling `.loc` XML pair.
///   • Second locator psg/loc for `Z_&lt;Slug&gt;_Start` (the world Start spawn the
///     world/fe_locations row's FELayout points at).
///   • One psg/loc per OTS challenge (DW ships these for bigfile-packer
///     metadata parity even though they're not loaded directly).
///   • `content/world/stream/&lt;WorldStream&gt;/&lt;WorldStream&gt;_Sim.loc` — THE
///     gate. Engine reads this on world join; every locator name in here gets
///     registered into LocationManager. Without an OTS's anchor name in this
///     file, its `.Location` HAL resolves to nothing → challenge dropped.
///
/// `&lt;prefix&gt;` is the world stream name truncated to 12 chars to avoid the
/// bigfile-packer's path-segment dedup (filename prefix MUST differ from the
/// folder name or the engine's PSG path lookup fails).
public static class WorldLocatorFilesWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(
        DlcSpec map,
        string outputDirectory,
        IReadOnlyList<OtsChallengeSpec>? otsChallenges,
        IReadOnlyList<RaceChallengeSpec>? raceChallenges,
        IList<string> writtenFiles)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);

        string locatorDir = Path.Combine(
            outputDirectory, "content", "global_locators", "DLC_BAM", map.WorldStreamName);
        Directory.CreateDirectory(locatorDir);

        string worldLc = map.Slug.ToLowerInvariant();
        string locatorName = "freeskate_" + worldLc + "_locator";
        var mainTransform = Transform44.YawAt(map.SpawnX, map.SpawnY, map.SpawnZ, map.SpawnYaw);

        // 6 sub-spawns at small XZ offsets (one per online lobby slot).
        // Engine's sub_22D078 needs exactly 6 entries to populate the per-player
        // spawn array; 4 was the difference between "appears offline" and "appears
        // online".
        var offsets = new (float Dx, float Dz)[]
            { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f), (2f, 2f), (-2f, -2f) };
        var subSpecs = new List<LocationDescDataBuilder.SubLocSpec>();
        for (int i = 0; i < offsets.Length; i++)
        {
            string subName = locatorName + "::freeskate_" + worldLc + "_spawn_"
                + (i + 1).ToString(CultureInfo.InvariantCulture);
            float yawRad = map.SpawnYaw * (MathF.PI / 180f);
            float rx = offsets[i].Dx * MathF.Cos(yawRad) - offsets[i].Dz * MathF.Sin(yawRad);
            float rz = offsets[i].Dx * MathF.Sin(yawRad) + offsets[i].Dz * MathF.Cos(yawRad);
            var subT = Transform44.YawAt(
                mainTransform.Tx + rx, mainTransform.Ty, mainTransform.Tz + rz, map.SpawnYaw);
            subSpecs.Add(new LocationDescDataBuilder.SubLocSpec(subName, subT));
        }

        ulong locatorGuid = LocatorPsgBuilder.ComputeLocatorGuid(locatorName);
        var locSpec = new LocationDescDataBuilder.LocSpec(
            Name: locatorName,
            Description: "world",
            Transform: mainTransform,
            Guid: locatorGuid,
            SubLocations: subSpecs);
        byte[] payload = LocationDescDataBuilder.Build(locSpec);

        // Filename basename: `<prefix>_[0xHASH].psg` where prefix = world stream
        // name truncated to 12 chars. The bigfile packer DEDUPLICATES path
        // segments when folder name and filename prefix are identical; without
        // truncation our PSGs end up under `DLC_BAM\<World>_[hash].psg` instead
        // of `DLC_BAM/<World>/<World>_[hash].psg` and the engine's PSG path
        // lookup then fails. DW ships truncated 12-char prefixes for exactly
        // this reason (folder=DLC_DW_MegaCompund, prefix=DLC_DW_MegaC).
        string streamName = map.WorldStreamName;
        string filePrefix = streamName.Length > 12 ? streamName[..12] : streamName;

        // Main freeskate locator
        WriteLocatorPair(locatorDir, filePrefix, locatorName, locatorGuid, payload, mainTransform, writtenFiles);

        // Z_<Slug>_Start — second locator psg/loc for the world Start spawn.
        // The world/fe_locations row's FELayout spawn field points at this name;
        // the engine resolves it through LocationManager. Without it the online
        // freeskate menu has no spawn point and drops the world from the listing.
        string startName = "Z_" + map.Slug + "_Start";
        ulong startGuid = LocatorPsgBuilder.ComputeLocatorGuid(startName);
        var startSpec = new LocationDescDataBuilder.LocSpec(
            Name: startName,
            Description: "world",
            Transform: mainTransform,
            Guid: startGuid,
            SubLocations: new List<LocationDescDataBuilder.SubLocSpec>());
        byte[] startPayload = LocationDescDataBuilder.Build(startSpec);
        WriteLocatorPair(locatorDir, filePrefix, startName, startGuid, startPayload, mainTransform, writtenFiles);

        // Per-OTS global_locator PSG — anchor as TOP-LEVEL tLocationDesc with
        // 6 lobby-style spawn slots (`<anchor>::<key>_spawn_<n>`). Verified
        // against retail DW DLC_DW_MegaC_[0x5a77fc8428ce315e].psg, whose
        // string blob contains: `ots_dwmc_01_challengelocator_01` + 6
        // `ots_dwmc_01_spawn_1..6`. The chev/start/vis/wait names DO NOT
        // appear here — they live exclusively in the per-mission cSim_Global
        // PSG. Sharing them across both PSGs duplicates the registration
        // (RegArena silently rejects duplicates via cntlzw on
        // GetLocationInfo) and clutters LocationManager.
        //
        // The engine streams this PSG when the world arena loads, so the
        // anchor name is resolvable for the OTS in-world marker render path
        // before the player ever accepts the challenge.
        if (otsChallenges != null)
        {
            foreach (OtsChallengeSpec ots in otsChallenges)
            {
                ulong otsGuid = LocatorPsgBuilder.ComputeLocatorGuid(ots.AnchorName);
                var anchorSpawnSlots = BuildAnchorSpawnSlots(ots);
                var otsLocSpec = new LocationDescDataBuilder.LocSpec(
                    Name: ots.AnchorName,
                    Description: "challenge",
                    Transform: ots.AnchorTransform,
                    Guid: otsGuid,
                    SubLocations: anchorSpawnSlots);
                byte[] otsPayload = LocationDescDataBuilder.Build(otsLocSpec);

                string otsBase = filePrefix + "_[0x" + Lookup8Hash.HashStringToHex(ots.AnchorName).ToLowerInvariant() + "]";
                string otsPsg = Path.Combine(locatorDir, otsBase + ".psg");
                string otsLoc = Path.Combine(locatorDir, otsBase + ".loc");

                using (var fs = File.Create(otsPsg))
                    LocatorPsgBuilder.Write(ots.AnchorName, otsPayload, otsGuid, fs);
                writtenFiles.Add(otsPsg);

                // Sibling .loc XML — anchor only (DW's per-OTS .loc carries
                // just the anchor transform; sub-spawn slots are PSG-only).
                File.WriteAllText(otsLoc,
                    LocXmlBuilder.Build(ots.AnchorName, ots.AnchorTransform), Utf8NoBom);
                writtenFiles.Add(otsLoc);
            }
        }

        // Per-race global_locator PSG — same shape as OTS but anchored on
        // the race's startlocator. Engine streams this PSG when the world
        // arena loads, BEFORE the race's per-mission cSim_Global.psf is
        // streamed in. Without this, `<race_key>_startlocator` isn't in
        // LocationManager at world-join time → the FE minimap icon falls
        // back to (0,0), the signup trigger can't be placed because the
        // engine can't resolve the anchor position, and vault row
        // construction NULL-derefs when challenge_global_data.Location's
        // tLocationID name resolves to nothing.
        if (raceChallenges != null)
        {
            foreach (RaceChallengeSpec race in raceChallenges)
            {
                if (race.Map.DistKey != map.DistKey) continue;
                string raceAnchorName = $"{race.ChallengeKey}_startlocator";
                Transform44 raceAnchorTransform = Transform44.YawAt(
                    race.AnchorPosition.X, race.AnchorPosition.Y, race.AnchorPosition.Z,
                    race.AnchorYawDegrees);
                ulong raceGuid = LocatorPsgBuilder.ComputeLocatorGuid(raceAnchorName);
                var raceSpawnSlots = BuildRaceAnchorSpawnSlots(raceAnchorName, raceAnchorTransform);
                var raceLocSpec = new LocationDescDataBuilder.LocSpec(
                    Name: raceAnchorName,
                    Description: "challenge",
                    Transform: raceAnchorTransform,
                    Guid: raceGuid,
                    SubLocations: raceSpawnSlots);
                byte[] racePayload = LocationDescDataBuilder.Build(raceLocSpec);

                string raceBase = filePrefix + "_[0x"
                    + Lookup8Hash.HashStringToHex(raceAnchorName).ToLowerInvariant() + "]";
                string racePsg = Path.Combine(locatorDir, raceBase + ".psg");
                string raceLoc = Path.Combine(locatorDir, raceBase + ".loc");

                using (var fs = File.Create(racePsg))
                    LocatorPsgBuilder.Write(raceAnchorName, racePayload, raceGuid, fs);
                writtenFiles.Add(racePsg);

                File.WriteAllText(raceLoc,
                    LocXmlBuilder.Build(raceAnchorName, raceAnchorTransform), Utf8NoBom);
                writtenFiles.Add(raceLoc);
            }
        }

        // ── THE GATE: per-world `_Sim.loc` ──
        // EBOOT format string at 0x151C9D0: `data/content\world\stream\%s\%s_Sim.loc`.
        // Engine reads this on world join → registers each <Location>'s name
        // into LocationManager. sub_F27490's lookup in
        // Challenges_FilterFreeskateList then succeeds and the online-freeskate
        // menu click is accepted. Without OTS entries here, every OTS row's
        // `.Location` HAL resolves to nothing → challenge dropped from FE listing
        // and from in-world marker rendering.
        string worldStreamDir = Path.Combine(
            outputDirectory, "content", "world", "stream", map.WorldStreamName);
        Directory.CreateDirectory(worldStreamDir);
        string worldSimLoc = Path.Combine(worldStreamDir, map.WorldStreamName + "_Sim.loc");

        // World Sim.loc deliberately NOT written here — see
        // `WriteWorldSimLoc` below. The per-MapInput call site writes the
        // freeskate-locator + Z_<Slug>_Start PSGs (which are unique per
        // MapInput because of their slug suffixes), but multiple MapInputs
        // can share the SAME WorldStreamName when they're authored against
        // the same DIST folder. Writing the Sim.loc here would have each
        // MapInput overwrite the previous one, clobbering everything except
        // the last call's data — which is what produced the "world Sim.loc
        // missing OTS sub-locators" symptom (orchestrator parks OTS on
        // map[0]; map[1] writes second; final file has no OTS).
        // Orchestrator now aggregates across MapInputs sharing a
        // WorldStreamName and calls `WriteWorldSimLoc` once per group.
    }

    /// Aggregated entry from a MapInput: the freeskate locator + its
    /// `Z_<Slug>_Start` companion. Multiple of these can share a
    /// WorldStreamName when several MapInputs target the same DIST.
    public sealed record FreeskateEntry(
        string LocatorName,
        string ZStartName,
        Transform44 Transform);

    /// Write the world `_Sim.loc` for one DIST. Aggregates every freeskate
    /// locator that points at this DIST plus, for each OTS challenge, the
    /// **anchor only** (<c>{key}_challengelocator_01</c>) — matching Danny Way retail
    /// <c>DLC_DW_MegaCompund_Sim.loc</c>: chevron / vis / start / wait names live only
    /// under <c>content/missions/&lt;key&gt;/&lt;key&gt;_Sim.loc</c> and <c>cSim_Global</c>, not here.
    /// The first freeskate entry's locator name is the XML root; everything else is a sibling.
    ///
    /// Engine reads this file at world-join time (via the EBOOT format
    /// string at 0x151C9D0: `data/content\world\stream\%s\%s_Sim.loc`) and
    /// registers each <Location> name into LocationManager.
    public static void WriteWorldSimLoc(
        string worldStreamName,
        IReadOnlyList<FreeskateEntry> freeskateEntries,
        IReadOnlyList<OtsChallengeSpec> otsChallenges,
        IReadOnlyList<RaceChallengeSpec> raceChallenges,
        string outputDirectory,
        IList<string> writtenFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldStreamName);
        ArgumentNullException.ThrowIfNull(freeskateEntries);
        ArgumentNullException.ThrowIfNull(otsChallenges);
        ArgumentNullException.ThrowIfNull(raceChallenges);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);

        if (freeskateEntries.Count == 0) return;

        string worldStreamDir = Path.Combine(
            outputDirectory, "content", "world", "stream", worldStreamName);
        Directory.CreateDirectory(worldStreamDir);
        string worldSimLoc = Path.Combine(worldStreamDir, worldStreamName + "_Sim.loc");

        // Root = first freeskate locator. The remaining freeskate locators
        // (if multiple MapInputs share this DIST) plus every Z_<Slug>_Start
        // and every OTS anchor (challengelocator_01 only — retail DW shape).
        var root = freeskateEntries[0];
        var siblings = new List<(string Name, Transform44 Transform)>
        {
            (Name: root.ZStartName, Transform: root.Transform),
        };

        for (int i = 1; i < freeskateEntries.Count; i++)
        {
            var fe = freeskateEntries[i];
            siblings.Add((Name: fe.LocatorName, Transform: fe.Transform));
            siblings.Add((Name: fe.ZStartName, Transform: fe.Transform));
        }

        foreach (OtsChallengeSpec ots in otsChallenges)
            siblings.Add((Name: ots.AnchorName, Transform: ots.AnchorTransform));

        // Race locators (_startlocator, _vi_NN, _endcamera) are NOT placed in
        // _Sim.loc. Stock DLC (DannyWay) has zero race entries in _Sim.loc —
        // the startlocator is registered via the world-stream global_locator
        // PSG, and VI + endcamera locators come from the per-mission
        // cSim_Global PSG which loads at challenge start.

        string worldSimLocXml = LocXmlBuilder.BuildWithSiblings(root.LocatorName, root.Transform, siblings);
        File.WriteAllText(worldSimLoc, worldSimLocXml, Utf8NoBom);
        writtenFiles.Add(worldSimLoc);
    }

    /// 6 lobby-style spawn slots hung off a race start locator, same
    /// `<anchor>::spawnpoint_<NN>` naming as the per-mission PSG produces
    /// (see `RaceMissionFolderWriter.BuildRaceStartSpawnSlots`). Yawed-XZ
    /// 2-row × 3-column starting grid behind the parent.
    private static IReadOnlyList<LocationDescDataBuilder.SubLocSpec> BuildRaceAnchorSpawnSlots(
        string anchorName, Transform44 anchorTransform)
    {
        (float Lx, float Lz)[] offsets =
        {
            (0f, 0f),       // 01 pole
            (1.5f, 0f),     // 02 right
            (-1.5f, 0f),    // 03 left
            (0f, -2f),      // 04 row 2 centre
            (1.5f, -2f),    // 05 row 2 right
            (-1.5f, -2f),   // 06 row 2 left
        };

        float yaw = MathF.Atan2(anchorTransform.Rows[8], anchorTransform.Rows[10]);
        float yawDeg = yaw * (180f / MathF.PI);

        var slots = new List<LocationDescDataBuilder.SubLocSpec>(offsets.Length);
        for (int i = 0; i < offsets.Length; i++)
        {
            var (lx, lz) = offsets[i];
            float rx = lx * MathF.Cos(yaw) - lz * MathF.Sin(yaw);
            float rz = lx * MathF.Sin(yaw) + lz * MathF.Cos(yaw);
            var t = Transform44.YawAt(
                anchorTransform.Tx + rx,
                anchorTransform.Ty,
                anchorTransform.Tz + rz,
                yawDeg);
            slots.Add(new LocationDescDataBuilder.SubLocSpec(
                Name: $"{anchorName}::spawnpoint_{i + 1:D2}",
                Transform: t));
        }
        return slots;
    }

    /// 6 lobby-style spawn slots hung off an OTS anchor — DW pattern.
    /// Names follow `<anchor>::<challengeKey>_spawn_<n>` (verified against
    /// DLC_DW_MegaC_[0x5a77fc8428ce315e].psg's string blob). Same yawed-XZ
    /// offsets we use for freeskate locator spawns.
    private static IReadOnlyList<LocationDescDataBuilder.SubLocSpec> BuildAnchorSpawnSlots(
        OtsChallengeSpec ots)
    {
        var offsets = new (float Dx, float Dz)[]
            { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f), (2f, 2f), (-2f, -2f) };

        // Extract anchor yaw from the transform's row-2 (sin/cos in Rows[8] / Rows[10]).
        float yaw = MathF.Atan2(ots.AnchorTransform.Rows[8], ots.AnchorTransform.Rows[10]);
        float yawDeg = yaw * (180f / MathF.PI);

        var slots = new List<LocationDescDataBuilder.SubLocSpec>(offsets.Length);
        for (int i = 0; i < offsets.Length; i++)
        {
            var (dx, dz) = offsets[i];
            float rx = dx * MathF.Cos(yaw) - dz * MathF.Sin(yaw);
            float rz = dx * MathF.Sin(yaw) + dz * MathF.Cos(yaw);
            var t = Transform44.YawAt(
                ots.AnchorTransform.Tx + rx,
                ots.AnchorTransform.Ty,
                ots.AnchorTransform.Tz + rz,
                yawDeg);
            slots.Add(new LocationDescDataBuilder.SubLocSpec(
                Name: $"{ots.AnchorName}::{ots.ChallengeKey}_spawn_{i + 1}",
                Transform: t));
        }
        return slots;
    }

    private static void WriteLocatorPair(
        string locatorDir,
        string filePrefix,
        string locName,
        ulong locGuid,
        byte[] payload,
        Transform44 transform,
        IList<string> writtenFiles)
    {
        string baseName = filePrefix + "_[0x" + Lookup8Hash.HashStringToHex(locName).ToLowerInvariant() + "]";
        string psgPath = Path.Combine(locatorDir, baseName + ".psg");
        string locPath = Path.Combine(locatorDir, baseName + ".loc");

        using (var fs = File.Create(psgPath))
            LocatorPsgBuilder.Write(locName, payload, locGuid, fs);
        writtenFiles.Add(psgPath);

        File.WriteAllText(locPath, LocXmlBuilder.Build(locName, transform), Utf8NoBom);
        writtenFiles.Add(locPath);
    }
}
