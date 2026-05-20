using DlcBuilder.Modules.Orchestrator;

namespace DlcBuilder;

/// Factory access to builder implementations. Front-ends should call
/// `CreateDefault()` — currently the only implementation, the
/// `DlcBuildOrchestrator` that chains every real module (DlcManifest +
/// challengebanks + progressionbanks + framework VLT + per-OTS VLTs +
/// FE language pack + locator PSGs + mission stubs + BIG pack).
public static class DlcBuilders
{
    public static IDlcBuilder CreateDefault() => new DlcBuildOrchestrator();
}
