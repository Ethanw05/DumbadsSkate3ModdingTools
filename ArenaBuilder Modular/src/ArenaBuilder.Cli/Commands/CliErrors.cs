namespace ArenaBuilder.Cli.Commands;

internal static class CliErrors
{
    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}

