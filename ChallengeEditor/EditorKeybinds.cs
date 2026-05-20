using System.Text.Json;
using Veldrid;
using Key = Veldrid.Key;

namespace ChallengeEditor;

/// <summary>Remappable editor shortcuts (keyboard or mouse; Ctrl+File* use keyboard keys with Ctrl held).</summary>
public sealed class EditorKeybinds
{
    public EditorInputBinding CamForward { get; set; } = EditorInputBinding.FromKey(Key.W);
    public EditorInputBinding CamBack { get; set; } = EditorInputBinding.FromKey(Key.S);
    public EditorInputBinding CamStrafeLeft { get; set; } = EditorInputBinding.FromKey(Key.A);
    public EditorInputBinding CamStrafeRight { get; set; } = EditorInputBinding.FromKey(Key.D);
    public EditorInputBinding CamUp { get; set; } = EditorInputBinding.FromKey(Key.E);
    public EditorInputBinding CamDown { get; set; } = EditorInputBinding.FromKey(Key.Q);

    /// <summary>Switch between orbit camera and fly camera (keyboard or mouse).</summary>
    public EditorInputBinding ToggleFlyCamera { get; set; } = EditorInputBinding.FromKey(Key.F2);

    public EditorInputBinding ToolMove { get; set; } = EditorInputBinding.FromKey(Key.M);
    public EditorInputBinding ToolRotate { get; set; } = EditorInputBinding.FromKey(Key.R);
    public EditorInputBinding ToolScale { get; set; } = EditorInputBinding.FromKey(Key.T);

    public EditorInputBinding FrameSelection { get; set; } = EditorInputBinding.FromKey(Key.F);
    public EditorInputBinding Deselect { get; set; } = EditorInputBinding.FromKey(Key.Escape);
    public EditorInputBinding DeleteSelected { get; set; } = EditorInputBinding.FromKey(Key.Delete);
    public EditorInputBinding DeleteSelectedAlt { get; set; } = EditorInputBinding.FromKey(Key.BackSpace);

    public EditorInputBinding FileSave { get; set; } = EditorInputBinding.FromKey(Key.S);
    public EditorInputBinding FileOpen { get; set; } = EditorInputBinding.FromKey(Key.O);
    public EditorInputBinding FileUndo { get; set; } = EditorInputBinding.FromKey(Key.Z);

    public static string DefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeEditor",
            "keybinds.json");

    public static EditorKeybinds CreateDefaults() => new();

    public static EditorKeybinds LoadOrDefaults(string? path = null)
    {
        path ??= DefaultConfigPath();
        try
        {
            if (!File.Exists(path)) return CreateDefaults();
            string json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<EditorKeybinds>(json, opts) ?? CreateDefaults();
        }
        catch
        {
            return CreateDefaults();
        }
    }

    public void WriteToDisk(string? path = null)
    {
        path ??= DefaultConfigPath();
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
    }

    public static string LabelForProperty(string propName) => propName switch
    {
        nameof(CamForward) => "Camera · move forward",
        nameof(CamBack) => "Camera · move back",
        nameof(CamStrafeLeft) => "Camera · strafe left",
        nameof(CamStrafeRight) => "Camera · strafe right",
        nameof(CamUp) => "Camera · move up",
        nameof(CamDown) => "Camera · move down",
        nameof(ToggleFlyCamera) => "Camera · toggle fly / orbit",
        nameof(ToolMove) => "Tool · move (tap again: world/local)",
        nameof(ToolRotate) => "Tool · rotate",
        nameof(ToolScale) => "Tool · scale",
        nameof(FrameSelection) => "View · frame selection / all",
        nameof(Deselect) => "Selection · deselect / cancel gizmo",
        nameof(DeleteSelected) => "Edit · delete selected",
        nameof(DeleteSelectedAlt) => "Edit · delete selected (alternate)",
        nameof(FileSave) => "File · save (with Ctrl)",
        nameof(FileOpen) => "File · open (with Ctrl)",
        nameof(FileUndo) => "Edit · undo (with Ctrl)",
        _ => propName
    };

    public static IReadOnlyList<string> AllPropertyNames { get; } =
    [
        nameof(CamForward),
        nameof(CamBack),
        nameof(CamStrafeLeft),
        nameof(CamStrafeRight),
        nameof(CamUp),
        nameof(CamDown),
        nameof(ToggleFlyCamera),
        nameof(ToolMove),
        nameof(ToolRotate),
        nameof(ToolScale),
        nameof(FrameSelection),
        nameof(Deselect),
        nameof(DeleteSelected),
        nameof(DeleteSelectedAlt),
        nameof(FileSave),
        nameof(FileOpen),
        nameof(FileUndo),
    ];

    public EditorInputBinding GetBinding(string propertyName) => propertyName switch
    {
        nameof(CamForward) => CamForward,
        nameof(CamBack) => CamBack,
        nameof(CamStrafeLeft) => CamStrafeLeft,
        nameof(CamStrafeRight) => CamStrafeRight,
        nameof(CamUp) => CamUp,
        nameof(CamDown) => CamDown,
        nameof(ToggleFlyCamera) => ToggleFlyCamera,
        nameof(ToolMove) => ToolMove,
        nameof(ToolRotate) => ToolRotate,
        nameof(ToolScale) => ToolScale,
        nameof(FrameSelection) => FrameSelection,
        nameof(Deselect) => Deselect,
        nameof(DeleteSelected) => DeleteSelected,
        nameof(DeleteSelectedAlt) => DeleteSelectedAlt,
        nameof(FileSave) => FileSave,
        nameof(FileOpen) => FileOpen,
        nameof(FileUndo) => FileUndo,
        _ => default
    };

    public void SetBinding(string propertyName, EditorInputBinding value)
    {
        switch (propertyName)
        {
            case nameof(CamForward): CamForward = value; break;
            case nameof(CamBack): CamBack = value; break;
            case nameof(CamStrafeLeft): CamStrafeLeft = value; break;
            case nameof(CamStrafeRight): CamStrafeRight = value; break;
            case nameof(CamUp): CamUp = value; break;
            case nameof(CamDown): CamDown = value; break;
            case nameof(ToggleFlyCamera): ToggleFlyCamera = value; break;
            case nameof(ToolMove): ToolMove = value; break;
            case nameof(ToolRotate): ToolRotate = value; break;
            case nameof(ToolScale): ToolScale = value; break;
            case nameof(FrameSelection): FrameSelection = value; break;
            case nameof(Deselect): Deselect = value; break;
            case nameof(DeleteSelected): DeleteSelected = value; break;
            case nameof(DeleteSelectedAlt): DeleteSelectedAlt = value; break;
            case nameof(FileSave): FileSave = value; break;
            case nameof(FileOpen): FileOpen = value; break;
            case nameof(FileUndo): FileUndo = value; break;
        }
    }
}
