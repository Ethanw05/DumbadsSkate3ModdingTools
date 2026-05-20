using System.Windows.Forms;

namespace ChallengeEditor;

/// File-picker dialogs for scene save/open. Uses WinForms (already a project
/// reference via UseWindowsForms=true). Returns null on cancel.
public static class SceneFilePicker
{
    private const string FilterText = "Challenge Editor Scene (*.cescn)|*.cescn|All Files (*.*)|*.*";
    private const string DefaultExt = "cescn";

    public static string? PickOpen(string title, string? initialPath = null)
    {
        using var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = FilterText,
            DefaultExt = DefaultExt,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
            dlg.FileName = initialPath;
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    }

    public static string? PickSave(string title, string? initialPath = null)
    {
        using var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = FilterText,
            DefaultExt = DefaultExt,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (!string.IsNullOrWhiteSpace(initialPath))
            dlg.FileName = initialPath;
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    }
}
