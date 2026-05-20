using ArenaBuilder.Core.Psg;

namespace ArenaBuilder.Cli.Commands;

internal static class PsgValidateClusteredMeshCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 1) return CliErrors.Fail("Usage: psg-validate-cmesh <path>");

        var path = args[0];
        var bytes = File.ReadAllBytes(path);
        var psg = PsgBinary.Parse(bytes);
        var cmesh = psg.Objects.FirstOrDefault(o => o.TypeId == 0x00080006);
        if (cmesh == null) return CliErrors.Fail("No ClusteredMesh (0x00080006) found in dictionary.");

        var obj = bytes.AsSpan(cmesh.Ptr, cmesh.Size);
        var report = ClusteredMeshValidator.Validate(obj);

        Console.WriteLine($"File: {path}");
        Console.WriteLine($"ClusteredMesh: size={cmesh.Size} numClusters={report.NumClusters} totalTris={report.TotalTris}");
        Console.WriteLine($"Clusters with invalid unit vertex indices: {report.InvalidClusterCount}");
        Console.WriteLine($"Total invalid units: {report.InvalidUnitCount}");
        if (report.InvalidExamples.Count > 0)
        {
            Console.WriteLine("Examples:");
            foreach (var ex in report.InvalidExamples)
            {
                Console.WriteLine(
                    $"  cluster={ex.ClusterIndex} unit={ex.UnitIndex} numVertices={ex.NumVertices} comp={ex.CompressionMode} unitDataStart={ex.UnitDataStart} normalStart={ex.NormalStart} flags=0x{ex.Flags:X2} v=({ex.V0},{ex.V1},{ex.V2}) unitStreamOff=0x{ex.UnitStreamOffset:X} unitOff=0x{ex.UnitOffsetInStream:X} bytes={ex.BytesHex}");
            }
        }

        return report.InvalidUnitCount == 0 ? 0 : 3;
    }
}
