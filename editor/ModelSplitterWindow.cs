// The SPLITTER half of the Model Workshop: island split, plane cut, deletion marks.
// Unity restores a docked editor window by binding its saved layout entry to the MonoScript whose FILE NAME matches the
// class; a window class nested in another class's file does not bind, and the window silently vanishes on the next
// restart (user 2026-09-19: "why did the Model Splitter and Model Fuser dialog disappear after startup"). One file each.
using UnityEditor;

public class ModelSplitterWindow : ModelWorkshopWindow
{
    [MenuItem("Tools/HAF/Model Splitter")]
    static void Open() => GetWindow<ModelSplitterWindow>("Model Splitter");
    protected override bool Fusing => false;
}
