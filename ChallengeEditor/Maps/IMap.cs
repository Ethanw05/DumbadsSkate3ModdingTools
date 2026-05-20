namespace ChallengeEditor;

/// Common shape for every authorable map in a scene. Both <see cref="Dist"/>
/// (PSF-loaded) and <see cref="GlbMap"/> (GLB-loaded) implement this so the
/// scene tree, inspector pass-throughs (TriggerVolumes/Locators/Challenges/
/// Meshes), picking, and rendering can iterate maps without caring about the
/// source format.
public interface IMap
{
    Guid Id { get; }
    string Name { get; set; }
    List<TriggerVolume> TriggerVolumes { get; }
    List<Locator> Locators { get; }
    List<Challenge> Challenges { get; }
    List<ImportedMesh> Meshes { get; }
}
