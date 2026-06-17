using DlcBuilder.Inputs;
using DlcBuilder.Outputs;

namespace DlcBuilder;

/// Optional flags controlling post-staging steps. The default ctor keeps the
/// orchestrator behaving exactly like the parameterless `Build(input, outputDir)`
/// overload — write the loose staging tree, no packing.
public sealed record BuildOptions
{
    /// When true, run `bigfile.exe` over the staging tree and produce
    /// `&lt;outputDirectory&gt;/&lt;DlcFolder&gt;/custom_&lt;slug&gt;.big.edat`.
    public bool PackBig { get; init; }

    /// When true (and OTS challenges are present), run `Stream File Tool.exe`
    /// over each per-OTS `cSim_Global` folder to produce `cSim_Global.psf`.
    /// Independent from `PackBig` because the PSF pack step is also useful
    /// when shipping the loose tree to a real PS3 dev unit.
    public bool PackOtsPsf { get; init; }

    /// When true (and PackBig succeeded), delete the loose `&lt;outputDirectory&gt;/data/`
    /// staging tree so only the packed `.big.edat` remains. Off by default so
    /// users can inspect what was built.
    public bool CleanStagingAfterPack { get; init; }

    /// Target console. <see cref="DlcPlatform.Ps3"/> (default) builds the loose
    /// tree + `custom_&lt;slug&gt;.big.edat` for RPCS3/real PS3. <see cref="DlcPlatform.Xbox360"/>
    /// targets the skate3recomp: generated arenas as `.rx2`, the package packed
    /// into a raw unencrypted `&lt;slug&gt;_00000000.big` (NO `.edat`), placed in the
    /// recomp Content tree under `…\454108E6\00000002\&lt;ContentID&gt;\`.
    public DlcPlatform Platform { get; init; } = DlcPlatform.Ps3;

    /// Convenience: stage + pack both BIG and OTS PSFs in one shot.
    public static BuildOptions FullPack { get; } = new() { PackBig = true, PackOtsPsf = true };
}

/// Public entry point for any front-end (editor UI, CLI, batch tools). The
/// implementation may be a stub (writes a manifest only), the full real builder
/// (writes VLT/PSF/BIG artifacts), or a hybrid that runs whichever modules it
/// has so far. Callers don't care which — they just hand over a PackageInput
/// and an output directory.
public interface IDlcBuilder
{
    /// Builds the DLC. Returns a BuildResult with diagnostics and a list of files
    /// actually written. Never throws for content errors — those go in
    /// Diagnostics with Level=Error and Status=Failed. Throws only for
    /// programmer errors (null inputs, etc.).
    BuildResult Build(PackageInput input, string outputDirectory);

    /// Overload accepting build options (BIG packing, OTS PSF packing, etc.).
    /// Default-implementation routes to the no-options overload for builders
    /// that don't support options yet.
    BuildResult Build(PackageInput input, string outputDirectory, BuildOptions options) =>
        Build(input, outputDirectory);
}
