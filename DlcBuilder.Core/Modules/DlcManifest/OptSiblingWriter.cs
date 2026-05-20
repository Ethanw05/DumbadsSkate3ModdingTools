namespace DlcBuilder.Modules.DlcManifest;

/// Writes a 16-byte `EndC` stub `.opt` sibling next to every `.vlt` file in
/// `data/db/`. The vault-package validator checks for these — without them
/// the engine's vault loader rejects the package on boot.
///
/// Stub content (verified by reading shipped `.opt` files):
///   `45 6E 64 43`  "EndC" magic
///   `00 00 00 10`  size = 16 bytes (BE u32)
///   `00 00 00 00`  pad
///   `00 00 00 00`  pad
public static class OptSiblingWriter
{
    private static readonly byte[] OptStubBytes =
    {
        0x45, 0x6E, 0x64, 0x43,
        0x00, 0x00, 0x00, 0x10,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };

    /// Walks `<dbDirectory>` recursively, writing a `.opt` sibling next to
    /// every `.vlt` it finds (skipping any that already have one).
    public static void WriteSiblings(string dbDirectory, IList<string> writtenFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);

        if (!Directory.Exists(dbDirectory)) return;
        foreach (string vltPath in Directory.EnumerateFiles(dbDirectory, "*.vlt", SearchOption.AllDirectories))
        {
            string optPath = Path.ChangeExtension(vltPath, ".opt");
            if (File.Exists(optPath)) continue;
            File.WriteAllBytes(optPath, OptStubBytes);
            writtenFiles.Add(optPath);
        }
    }
}
