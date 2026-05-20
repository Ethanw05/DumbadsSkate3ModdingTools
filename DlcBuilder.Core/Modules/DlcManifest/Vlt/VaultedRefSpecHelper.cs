using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt;

/// Helper for building tVaultedRefSpec bin entries paired with their PtrN
/// path-string fixup. The engine's `sub_A6D400` path-string dispatch reads
/// `*(blob+0x18)` as a `const char*` and constructs `"data/db/<that>"` to
/// load the referenced vault file. Callers must ship a real path string for
/// every cross-VLT vaulted ref or the parent-chain walk fails (PPU access
/// violation reading 0x20 inside `sub_737790`'s
/// `j_Vault_FindCollectionByHash` chain).
public static class VaultedRefSpecHelper
{
    /// Adds a 32-byte tVaultedRefSpec(className, rowKey) blob to the bin pool
    /// AND a `"<class>\<rowKey>.vlt"` path string. Registers the +0x18 PtrN
    /// fixup pointing the blob at the path string. Returns the blob's bin
    /// offset.
    public static uint AddVaultedRefSpecWithPath(
        BinPoolBuilder bin,
        List<(uint, uint)> binFixups,
        string className,
        string rowKey)
    {
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(binFixups);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        string path = $"{className}\\{rowKey}.vlt";
        uint pathOff = bin.AddString(path);
        uint blobOff = bin.AddBlob(VltBinHelpers.BuildVaultedRefSpec(className, rowKey));
        binFixups.Add((blobOff + 0x18u, pathOff));
        return blobOff;
    }
}
