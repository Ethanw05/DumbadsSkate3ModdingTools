using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace DlcBuilder
{
    /// Target console for a DLC build. PS3 is the historical default (loose tree
    /// + `.big.edat` for RPCS3); Xbox360 targets the skate3recomp (rx2 arenas,
    /// raw unencrypted `.big`, Content-tree placement).
    public enum DlcPlatform
    {
        Ps3 = 0,
        Xbox360 = 1,
    }
}

namespace DlcBuilder.Modules
{
    /// Per-platform build knobs derived from <see cref="DlcPlatform"/>. Threaded
    /// through the PSG-emitting writers + the pack step so the same orchestrator
    /// produces either a PS3 or an Xbox 360 package.
    ///
    ///  • <see cref="Arena"/>          — RW4 arena platform for GeneralArenaBuilder.Write.
    ///  • <see cref="PsgExt"/>         — generated-arena file extension (.psg / .rx2).
    ///  • <see cref="FeImageExt"/>     — FE menu location image extension (.rps3 / .rx2).
    ///  • <see cref="StreamToolPlatform"/> — Stream File Tool `--platform=` letter (p / x).
    ///  • <see cref="PackEdatSuffix"/> — true → `.big.edat` (PS3); false → raw `.big` (X360).
    public sealed record PlatformProfile(
        ArenaPlatform Arena,
        string PsgExt,
        string FeImageExt,
        string StreamToolPlatform,
        bool PackEdatSuffix)
    {
        public static readonly PlatformProfile Ps3 =
            new(ArenaPlatform.Ps3, ".psg", ".rps3", "p", PackEdatSuffix: true);

        public static readonly PlatformProfile Xbox360 =
            new(ArenaPlatform.Xbox360, ".rx2", ".rx2", "x", PackEdatSuffix: false);

        public static PlatformProfile For(DlcPlatform platform) =>
            platform == DlcPlatform.Xbox360 ? Xbox360 : Ps3;
    }
}
