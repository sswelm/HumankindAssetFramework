// AtlasDebug.cs (ENC editor) — tiny diagnostic: dump a baked _Atlas.asset (or any Texture2D) to a readable PNG so its
// ACTUAL pixels can be inspected outside Unity. The baked atlas is DXT1-compressed and non-readable, so we blit it
// through a LINEAR RenderTexture (raw copy, no sRGB gamma shift that would itself darken the result) and read it back.
// Select the atlas in the Project view, then Tools ▸ ENC ▸ Export selected atlas to PNG.
using System.IO;
using UnityEditor;
using UnityEngine;

public static class AtlasDebug
{
    [MenuItem("Tools/HAF/Export selected atlas to PNG")]
    static void ExportSelectedAtlas()
    {
        var tex = Selection.activeObject as Texture2D;
        if (tex == null) { Debug.LogError("[AtlasDebug] Select a Texture2D (e.g. <name>_Atlas.asset) in the Project view first."); return; }

        // LINEAR read-write = raw pass-through, so the export reflects the atlas's stored colours exactly (no gamma applied).
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0); readable.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);

        // report average colour so we get a number even without opening the PNG
        var px = readable.GetPixels32();
        long r = 0, g = 0, b = 0;
        for (int i = 0; i < px.Length; i++) { r += px[i].r; g += px[i].g; b += px[i].b; }
        int n = Mathf.Max(1, px.Length);
        var dir = "C:/tmp"; Directory.CreateDirectory(dir);
        var path = dir + "/" + tex.name + "_export.png";
        File.WriteAllBytes(path, readable.EncodeToPNG());
        Object.DestroyImmediate(readable);
        Debug.Log($"[AtlasDebug] '{tex.name}' {tex.width}x{tex.height} {tex.format} — avg RGB=({r / n},{g / n},{b / n}) — exported to {path}");
    }
}
