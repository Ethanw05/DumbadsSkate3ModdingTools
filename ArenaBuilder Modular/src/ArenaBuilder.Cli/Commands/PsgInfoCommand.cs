using ArenaBuilder.Core.Psg;

namespace ArenaBuilder.Cli.Commands;

internal static class PsgInfoCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 1) return CliErrors.Fail("Usage: psg-info <path>");

        var path = args[0];
        var bytes = File.ReadAllBytes(path);
        var psg = PsgBinary.Parse(bytes);

        Console.WriteLine($"File: {path}");
        Console.WriteLine($"Length: {bytes.Length} bytes");
        Console.WriteLine($"ArenaId: 0x{psg.ArenaId:X8}");
        Console.WriteLine($"DictStart: 0x{psg.DictStart:X8}");
        Console.WriteLine($"SectionsStart: 0x{psg.SectionsStart:X8}");
        Console.WriteLine($"FileSizeField: 0x{psg.FileSizeField:X8}");
        Console.WriteLine();

        Console.WriteLine("Idx  TypeId       Ptr        Size");
        for (int i = 0; i < psg.Objects.Count; i++)
        {
            var o = psg.Objects[i];
            Console.WriteLine($"{i,3}  0x{o.TypeId:X8}  0x{o.Ptr:X8}  {o.Size,8}");
        }

        return 0;
    }
}

