using ArenaBuilder.Core;
using DlcBuilder.Builders;
using DlcBuilder.Modules.LocatorPsg;
using DlcBuilder.Modules.MissionTemplates;
using DlcBuilder.Modules.OtsPsg;

namespace DlcBuilder.Modules.Skate;

/// Writes the per-Skate-spot mission folder under
/// `&lt;outputDirectory&gt;/content/missions/skate_&lt;key&gt;/`:
///
///   • 4 Pres manifest stubs + 4 Tex manifest stubs (8 stubs total)
///   • `cSim_Global/&lt;hash&gt;.psg` — registers the 4 trigger volumes
///     (ChallengeBoundary, SpotVolumes×N, TurnBasedStartVolume) and the
///     4 locators (start, wait, vi_01, vi_02) the per-instance VLT
///     references.
///
/// Mirrors <see cref="Race.RaceMissionFolderWriter"/> for the race side
/// (skips the 4 Sim stubs — cSim_Global.psf supersedes them). Reuses
/// OtsPsgBytesBuilder for the PSG body since the underlying RW4 layout
/// is independent of challenge type.
public static class SkateMissionFolderWriter
{
    public static void Write(SkateChallengeSpec spec, string outputDirectory, IList<string> writtenFiles,
        PlatformProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);
        profile ??= PlatformProfile.Ps3;

        string missionDir = Path.Combine(outputDirectory, "content", "missions", spec.ChallengeKey);
        Directory.CreateDirectory(missionDir);

        // ── 1) Pres + Tex stubs ────────────────────────────────────────────
        string[] suffixes =
        {
            "_Pres.pmm", "_Pres.psm", "_Pres.pss", "_Pres.pst",
            "_Tex.pmm",  "_Tex.psm",  "_Tex.pss",  "_Tex.pst",
        };
        foreach (string suffix in suffixes)
        {
            string dst = Path.Combine(missionDir, spec.ChallengeKey + suffix);
            if (MissionTemplateProvider.TryGetTemplateBytes(suffix, out byte[] templateBytes))
            {
                File.WriteAllBytes(dst, templateBytes);
                writtenFiles.Add(dst);
            }
        }

        // ── 2) cSim_Global/<hash>.psg ──────────────────────────────────────
        // Registers: ChallengeBoundary + SpotVolumes[1..2] + TurnBasedStartVolume
        //          + start locator + wait locator + visual indicators[1..2]
        string cSimDir = Path.Combine(missionDir, "cSim_Global");
        Directory.CreateDirectory(cSimDir);
        ulong psgHash = Lookup8Hash.HashString($"{spec.ChallengeKey}_cSim_Global");
        string psgPath = Path.Combine(cSimDir, $"{psgHash:X16}{profile.PsgExt}");

        var triggers = new List<OtsTriggerVolume>();
        triggers.Add(BuildTriggerVolume(spec.Map.WorldStreamName, spec.ChallengeBoundary));
        foreach (var sv in spec.SpotVolumes)
            triggers.Add(BuildTriggerVolume(spec.Map.WorldStreamName, sv));
        triggers.Add(BuildTriggerVolume(spec.Map.WorldStreamName, spec.TurnBasedStartVolume));

        var locators = new List<LocationDescDataBuilder.LocSpec>();
        locators.Add(new LocationDescDataBuilder.LocSpec(
            Name: spec.StartLocatorName,
            Description: spec.DisplayTitle,
            Transform: Transform44.YawAt(
                spec.StartLocatorPosition.X, spec.StartLocatorPosition.Y,
                spec.StartLocatorPosition.Z, spec.StartLocatorYawDegrees),
            Guid: Lookup8Hash.HashString(spec.StartLocatorName),
            SubLocations: Array.Empty<LocationDescDataBuilder.SubLocSpec>()));

        locators.Add(new LocationDescDataBuilder.LocSpec(
            Name: spec.WaitLocatorName,
            Description: $"{spec.DisplayTitle} wait",
            Transform: Transform44.YawAt(
                spec.WaitLocatorPosition.X, spec.WaitLocatorPosition.Y,
                spec.WaitLocatorPosition.Z, spec.WaitLocatorYawDegrees),
            Guid: Lookup8Hash.HashString(spec.WaitLocatorName),
            SubLocations: Array.Empty<LocationDescDataBuilder.SubLocSpec>()));

        for (int i = 0; i < spec.VisualIndicators.Count; i++)
        {
            string viName = spec.VisualIndicatorName(i + 1);
            var (pos, yaw) = spec.VisualIndicators[i];
            locators.Add(new LocationDescDataBuilder.LocSpec(
                Name: viName,
                Description: $"{spec.DisplayTitle} VI {i + 1}",
                Transform: Transform44.YawAt(pos.X, pos.Y, pos.Z, yaw),
                Guid: Lookup8Hash.HashString(viName),
                SubLocations: Array.Empty<LocationDescDataBuilder.SubLocSpec>()));
        }

        using (var fs = File.Create(psgPath))
            OtsPsgBytesBuilder.Build(spec.ChallengeKey, triggers, locators, fs, profile.Arena);
        writtenFiles.Add(psgPath);
    }

    private static OtsTriggerVolume BuildTriggerVolume(string worldStreamName, SkateTriggerVolume volume)
    {
        var c = volume.Center;
        float hx = MathF.Max(volume.HalfExtents.X, 0.01f);
        float hy = MathF.Max(volume.HalfExtents.Y, 0.01f);
        float hz = MathF.Max(volume.HalfExtents.Z, 0.01f);

        float yawRad = volume.YawDegrees * MathF.PI / 180f;
        float cosY = MathF.Cos(yawRad);
        float sinY = MathF.Sin(yawRad);
        var polygon = new (float X, float Z)[]
        {
            (c.X - hx*cosY - hz*sinY, c.Z + hx*sinY - hz*cosY),
            (c.X + hx*cosY - hz*sinY, c.Z - hx*sinY - hz*cosY),
            (c.X + hx*cosY + hz*sinY, c.Z - hx*sinY + hz*cosY),
            (c.X - hx*cosY + hz*sinY, c.Z + hx*sinY + hz*cosY),
        };

        string canonical = $"{worldStreamName}|{volume.Name}|0x{volume.Guid:x16}";
        return new OtsTriggerVolume
        {
            Polygon = polygon,
            MinY = c.Y - hy,
            MaxY = c.Y + hy,
            Name = canonical,
            Guid = Lookup8Hash.HashString(volume.Name),
            GuidLocal = volume.Guid,
            TriggerType = 0,
            YawRadians = yawRad,
        };
    }
}
