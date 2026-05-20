using ArenaBuilder.Texture;

namespace ArenaBuilder.WinForms;

internal static class NormalSynthSettingsStore
{
    private static readonly object Gate = new();

    // Start with pipeline defaults so MainForm can display them
    // even before the preview window is opened.
    private static DerivedTextureGenerator.NormalSynthSettings _current =
        DerivedTextureGenerator.DefaultNormalSettings;

    public static DerivedTextureGenerator.NormalSynthSettings Get()
    {
        lock (Gate)
            return _current;
    }

    public static void Save(DerivedTextureGenerator.NormalSynthSettings settings)
    {
        lock (Gate)
            _current = settings;
    }
}
