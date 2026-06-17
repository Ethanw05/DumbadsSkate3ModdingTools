using System.Text;
using ArenaBuilder.Core;
using DlcBuilder.Modules.LocatorPsg;
using DlcBuilder.Modules.LocXml;
using DlcBuilder.Modules.MissionTemplates;

namespace DlcBuilder.Modules.OtsPsg;

/// Writes the per-OTS mission folder under
/// `&lt;outputDirectory&gt;/content/missions/&lt;challengeKey&gt;/`:
///
///   • 12 manifest stubs (Pres/Sim/Tex × pmm/psm/pss/pst) copied from
///     `lib/mission_template/` if present (skipped silently when missing).
///   • `&lt;challengeKey&gt;_Sim.loc` — XML locator transforms (sub-locators only; anchor is world `_Sim.loc`).
///   • Loose root `.loc` duplicate matching DW's
///     `[hashes]_Proc_Proc_Container_0_Sim_&lt;NUM&gt;.loc` filename. Built for shape
///     parity with retail (the bigfile packer pipeline references it; not
///     loaded directly at runtime).
///   • `cSim_Global/&lt;hash&gt;.psg` — the OTS PSG body, named so the Stream File
///     Tool packs it into `cSim_Global.psf` when invoked with `--type=sim`.
public static class OtsMissionFolderWriter
{
    private const ulong ProcContainerTypeId = 0x0000008003e38704UL;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(OtsChallengeSpec spec, string outputDirectory, IList<string> writtenFiles,
        PlatformProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);
        profile ??= PlatformProfile.Ps3;

        string missionDir = Path.Combine(outputDirectory, "content", "missions", spec.ChallengeKey);
        Directory.CreateDirectory(missionDir);

        // 1) 12 manifest stubs from lib/mission_template/ (if available).
        string[] suffixes =
        {
            "_Pres.pmm", "_Pres.psm", "_Pres.pss", "_Pres.pst",
            "_Sim.pmm",  "_Sim.psm",  "_Sim.pss",  "_Sim.pst",
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

        // 2) Per-mission Sim.loc — sub-locators ONLY, no anchor.
        // Verified against retail DW (ots_dwmc_01_Sim.loc): file contains
        // optional chev_* siblings + startlocator, vis_1, waitlocator.
        // Anchor name (`<key>_challengelocator_01`) is NOT in this file —
        // it's registered through the world Sim.loc instead. The engine
        // loads BOTH files: world Sim.loc populates LocationManager with
        // the anchor at world-join time; per-mission Sim.loc registers
        // the sub-locators when the OTS challenge starts.
        // Mission `<key>_Sim.loc` — retail uses self-closing `<Category />`.
        string locXml;
        if (spec.SubLocators.Count > 0)
        {
            var first = spec.SubLocators[0];
            var rest = spec.SubLocators.Skip(1)
                .Select(sl => (Name: sl.Name, Transform: sl.Transform));
            locXml = LocXmlBuilder.BuildWithSiblings(first.Name, first.Transform, rest);
        }
        else
        {
            // Defensive fallback when an OTS spec carries no sub-locators
            // (unexpected — OtsLayout normally emits at least start/vis/wait).
            locXml = LocXmlBuilder.Build(spec.AnchorName, spec.AnchorTransform);
        }
        string simLocPath = Path.Combine(missionDir, spec.ChallengeKey + "_Sim.loc");
        File.WriteAllText(simLocPath, locXml, Utf8NoBom);
        writtenFiles.Add(simLocPath);

        // 2b) Loose root .loc duplicate matching DW's
        // `[hashes]_Proc_Proc_Container_0_Sim_<NUM>.loc`. Per RPCS3 file
        // logs these aren't directly loaded at runtime — they're for the
        // bigfile packer pipeline — but we ship them for shape parity with DW.
        // Retail loose-root files use the PAIRED `<Category></Category>`
        // form (verified by raw-byte diff against shipping DW), unlike the
        // mission `_Sim.loc` above which uses the self-closing form.
        string looseLocXml;
        if (spec.SubLocators.Count > 0)
        {
            var first = spec.SubLocators[0];
            var rest = spec.SubLocators.Skip(1)
                .Select(sl => (Name: sl.Name, Transform: sl.Transform));
            looseLocXml = LocXmlBuilder.BuildWithSiblings(first.Name, first.Transform, rest, pairedCategory: true);
        }
        else
        {
            looseLocXml = LocXmlBuilder.Build(spec.AnchorName, spec.AnchorTransform, pairedCategory: true);
        }
        ulong hash1 = (0x2c701705UL << 32) | (Lookup8Hash.HashString(spec.Map.WorldStreamName + "_pres") & 0xFFFFFFFFUL);
        ulong hash2 = (0x2c701706UL << 32) | (Lookup8Hash.HashString(spec.Map.WorldStreamName + "_sim") & 0xFFFFFFFFUL);
        ulong hash3 = Lookup8Hash.HashString(spec.ChallengeKey + "_proc_container");
        uint simId  = (uint)(Lookup8Hash.HashString(spec.ChallengeKey + "_sim_id") & 0x7FFFFFFFUL);
        string looseLocName =
            $"[0x{ProcContainerTypeId:x16}]"
            + $"[0x{hash1:x16}]"
            + $"[0x{hash2:x16}]"
            + $"[0x{hash3:x16}]"
            + $"_Proc_Proc_Container_0_Sim_{simId}.loc";
        string looseLocPath = Path.Combine(outputDirectory, "content", "missions", looseLocName);
        Directory.CreateDirectory(Path.GetDirectoryName(looseLocPath)!);
        File.WriteAllText(looseLocPath, looseLocXml, Utf8NoBom);
        writtenFiles.Add(looseLocPath);

        // 3) cSim_Global PSG — into cSim_Global/<hash>.psg subfolder. Stream
        // File Tool picks it up when invoked with --type=sim and packs it
        // into cSim_Global.psf.
        string cSimDir = Path.Combine(missionDir, "cSim_Global");
        Directory.CreateDirectory(cSimDir);
        ulong psgHash = Lookup8Hash.HashString($"{spec.ChallengeKey}_cSim_Global");
        string psgPath = Path.Combine(cSimDir, $"{psgHash:X16}{profile.PsgExt}");

        // Each named OTS sub-locator (optional chev_*, vis_*, startlocator,
        // waitlocator) becomes an INDEPENDENT top-level tLocationDesc.
        // `cLocationManager::RegArena` only registers top-level entries, so
        // this is the only shape that lets the engine resolve names like
        // `<key>_startlocator` (referenced by challenge_local_data.StartLocation)
        // through `LocationManager::GetSpawnLocation`. Verified against retail
        // DW ots_dwmc_01 cSim_Global PSG (numLocs=6 + 12 sub-spawn slots).
        // The anchor name lives ONLY in the world Sim.loc + the DLC_BAM
        // global_locator PSG — it's NOT shipped here, matching DW (the DW
        // PSG's string blob contains chev/vis/start/wait names, no
        // `<key>_challengelocator_01`).
        var locators = OtsLocatorPlanner.PlanMissionPsgLocators(spec);

        using (var fs = File.Create(psgPath))
            OtsPsgBytesBuilder.Build(spec.ChallengeKey, spec.Triggers, locators, fs, profile.Arena);
        writtenFiles.Add(psgPath);
    }
}
