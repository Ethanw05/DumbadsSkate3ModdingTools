namespace ChallengeEditor.Rendering;

public enum EditorTool { Select, Move, Rotate, Scale }

public enum GizmoAxis { None, X, Y, Z }

/// World = drag along world XYZ. Local = drag along the selected object's
/// rotated local axes. Currently only the Move tool honors this.
public enum GizmoSpace { World, Local }
