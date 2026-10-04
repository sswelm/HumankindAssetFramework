// Copy into Assets/Editor of a scratch Unity project with the authoring package installed.
// Run Unity -batchmode -projectPath <scratch> -executeMethod HighlightDrill.Run -logFile <log>.
// Requires a graphics device: omit -nographics. Uses only synthetic geometry; no model files are written.
using System;
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
            Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "graphics device required; omit -nographics");
            foreach (var type in new[] { typeof(ModelFuserWindow), typeof(ModelSplitterWindow) }) Exercise(type);
            Debug.Log("HIGHLIGHT_DRILL PASS: both sides visible, depth occlusion retained, original material restored in both windows");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
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
            var select = type.GetMethod("SelectRow", Flags);
            select.Invoke(window, new[] { row });
            highlight = (Material)type.GetField("highlightMat", Flags).GetValue(window);
            Require(renderer.sharedMaterial == highlight, "actual selection material applied");
            int front = Draw(Vector3.forward, false), back = Draw(Vector3.back, false);
            Require(front > 100 && back > 100, "selected plate must render from both sides");
            Require(Draw(Vector3.forward, true) == 0 && Draw(Vector3.back, true) == 0, "selection must remain hidden behind other geometry");
            select.Invoke(window, new object[] { null });
            Require(renderer.sharedMaterial == original, "original preview material restored");
            Require(Draw(Vector3.forward, false) == 0 && Draw(Vector3.back, false) == 0, "no highlight left after deselection");
            Debug.Log("HIGHLIGHT_DRILL " + windowType.Name + " pixels front=" + front + " back=" + back);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(window);
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(original);
            if (highlight != null) UnityEngine.Object.DestroyImmediate(highlight);
        }
    }

    static int Draw(Vector3 direction, bool occluded)
    {
        var cameraGO = new GameObject("highlight drill camera");
        var camera = cameraGO.AddComponent<Camera>();
        var target = new RenderTexture(128, 128, 24);
        var pixels = new Texture2D(128, 128, TextureFormat.RGB24, false);
        GameObject blocker = null; Material black = null;
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
            UnityEngine.Object.DestroyImmediate(cameraGO);
        }
    }
}
