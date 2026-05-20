using DlcBuilder.Builders;

namespace DlcBuilder.Modules.DlcManifest.Vlt.Templates;

/// `freeskate_dlc_<area>.vlt` — per-area freeskate stub. One row:
/// `challenge_local_data/freeskate_dlc_<area>` with parent
/// `<framework>_freeskate_locations`, 0 attrs, 1 type slot
/// (`Sk8::Audio::eSk8Characters`), 48-byte zero-filled layout.
///
/// Verified against retail Danny Way `freeskate_dlc_dwmc.vlt`. Earlier
/// 4-byte layouts crashed at runtime because the engine read 44 bytes of
/// adjacent StrE strings as layout content; 48 zeros is "empty layout, all
/// schema defaults".
public static class FreeskateStubVltBuilder
{
    public sealed record StubArtifacts(string FileName, byte[] VltBytes, byte[] BinBytes);

    public static StubArtifacts Build(DlcManifest.DlcSpec map, string frameworkKey)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);

        string fileName = "freeskate_dlc_" + map.Slug;
        string vltFileName = fileName + ".vlt";
        string binFileName = fileName + ".bin";

        var bin = new BinPoolBuilder();
        uint stubLayoutOff = bin.AddBlob(new byte[48]);

        var stubRow = VltCollectionBuilder.BuildBareCollection(
            "challenge_local_data",
            key: fileName,
            parent: frameworkKey + "_freeskate_locations",
            layoutOffset: stubLayoutOff,
            explicitTypes: new[] { "Sk8::Audio::eSk8Characters" });

        var collections = new List<CollectionBlob> { stubRow };
        byte[] vltBytes = VltFileWriter.BuildVltWithCollections(
            vltFileName, binFileName, collections, Array.Empty<(uint, uint)>());
        byte[] binBytes = bin.BuildBinFile();
        return new StubArtifacts(fileName, vltBytes, binBytes);
    }
}
