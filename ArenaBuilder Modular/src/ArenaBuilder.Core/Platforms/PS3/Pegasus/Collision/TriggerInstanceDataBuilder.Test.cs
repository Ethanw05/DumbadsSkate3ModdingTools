// Standalone round-trip test: build a TriggerInstanceData payload that exactly
// matches DW's ots_dwmc_01 cSim_Global.psf trigger data (offset 0x250..0x8DB).
//
// Run via: dotnet run --project this assembly's host (or copy into an existing
// CLI command). This file is excluded from the normal build by being a *.Test.cs
// without a Main entry point — it's meant as documentation + a reference call
// site that demonstrates the spec format.

#if TRIGGER_INSTANCE_BUILDER_ROUND_TRIP_TEST
using System.IO;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Collision;

internal static class TriggerInstanceDataRoundTripTest
{
    public static void Run(string referenceCSimGlobalPsg, string outputDumpPath)
    {
        // Reference values extracted from DW ots_dwmc_01 cSim_Global.psf via
        // psg_structure_dumper.py — the embedded PSG starts at offset 0x237 of
        // the wrapping PSF; the tTriggerInstanceData object is at offset 0x250
        // of the embedded PSG (0x19 bytes past the PSG arena header).
        var instances = new[]
        {
            // 0: scoringboundary — challenge tier, dict index 4 → first CollisionModelData.
            new TriggerInstanceDataBuilder.InstanceSpec
            {
                BBoxMin = (131.224f, -26.306641f, -50.391815f),
                BBoxMax = (141.792f, -13.232605f, -8.887604f),
                Guid     = 0xE3DF4C42E756EB69ul,
                GuidLocal = 0x2C701706003D0BB5ul,
                CollisionModelDictIndex = 4,
                Name = "ots_dwmc_01_scoringboundary_0x2c70170500110003:0x2c70170600113aea:" +
                       "0x2c701706003d0772:0x2c701706003d0872:0x2c701706003d0873:" +
                       "0x2c701706003d0baa:0x2c701706003d0bab:0x2c701706003d0bb5:" +
                       "0x2c701703003d0272:0x2c701707003d0271::" +
                       "[0x0000008003e38704][0x2c70170500110003][0x2c70170600113aea]" +
                       "[0x0027e4c211e0f35f]_HighLOD",
            },
            // 1: challengeboundary — dict index 8.
            new TriggerInstanceDataBuilder.InstanceSpec
            {
                BBoxMin = (119.158f, -34.20892f, -98.71237f),
                BBoxMax = (213.21899f, 5.7232294f, 17.696f),
                Guid     = 0xFDB27A5C45ECD0D6ul,
                GuidLocal = 0x2C701706003D0BACul,
                CollisionModelDictIndex = 8,
                Name = "ots_dwmc_01_challengeboundary_0x2c70170500110003:0x2c70170600113aea:" +
                       "0x2c701706003d0772:0x2c701706003d0872:0x2c701706003d0873:" +
                       "0x2c701706003d0baa:0x2c701706003d0bab:0x2c701706003d0bac:" +
                       "0x2c701703003d0271:0x2c701707003d0270::" +
                       "[0x0000008003e38704][0x2c70170500110003][0x2c70170600113aea]" +
                       "[0x0027e4c211e0f35f]_HighLOD",
            },
            // 2: spotvolume_1 — dict index 12 (= 0x0C).
            new TriggerInstanceDataBuilder.InstanceSpec
            {
                BBoxMin = (132.543f, -23.099f, -49.196f),
                BBoxMax = (138.373f, -13.939f, -10.084f),
                Guid     = 0xA44D6C4EF657E93Aul,
                GuidLocal = 0x2C701706003D0BB6ul,
                CollisionModelDictIndex = 12,
                Name = "ots_dwmc_01_spotvolume_1_0x2c70170500110003:0x2c70170600113aea:" +
                       "0x2c701706003d0772:0x2c701706003d0872:0x2c701706003d0873:" +
                       "0x2c701706003d0baa:0x2c701706003d0bab:0x2c701706003d0bb6:" +
                       "0x2c701703003d0273:0x2c701707003d0272::" +
                       "[0x0000008003e38704][0x2c70170500110003][0x2c70170600113aea]" +
                       "[0x0027e4c211e0f35f]_HighLOD",
            },
        };

        byte[] built = TriggerInstanceDataBuilder.Build(instances);

        // Read the reference object from the source PSG.
        byte[] psg = File.ReadAllBytes(referenceCSimGlobalPsg);
        // Embedded PSG starts at 0x237 of the .psf; tTriggerInstanceData at 0x250
        // of the embedded PSG = 0x250 - 0x237 + 0x237 = 0x250 in the embedded PSG.
        // (The user already extracted it as <hash>.psg, so offsets are relative
        // to the .psg start — the tTriggerInstanceData object is at 0x250.)
        const int RefOffset = 0x250;
        const int RefSize = 1675;
        if (psg.Length < RefOffset + RefSize)
            throw new InvalidOperationException("Reference PSG too small.");

        byte[] reference = new byte[RefSize];
        Array.Copy(psg, RefOffset, reference, 0, RefSize);

        // Diff first mismatching byte.
        int min = Math.Min(built.Length, reference.Length);
        int firstDiff = -1;
        for (int i = 0; i < min; i++)
        {
            if (built[i] != reference[i]) { firstDiff = i; break; }
        }
        if (built.Length != reference.Length)
        {
            File.WriteAllText(outputDumpPath,
                $"SIZE MISMATCH: built={built.Length}, reference={reference.Length}, firstDiff=0x{firstDiff:X}\n");
        }
        else if (firstDiff < 0)
        {
            File.WriteAllText(outputDumpPath, "MATCH: all 1675 bytes byte-identical to retail DW ots_dwmc_01.\n");
        }
        else
        {
            File.WriteAllText(outputDumpPath,
                $"FIRST DIFF at 0x{firstDiff:X4}: built=0x{built[firstDiff]:X2}, ref=0x{reference[firstDiff]:X2}\n");
        }
    }
}
#endif
