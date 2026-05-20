using ArenaBuilder.Core.Psg;

namespace ArenaBuilder.Cli.Commands;

internal static class PsgDiffCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 2) return CliErrors.Fail("Usage: psg-diff <pathA> <pathB>");

        var pathA = args[0];
        var pathB = args[1];
        var aBytes = File.ReadAllBytes(pathA);
        var bBytes = File.ReadAllBytes(pathB);
        var a = PsgBinary.Parse(aBytes);
        var b = PsgBinary.Parse(bBytes);

        Console.WriteLine($"A: {pathA}");
        Console.WriteLine($"B: {pathB}");
        Console.WriteLine($"LenA={aBytes.Length} LenB={bBytes.Length} Delta={aBytes.Length - bBytes.Length}");
        Console.WriteLine($"ArenaIdA=0x{a.ArenaId:X8} ArenaIdB=0x{b.ArenaId:X8}");
        Console.WriteLine($"DictStartA=0x{a.DictStart:X8} DictStartB=0x{b.DictStart:X8}");
        Console.WriteLine();

        int count = Math.Min(a.Objects.Count, b.Objects.Count);
        Console.WriteLine("Idx  TypeId       SizeA    SizeB    Same?   Sha256(A obj) (first 16)   Sha256(B obj) (first 16)   FirstMismatch");

        for (int i = 0; i < count; i++)
        {
            var oa = a.Objects[i];
            var ob = b.Objects[i];
            var aObj = aBytes.AsSpan(oa.Ptr, oa.Size);
            var bObj = bBytes.AsSpan(ob.Ptr, ob.Size);

            bool sameType = oa.TypeId == ob.TypeId;
            bool sameSize = oa.Size == ob.Size;
            bool sameBytes = sameType && sameSize && aObj.SequenceEqual(bObj);

            string mismatch = "-";
            if (!sameBytes)
            {
                int m = Math.Min(aObj.Length, bObj.Length);
                int first = -1;
                for (int j = 0; j < m; j++)
                {
                    if (aObj[j] != bObj[j]) { first = j; break; }
                }
                mismatch = first >= 0
                    ? $"+0x{first:X} ({aObj[first]:X2}!={bObj[first]:X2})"
                    : $"(prefix={m} sizes {aObj.Length}/{bObj.Length})";
            }

            Console.WriteLine(
                $"{i,3}  0x{oa.TypeId:X8}  {oa.Size,7}  {ob.Size,7}  {(sameBytes ? "YES" : "NO "),5}  {PsgBinary.Sha256Hex16(aObj)}  {PsgBinary.Sha256Hex16(bObj)}  {mismatch}");

            if (!sameType)
                Console.WriteLine($"     WARNING: typeId mismatch at idx {i}: 0x{oa.TypeId:X8} vs 0x{ob.TypeId:X8}");

            // Extra decoding for the ClusteredMesh object (helps diagnose "stringy" meshes quickly).
            if (oa.TypeId == 0x00080006 && ob.TypeId == 0x00080006)
            {
                var ha = PsgStructureDecoder.DecodeClusteredMeshHeader(aObj);
                var hb = PsgStructureDecoder.DecodeClusteredMeshHeader(bObj);
                Console.WriteLine($"     ClusteredMesh(A): totalTris={ha.TotalTris} numClusters={ha.NumClusters} kdOff=0x{ha.KdOff:X} clPtrOff=0x{ha.ClusterPtrOff:X} clusterParams=0x{ha.ClusterParams:X16} totalSize={ha.TotalSize}");
                Console.WriteLine($"     ClusteredMesh(B): totalTris={hb.TotalTris} numClusters={hb.NumClusters} kdOff=0x{hb.KdOff:X} clPtrOff=0x{hb.ClusterPtrOff:X} clusterParams=0x{hb.ClusterParams:X16} totalSize={hb.TotalSize}");

                var kda = PsgStructureDecoder.DecodeKdTreeHeader(aObj);
                var kdb = PsgStructureDecoder.DecodeKdTreeHeader(bObj);
                Console.WriteLine($"     KDTree(A): numEntries={kda.NumEntries} numBranches={kda.NumBranches} branchOff=0x{kda.BranchOff:X}");
                Console.WriteLine($"     KDTree(B): numEntries={kdb.NumEntries} numBranches={kdb.NumBranches} branchOff=0x{kdb.BranchOff:X}");
            }
        }

        return 0;
    }
}

