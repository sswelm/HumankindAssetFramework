// The SPLITTER half of the Model Workshop: island split, plane cut, deletion marks.
// Unity restores a docked editor window by binding its saved layout entry to the MonoScript whose FILE NAME matches the
// class; a window class nested in another class's file does not bind, and the window silently vanishes on the next
// restart (user 2026-09-19: "why did the Model Splitter and Model Fuser dialog disappear after startup"). One file each.
using UnityEditor;

public class ModelSplitterWindow : ModelWorkshopWindow
{
    // NAMED "Model Cutter" since 2026-09-20 (user: "we are mainly cutting away bad or insignificant parts and only on
    // occasion split an object apart"). The CLASS keeps its old name on purpose: Unity stores a saved window layout by
    // type name, so renaming it would drop this window out of the user's layout and make them reopen it from the menu.
    [MenuItem("Tools/HAF/Model Cutter")]
    static void Open() => GetWindow<ModelSplitterWindow>("Model Cutter");
    protected override bool Fusing => false;
}
