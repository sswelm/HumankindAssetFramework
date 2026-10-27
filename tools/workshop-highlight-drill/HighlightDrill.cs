// Copy into Assets/Editor of a scratch Unity project with the authoring package installed.
// Run Unity -batchmode -projectPath <scratch> -executeMethod HighlightDrill.Run -logFile <log>.
// Requires a graphics device: omit -nographics. Uses only synthetic geometry; no model files are written.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class HighlightDrill
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run()
    {
        try
        {
            CheckLifecycle();
            Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "graphics device required; omit -nographics");
            foreach (var type in new[] { typeof(ModelFuserWindow), typeof(ModelSplitterWindow) }) Exercise(type);
            Debug.Log("HIGHLIGHT_DRILL PASS: both sides visible over coincident twins, depth occlusion retained, original material restored in both windows");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    // No graphics required: also run with -batchmode -nographics -executeMethod HighlightDrill.RunLifecycle.
    // EditorPrefs in a ScriptableObject initializer throws in Unity even when compilation and every .NET test pass.
    public static void RunLifecycle()
    {
        try { CheckLifecycle(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void CheckLifecycle()
    {
        const string key = "HAF.ModelFuser.GroupsAtOnce";
        bool hadPreference = EditorPrefs.HasKey(key); int oldPreference = EditorPrefs.GetInt(key, 0);
        var setting = typeof(ModelWorkshopWindow).GetField("groupsAtOnce", Flags);
        var live = new List<UnityEngine.Object>();
        string saved = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haf_window_" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var type in new[] { typeof(ModelFuserWindow), typeof(ModelSplitterWindow) })
            {
                EditorPrefs.DeleteKey(key);
                var defaults = ScriptableObject.CreateInstance(type); live.Add(defaults);
                Require((int)setting.GetValue(defaults) == 0, type.Name + " must default to auto");
                UnityEngine.Object.DestroyImmediate(defaults);
                EditorPrefs.SetInt(key, 3);
                var window = ScriptableObject.CreateInstance(type); live.Add(window);
                Require((int)setting.GetValue(window) == 3, type.Name + " must load the saved setting on creation");
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new[] { window }, saved, true);
                UnityEngine.Object.DestroyImmediate(window);
                EditorPrefs.SetInt(key, 1);
                var restored = UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(saved); live.AddRange(restored);
                Require(restored.Length == 1 && type.IsInstanceOfType(restored[0]), type.Name + " must restore its serialized window");
                Require((int)setting.GetValue(restored[0]) == 1, type.Name + " must reload the preference on restoration");
                UnityEngine.Object.DestroyImmediate(restored[0]);
                Debug.Log("WINDOW_LIFECYCLE " + type.Name + " default=auto saved=3 restored=1");
            }
            Debug.Log("WINDOW_LIFECYCLE PASS: both windows initialize and restore their saved fuse setting");
        }
        finally
        {
            foreach (var window in live) if (window != null) UnityEngine.Object.DestroyImmediate(window);
            if (hadPreference) EditorPrefs.SetInt(key, oldPreference); else EditorPrefs.DeleteKey(key);
            if (System.IO.File.Exists(saved)) System.IO.File.Delete(saved);
        }
    }

    static void Exercise(Type windowType)
    {
        var window = ScriptableObject.CreateInstance(windowType);
        var mesh = new Mesh { vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0) }, triangles = new[] { 0, 1, 2 } };
        mesh.RecalculateBounds();
        var original = new Material(Shader.Find("Unlit/Color")) { color = Color.blue };
        Material highlight = null;
        try
        {
            var type = typeof(ModelWorkshopWindow);
            var root = new GameObject("highlight drill preview");
            var part = new GameObject("inward plate") { layer = 31 };
            part.transform.SetParent(root.transform);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>(); renderer.sharedMaterial = original;
            type.GetField("inst", Flags).SetValue(window, root);
            var map = (Dictionary<int, Renderer>)type.GetField("previewByNode", Flags).GetValue(window);
            map.Add(123, renderer);
            var rowType = type.GetNestedType("Row", BindingFlags.NonPublic);
            var row = Activator.CreateInstance(rowType, true);
            rowType.GetField("nodeIndex").SetValue(row, 123);
            rowType.GetField("node").SetValue(row, "inward plate");
            var otherRow = Activator.CreateInstance(rowType, true);
            rowType.GetField("nodeIndex").SetValue(otherRow, 124);
            rowType.GetField("node").SetValue(otherRow, "other part");
            var rows = (IList)type.GetField("rows", Flags).GetValue(window);
            rows.Add(row); rows.Add(otherRow);
            var otherGO = new GameObject("other part") { layer = 31 };
            otherGO.transform.SetParent(root.transform); otherGO.transform.localPosition = Vector3.right * 10;
            otherGO.AddComponent<MeshFilter>().sharedMesh = mesh;
            var otherRenderer = otherGO.AddComponent<MeshRenderer>(); otherRenderer.sharedMaterial = original;
            map.Add(124, otherRenderer);
            var select = type.GetMethod("SelectRow", Flags);
            select.Invoke(window, new[] { row });
            highlight = (Material)type.GetField("highlightMat", Flags).GetValue(window);
            Require(renderer.sharedMaterial == highlight, "actual selection material applied");
            int front = Draw(Vector3.forward, false), back = Draw(Vector3.back, false);
            Require(front > 100 && back > 100, "selected plate must render from both sides");
            int twinFront = Draw(Vector3.forward, false, true), twinBack = Draw(Vector3.back, false, true);
            Require(twinFront == front && twinBack == back, "coincident opposite-facing twin must not cover the selection");
            Require(Draw(Vector3.forward, true) == 0 && Draw(Vector3.back, true) == 0, "selection must remain hidden behind other geometry");
            select.Invoke(window, new object[] { null });
            Require(renderer.sharedMaterial == original, "original preview material restored");
            Require(Draw(Vector3.forward, false) == 0 && Draw(Vector3.back, false) == 0, "no highlight left after deselection");
            type.GetField("isolateSelected", Flags).SetValue(window, true);
            select.Invoke(window, new[] { row });
            Require(renderer.enabled && !otherRenderer.enabled, "isolation shows only the selected node");
            select.Invoke(window, new[] { otherRow });
            Require(!renderer.enabled && otherRenderer.enabled, "isolation follows a changed selection");
            select.Invoke(window, new object[] { null });
            Require(renderer.enabled && otherRenderer.enabled, "deselection restores all preview parts");
            rowType.GetField("delete").SetValue(otherRow, true);
            type.GetField("showOnly", Flags).SetValue(window, WorkshopRules.HideDeletedView);
            select.Invoke(window, new[] { row });
            select.Invoke(window, new object[] { null });
            Require(renderer.enabled && !otherRenderer.enabled, "deselection still respects Hide deleted parts");
            type.GetField("showOnly", Flags).SetValue(window, 0);
            select.Invoke(window, new[] { row });
            type.GetField("isolateSelected", Flags).SetValue(window, false);
            type.GetMethod("UpdatePreviewVisibility", Flags).Invoke(window, null);
            Require(renderer.enabled && otherRenderer.enabled, "turning isolation off restores ordinary preview");
            Require(rows.Count == 2 && (int)rowType.GetField("nodeIndex").GetValue(row) == 123
                && (int)rowType.GetField("nodeIndex").GetValue(otherRow) == 124
                && (bool)rowType.GetField("delete").GetValue(otherRow), "isolation preserves node numbers and marks");
            type.GetField("isolateSelected", Flags).SetValue(window, true);
            type.GetMethod("ResetFilters", Flags).Invoke(window, null);
            type.GetMethod("UpdatePreviewVisibility", Flags).Invoke(window, null);
            Require(!(bool)type.GetField("isolateSelected", Flags).GetValue(window) && renderer.enabled && otherRenderer.enabled,
                "Show all exits isolation and restores every part");
            select.Invoke(window, new object[] { null });
            Debug.Log("HIGHLIGHT_DRILL " + windowType.Name + " pixels front=" + front + " back=" + back + " twin front=" + twinFront + " twin back=" + twinBack);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(window);
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(original);
            if (highlight != null) UnityEngine.Object.DestroyImmediate(highlight);
        }
    }

    static int Draw(Vector3 direction, bool occluded, bool coincident = false)
    {
        var cameraGO = new GameObject("highlight drill camera");
        var camera = cameraGO.AddComponent<Camera>();
        var target = new RenderTexture(128, 128, 24);
        var pixels = new Texture2D(128, 128, TextureFormat.RGB24, false);
        GameObject blocker = null; Material black = null; Mesh twinMesh = null;
        var previous = RenderTexture.active;
        try
        {
            if (occluded)
            {
                blocker = GameObject.CreatePrimitive(PrimitiveType.Cube); blocker.layer = 31;
                blocker.transform.position = direction * 0.5f; blocker.transform.localScale = new Vector3(3, 3, 0.1f);
                black = new Material(Shader.Find("Unlit/Color")) { color = Color.black };
                blocker.GetComponent<Renderer>().sharedMaterial = black;
            }
            else if (coincident)
            {
                // SketchUp's separate back-face material is a second node occupying the same surface with
                // reversed winding. Draw the twin after the selection to expose the coplanar depth tie.
                blocker = new GameObject("opposite-facing twin") { layer = 31 };
                twinMesh = new Mesh { vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0) },
                    triangles = direction.z > 0 ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 } };
                twinMesh.RecalculateBounds();
                blocker.AddComponent<MeshFilter>().sharedMesh = twinMesh;
                black = new Material(Shader.Find("Unlit/Color")) { color = Color.black, renderQueue = 2001 };
                blocker.AddComponent<MeshRenderer>().sharedMaterial = black;
            }
            target.Create(); camera.targetTexture = target; camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.orthographic = true; camera.orthographicSize = 1.5f;
            camera.nearClipPlane = 0.01f; camera.farClipPlane = 10;
            camera.transform.position = direction * 3; camera.transform.LookAt(Vector3.zero);
            camera.Render(); RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
            return pixels.GetPixels32().Count(c => c.r > 150 && c.g > 100 && c.b < 100);
        }
        finally
        {
            RenderTexture.active = previous; camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(pixels); target.Release(); UnityEngine.Object.DestroyImmediate(target);
            if (blocker != null) UnityEngine.Object.DestroyImmediate(blocker);
            if (black != null) UnityEngine.Object.DestroyImmediate(black);
            if (twinMesh != null) UnityEngine.Object.DestroyImmediate(twinMesh);
            UnityEngine.Object.DestroyImmediate(cameraGO);
        }
    }
}
