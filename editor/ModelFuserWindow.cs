// The FUSER half of the Model Workshop: ⊕ groups welded into one shell each, deletion marks.
// Unity restores a docked editor window by binding its saved layout entry to the MonoScript whose FILE NAME matches the
// class; a window class nested in another class's file does not bind, and the window silently vanishes on the next
// restart (user 2026-09-19: "why did the Model Splitter and Model Fuser dialog disappear after startup"). One file each.
using UnityEditor;

public class ModelFuserWindow : ModelWorkshopWindow
{
    [MenuItem("Tools/HAF/Model Fuser")]
    static void Open() => GetWindow<ModelFuserWindow>("Model Fuser");
    protected override bool Fusing => true;
}
