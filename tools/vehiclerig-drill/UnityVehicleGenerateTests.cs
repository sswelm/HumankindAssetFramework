using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class UnityVehicleGenerateTests
{
    static readonly List<string> Rows = new List<string>(); static int failed; static string output, fixtures;
    static string Arg(string key) { var a=Environment.GetCommandLineArgs();int i=Array.IndexOf(a,key);return i>=0&&i+1<a.Length?a[i+1]:null; }
    static void Row(string text) { Rows.Add(text);File.WriteAllLines(output,Rows); }
    static void Check(string name,bool ok,string detail) { Row((ok?"PASS ":"FAIL ")+name+": "+detail);if(!ok)failed++; }
    static FieldInfo Field(string name)=>typeof(VehicleLabWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
    static MethodInfo Method(string name)=>typeof(VehicleLabWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic);
    static void ParallelNormals()
    {
        bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            var x = new byte[4 * a.Length]; var y = new byte[4 * b.Length];
            Buffer.BlockCopy(a, 0, x, 0, x.Length); Buffer.BlockCopy(b, 0, y, 0, y.Length);
            return x.SequenceEqual(y);
        }
        foreach (float scale in new[] { 1f, 1e-18f })
        {
            const int size = 129;
            var points = new List<float>(); var normals = new List<float>(); var faces = new List<int>();
            for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++)
            {
                points.AddRange(new[] { x * scale, y * scale, (float)((x % 3 - y % 4) * .125) * scale });
                normals.AddRange((x + y) % 11 == 0 ? new[] { float.NaN, float.NaN, float.NaN } : new[] { .25f, -.5f, 1f });
            }
            points.AddRange(new[] { -0f, 0f, 0f, 2f, 3f, 4f }); normals.AddRange(new[] { 0f, 0f, 0f, 0f, 0f, 1f });
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                int a = y * (size + 1) + x, b = a + 1, c = a + size + 1, d = c + 1;
                faces.AddRange(new[] { a, b, d, a, d, c });
            }
            var p = points.ToArray(); var f = faces.ToArray(); var n = normals.ToArray();
            var sharp = Enumerable.Range(0, f.Length / 3).Select(i => i % 7 == 0).ToArray();
            var encoded = VehicleProbe.EncodeCustomShorts(p, f, n, sharp, 1);
            var decoded = VehicleProbe.DecodeCustomShorts(p, f, sharp, encoded.d0, encoded.d1, threads: 1);
            var corners = VehicleProbe.BlenderCornerNormals(p, f, sharp, encoded.d0, encoded.d1, threads: 1);
            var data = new short[2 * f.Length]; for (int i = 0; i < f.Length; i++) { data[2 * i] = encoded.d0[i]; data[2 * i + 1] = encoded.d1[i]; }
            var vectors = VehicleProbe.DecodeBMeshCustomNormals(p, f, sharp, data, 1);
            var bmesh = VehicleProbe.EncodeBMeshCustomNormals(p, f, sharp, vectors, 1);
            foreach (int workers in new[] { 2, 4, 8, 0 })
            {
                var actual = VehicleProbe.EncodeCustomShorts(p, f, n, sharp, workers);
                bool equal = encoded.d0.SequenceEqual(actual.d0) && encoded.d1.SequenceEqual(actual.d1)
                    && Same(decoded, VehicleProbe.DecodeCustomShorts(p, f, sharp, actual.d0, actual.d1, threads: workers))
                    && Same(corners, VehicleProbe.BlenderCornerNormals(p, f, sharp, actual.d0, actual.d1, threads: workers))
                    && Same(vectors, VehicleProbe.DecodeBMeshCustomNormals(p, f, sharp, data, workers))
                    && bmesh.SequenceEqual(VehicleProbe.EncodeBMeshCustomNormals(p, f, sharp, vectors, workers));
                Check("parallel normals " + scale + " workers=" + workers, equal, "serial and parallel shorts and float bits agree");
            }
        }
    }

    static void NativeRigMeshPersistence()
    {
        string folder = "Assets/__haf_native_mesh_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        GameObject root = null; var clones = new List<Mesh>();
        try
        {
            root = new GameObject("Root");
            var source = new Mesh { name = "Source" };
            source.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; source.triangles = new[] { 0, 1, 2 };
            source.uv = new[] { Vector2.zero, Vector2.right, Vector2.up }; source.bindposes = new[] { Matrix4x4.identity };
            source.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            AssetDatabase.CreateAsset(source, folder + "/test_rigmesh0.asset");
            var rigid = UnityEngine.Object.Instantiate(source); rigid.name = "Rigid";
            AssetDatabase.CreateAsset(rigid, folder + "/test_rigmesh1.asset");
            var unused = UnityEngine.Object.Instantiate(source);
            AssetDatabase.CreateAsset(unused, folder + "/test_rigmesh3.asset"); // a gap is not the end of the owned set
            var rigidChild = new GameObject("Rigid"); rigidChild.transform.SetParent(root.transform, false);
            rigidChild.AddComponent<MeshFilter>().sharedMesh = rigid; rigidChild.AddComponent<MeshRenderer>();
            for (int i = 0; i < 2; i++)
            {
                var child = new GameObject("Body" + i); child.transform.SetParent(root.transform, false);
                var smr = child.AddComponent<SkinnedMeshRenderer>(); smr.sharedMesh = source; smr.bones = new[] { root.transform }; smr.rootBone = root.transform;
            }
            string path = folder + "/rig.prefab"; var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root); root = null;
            var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var clone = UnityEngine.Object.Instantiate(renderers[i].sharedMesh); clones.Add(clone);
                clone.uv = i == 0 ? new[] { new Vector2(.25f, .75f), Vector2.right, Vector2.up } : new Vector2[0];
                renderers[i].sharedMesh = clone;
            }
            Check("native rig persists every transient mesh", UniversalBaker.PersistNativeRigMeshes(prefab, folder, "test") == 2, "both UV and no-UV clones become assets");
            Check("native rig retains rigid mesh assets", AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/test_rigmesh1.asset") != null,
                "MeshFilter references keep their original mesh alive");
            Check("native rig removes replaced originals across gaps", AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/test_rigmesh0.asset") == null && AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/test_rigmesh3.asset") == null,
                "only unused numbered import meshes are removed");
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path); renderers = loaded.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            bool intact = renderers.Length == 2 && renderers.All(r => r.sharedMesh != null && AssetDatabase.Contains(r.sharedMesh) && r.sharedMesh.vertexCount == 3);
            Check("native rig meshes survive prefab reload", intact, "SDK source retains persistent mesh references");
            Check("native rig rigid mesh survives prefab reload", loaded.GetComponentInChildren<MeshFilter>(true)?.sharedMesh != null,
                "rigid geometry remains on the saved prefab");
            if (intact)
            {
                Check("native rig remapped UVs survive", renderers[0].sharedMesh.uv[0] == new Vector2(.25f, .75f) && renderers[1].sharedMesh.uv.Length == 0, "data preserved across SaveAssets and reimport");
                Check("native rig persistence is idempotent", UniversalBaker.PersistNativeRigMeshes(loaded, folder, "test") == 0, "existing assets are retained");
                // Exercise the real preview writer: the rig cache is disposable, the preview's copied meshes are independent.
                string previewDir = folder + "/Preview"; AssetDatabase.CreateFolder(folder, "Preview");
                var atlas = new Texture2D(2, 2); AssetDatabase.CreateAsset(atlas, previewDir + "/atlas.asset");
                var originals = renderers.Select(r => r.sharedMesh).ToList();
                var originalPaths = originals.Select(AssetDatabase.GetAssetPath).ToArray();
                typeof(UniversalBaker).GetMethod("GeneratePreviewPrefab", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { "test", previewDir, path, atlas, Vector3.zero, originals });
                var copies = Enumerable.Range(0, originals.Count).Select(i => AssetDatabase.LoadAssetAtPath<Mesh>(UniversalBaker.PreviewMeshPath(previewDir, "test", i))).ToArray();
                bool copied = copies.All(m => m != null) && copies.Zip(originals, (copy, original) => copy != original && copy.vertices.SequenceEqual(original.vertices) && copy.uv.SequenceEqual(original.uv) && copy.bindposes.SequenceEqual(original.bindposes)).All(equal => equal);
                Check("native preview copies persistent meshes", copied && originalPaths.All(p => AssetDatabase.LoadAssetAtPath<Mesh>(p) != null), "all preview meshes are independent assets with the rig's data");
                foreach (string originalPath in originalPaths) AssetDatabase.DeleteAsset(originalPath);
                string previewPath = previewDir + "/test_Preview.prefab";
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
                AssetDatabase.ImportAsset(previewPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var preview = AssetDatabase.LoadAssetAtPath<GameObject>(previewPath);
                Check("native preview survives rig cache deletion", preview != null && preview.GetComponentInChildren<MeshFilter>()?.sharedMesh != null && copies.All(m => m != null && m.vertexCount == 3), "saved preview and every substitute survive deletion of their rig sources");
            }
        }
        finally
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            foreach (var mesh in clones) if (mesh != null && !AssetDatabase.Contains(mesh)) UnityEngine.Object.DestroyImmediate(mesh);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    static void ImageEncodingParity()
    {
        string recipe = Path.Combine(fixtures, "recipes", "fx_e2_stances.json");
        bool ok = VehicleLabWindow.GenerateHeadless(recipe, "Logs/image_encoding/source", "csharp", out string log, out string status, out string source, out string route, out _);
        Check("image encoding source", ok && route == "csharp", status); if (!ok || route != "csharp") return;
        var model = GlbReader.Read(source);
        if (model.Images.Count == 0) model.Images.Add(new HafImage { Name = "Encoding" });
        var image = model.Images[0]; image.Uri = "";
        var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            var colors = Enumerable.Range(0, 64).Select(i => new Color32((byte)(i * 3), (byte)(255 - i * 2), (byte)(i * 4), 255)).ToArray();
            texture.SetPixels32(colors); texture.Apply(); byte[] jpeg = ImageConversion.EncodeToJPG(texture, 95);
            if (!ImageConversion.LoadImage(decoded, jpeg)) throw new Exception("Synthetic JPEG did not decode.");
            byte[] png = ImageConversion.EncodeToPNG(decoded);
            string a = source + ".png.glb", b = source + ".jpeg.glb";
            void Write(string path, string mime, byte[] bytes) { image.MimeType = mime; image.Bytes = bytes; GlbWriter.Write(model, path); }
            Write(a, "image/png", png); Write(b, "image/jpeg", jpeg);
            Check("image encoding exact RGBA control", VehicleGenerateAcceptanceTest.CompareOutputs(a, log, b, log, out _).Count == 0, "source JPEG and PNG decoded from it match, all other bytes held");
            texture.SetPixels32(decoded.GetPixels32()); texture.Apply();
            Write(b, "image/jpeg", ImageConversion.EncodeToPNG(texture));
            Check("image encoding MIME mutation", VehicleGenerateAcceptanceTest.CompareOutputs(a, log, b, log, out _).Count > 0, "equal-pixel PNG payload falsely labelled JPEG is rejected");
            colors = Enumerable.Repeat(new Color32(240, 10, 20, 255), 64).ToArray(); texture.SetPixels32(colors); texture.Apply();
            Write(b, "image/jpeg", ImageConversion.EncodeToJPG(texture, 95));
            Check("image encoding RGB mutation", VehicleGenerateAcceptanceTest.CompareOutputs(a, log, b, log, out _).Count > 0, "different JPEG pixels rejected");
            Write(b, "image/jpeg", jpeg); var pixels = decoded.GetPixels32(); pixels[0].a = 127; texture.SetPixels32(pixels); texture.Apply();
            Write(a, "image/png", ImageConversion.EncodeToPNG(texture));
            Check("image encoding alpha mutation", VehicleGenerateAcceptanceTest.CompareOutputs(a, log, b, log, out _).Count > 0, "lost alpha rejected");
            Write(a, "image/png", png); image.Name += " changed"; Write(b, "image/jpeg", jpeg);
            Check("image encoding metadata mutation", VehicleGenerateAcceptanceTest.CompareOutputs(a, log, b, log, out _).Count > 0, "renamed image rejected");
            image.Name = image.Name.Substring(0, image.Name.Length - " changed".Length);
            model.Meshes[0].Primitives[0].Positions[0] += .125f; Write(b, "image/jpeg", jpeg);
            Check("image encoding mesh mutation", VehicleGenerateAcceptanceTest.CompareOutputs(a, log, b, log, out _).Count > 0, "geometry change beside equal pixels rejected");
        }
        finally { UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(decoded); }
    }

    static void Availability()
    {
        bool native = HafNativeKernels.Available;
        try
        {
            foreach(bool available in new[]{false,true})
            foreach(bool blender in new[]{false,true})
            foreach(string editorDefault in new[]{"csharp","blender","","invalid"})
            {
                HafNativeKernels.Available=available;
                var labels=VehicleLabWindow.GenerateBackendLabels(blender,editorDefault);
                bool csharpDefault=editorDefault!="blender";
                Check("backend labels native="+available+" blender="+blender+" default="+editorDefault,
                    labels[2].Contains("C++")==available && labels[2].Contains("managed")==!available
                    && labels[2].Contains("unavailable")==!blender
                    && labels[1].Contains("unavailable")==!blender && labels[0].Contains("unavailable")==!blender
                    && labels[0].Contains("C#")==csharpDefault
                    && (!csharpDefault || labels[0].Contains("C++")==available)
                    && (!blender || (labels[2].Contains("fallback to Blender") && (!csharpDefault || labels[0].Contains("fallback to Blender")))),string.Join("; ",labels));
            }
        }
        finally { HafNativeKernels.Available=native; }
        bool hadPreference=EditorPrefs.HasKey(VehicleLabWindow.GenerateRouteDefaultPref);
        string oldPreference=EditorPrefs.GetString(VehicleLabWindow.GenerateRouteDefaultPref,"");
        var w=ScriptableObject.CreateInstance<VehicleLabWindow>();
        try
        {
            Field("headless").SetValue(w,true);Field("headlessDir").SetValue(w,"Logs/availability");
            Field("findBlender").SetValue(w,new Func<string>(()=>null));
            Method("LoadRecipeFromPath").Invoke(w,new object[]{Path.Combine(fixtures,"recipes/fx_e2_stances.json")});
            string path=Path.GetFullPath("Logs/availability/rig.glb");Field("outGlb").SetValue(w,path);Field("generateRoute").SetValue(w,"");
            EditorPrefs.SetString(VehicleLabWindow.GenerateRouteDefaultPref,"");
            Method("Vehicleize").Invoke(w,null);
            string generatedStatus=(string)Field("status").GetValue(w);
            Check("factory default without Blender",w.lastGenerateRoute=="csharp"&&File.Exists(path)&&generatedStatus.Contains("DONE"),generatedStatus);
            Check("result native availability",generatedStatus.Contains("C++")==native && (native || generatedStatus.Contains("managed")),generatedStatus);
            if(!File.Exists(path))return;
            byte[] original=File.ReadAllBytes(path);
            string unsupported=Path.Combine(fixtures,"no_blender.fbx");File.WriteAllText(unsupported,"not launched");Field("srcFile").SetValue(w,unsupported);
            Method("Vehicleize").Invoke(w,null);string status=(string)Field("status").GetValue(w);
            Check("unavailable fallback explains why",w.lastGenerateRoute=="csharp"&&status.Contains("Blender fallback is unavailable")&&status.Contains(".fbx")&&!status.Contains("DONE"),status);
            Check("unavailable fallback preserves output",original.SequenceEqual(File.ReadAllBytes(path)),"previous GLB remains intact");
            Field("generateRoute").SetValue(w,"blender");Method("Vehicleize").Invoke(w,null);status=(string)Field("status").GetValue(w);
            Check("direct Blender unavailable",status.Contains("Blender is unavailable")&&!status.Contains("DONE")&&original.SequenceEqual(File.ReadAllBytes(path)),status);
        }
        finally
        {
            try { UnityEngine.Object.DestroyImmediate(w); }
            finally { if(hadPreference)EditorPrefs.SetString(VehicleLabWindow.GenerateRouteDefaultPref,oldPreference);else EditorPrefs.DeleteKey(VehicleLabWindow.GenerateRouteDefaultPref); }
        }
    }
    static void Recipes(string label,string[] jobs,string golden)
    {
        var captured=jobs.Where(j=>!File.Exists(Path.Combine(golden,Path.GetFileNameWithoutExtension(j)+".txt"))).ToArray();
        var r=VehicleGenerateAcceptanceTest.RunRecipes(jobs,"Logs/acceptance/"+label,golden);foreach(string l in r.body.Split('\n'))Row(label+" "+l);failed+=r.fail;
        Check(label+" exercised",r.pass+captured.Length>0,r.pass+" passed, "+r.fail+" failed, "+r.skip+" fallback/captured");
        if(captured.Length>0)
        {
            var again=VehicleGenerateAcceptanceTest.RunRecipes(captured,"Logs/acceptance/"+label+"_captured",golden);foreach(string l in again.body.Split('\n'))Row(label+" verified "+l);failed+=again.fail;
            Check(label+" captured goldens verified",again.pass>0&&again.fail==0,again.pass+" passes");
        }
    }
    static void Persistence(string recipe)
    {
        var w=ScriptableObject.CreateInstance<VehicleLabWindow>();
        try
        {
            Method("LoadRecipeFromPath").Invoke(w,new object[]{recipe});Field("generateRoute").SetValue(w,"csharp");
            string json=JsonUtility.ToJson(Method("BuildRecipe").Invoke(w,null));Check("recipe saves route",json.Contains("\"generateRoute\":\"csharp\""),"C# is recipe state");
            string saved=Path.Combine(fixtures,"route_saved.json");File.WriteAllText(saved,json);Method("LoadRecipeFromPath").Invoke(w,new object[]{saved});
            Check("recipe loads route",(string)Field("generateRoute").GetValue(w)=="csharp","explicit backend survives reload");
            Method("LoadRecipeFromPath").Invoke(w,new object[]{recipe});Check("old recipe clears route",(string)Field("generateRoute").GetValue(w)=="","absent backend returns to editor default");
            Field("generateRoute").SetValue(w,"csharp");Method("NewModel").Invoke(w,null);Check("new model clears route",(string)Field("generateRoute").GetValue(w)=="","no previous recipe backend leak");
        }
        finally{UnityEngine.Object.DestroyImmediate(w);}
    }
    static List<Vector3> Expected(HafModel model,int animation,double time)
    {
        var world=HafTransforms.WorldMatrices(model,animation<0?null:HafTransforms.PoseAt(model,animation,time));var points=new List<Vector3>();
        for(int n=0;n<model.Nodes.Count;n++)if(model.Nodes[n].Mesh>=0)
            foreach(var primitive in model.Meshes[model.Nodes[n].Mesh].Primitives)
            {
                var values=HafTransforms.WorldPositions(model,n,primitive,world);
                for(int v=0;v<values.Length;v+=3)points.Add(new Vector3((float)-values[v],(float)values[v+1],(float)values[v+2]));
            }
        return points;
    }
    static List<Vector3> Rendered(GameObject root)
    {
        var points=new List<Vector3>();
        foreach(var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh=new Mesh();try{renderer.BakeMesh(mesh);points.AddRange(mesh.vertices.Select(v=>renderer.transform.TransformPoint(v)));}finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }
        foreach(var filter in root.GetComponentsInChildren<MeshFilter>())points.AddRange(filter.sharedMesh.vertices.Select(v=>filter.transform.TransformPoint(v)));
        return points;
    }
    static void Preview(string key)
    {
        string recipe=Path.Combine(fixtures,"recipes",key+".json");
        bool a=VehicleLabWindow.GenerateHeadless(recipe,"Logs/preview/"+key+"/blender","blender",out string la,out string sa,out string pa,out _,out _);
        bool b=VehicleLabWindow.GenerateHeadless(recipe,"Logs/preview/"+key+"/csharp","csharp",out string lb,out string sb,out string pb,out string route,out string fallback);
        Check(key+" preview runs",a&&b&&route=="csharp",!a?sa:!b?sb:route+" "+fallback);if(!a||!b||route!="csharp")return;
        var w=ScriptableObject.CreateInstance<VehicleLabWindow>();
        try
        {
            // Blender's actual GLB is the reference. Its reader samples poses independently of
            // Unity's clips/skinning and the window's preview wiring; no FBX unit conventions enter.
            var reference=GlbReader.Read(pa);w.BuildGlbPreview(pb);var go=(GameObject)Field("inst").GetValue(w);var clips=(List<AnimationClip>)Field("previewClips").GetValue(w);
            Check(key+" GLB preview",go!=null&&go.GetComponentsInChildren<SkinnedMeshRenderer>().Length>0,"live skinned preview");
            Check(key+" action names",clips.Select(c=>c.name).OrderBy(x=>x).SequenceEqual(reference.Animations.Select(c=>c.Name).OrderBy(x=>x)),string.Join(",",clips.Select(c=>c.name)));
            if(clips.Any(c=>c.name=="Spin"))Check(key+" Spin first",clips[0].name=="Spin",string.Join(",",clips.Select(c=>c.name)));
            var rest=Expected(reference,-1,0);var box=new Bounds(rest[0],Vector3.zero);foreach(var point in rest)box.Encapsulate(point);float tolerance=Mathf.Max(.0001f,box.size.magnitude*.0001f);
            Check(key+" rest preview",RigRouteAcceptanceTest.ComparePositionSets(rest,Rendered(go),tolerance,out _)==null,"Unity skinned vertices match Blender GLB read-back");
            foreach(var clip in clips)
            {
                int index=reference.Animations.FindIndex(an=>an.Name==clip.name);double duration=reference.Animations[index].Duration;string problem=null;int samples=0;
                for(int f=0;f<=Math.Ceiling(duration*24);f++)
                {
                    double time=Math.Min(duration,f/24.0);clip.SampleAnimation(go,(float)time);samples++;
                    problem=RigRouteAcceptanceTest.ComparePositionSets(Expected(reference,index,time),Rendered(go),tolerance,out _);if(problem!=null)break;
                }
                Check(key+" "+clip.name+" sampled preview",problem==null&&Math.Abs(clip.length-duration)<.001,samples+" frames: "+problem);
            }
            if(clips.Count>0)clips[0].SampleAnimation(go,0);
            var expected=Expected(reference,clips.Count>0?reference.Animations.FindIndex(an=>an.Name==clips[0].name):-1,0);
            go.transform.localScale*=1.1f;Check(key+" preview scale mutation",RigRouteAcceptanceTest.ComparePositionSets(expected,Rendered(go),tolerance,out _)!=null,"incorrect preview placement/scale rejected");go.transform.localScale=Vector3.one;
            Check(key+" no auxiliary FBX",!File.Exists(Path.Combine(Path.GetDirectoryName(pb),key+"_Spin_preview.fbx")),"C# preview consumes its GLB");
            string copy=pb+".compare.glb";File.Copy(pb,copy,true);string gun=pb+".gun.txt";if(File.Exists(gun))File.Copy(gun,copy+".gun.txt",true);else if(File.Exists(copy+".gun.txt"))File.Delete(copy+".gun.txt");
            Check("acceptance intact control",VehicleGenerateAcceptanceTest.CompareOutputs(pb,lb,copy,lb,out _).Count==0,"real written rig");
            Check("acceptance log mutation",VehicleGenerateAcceptanceTest.CompareOutputs(pb,lb,copy,lb.Replace("VEHICLE export totals:","VEHICLE export totals: changed"),out _).Count>0,"changed diagnostics rejected");
            File.WriteAllText(copy+".gun.txt","changed");Check("acceptance gun mutation",VehicleGenerateAcceptanceTest.CompareOutputs(pb,lb,copy,lb,out _).Count>0,"changed or spurious gun span rejected");
            var bytes=File.ReadAllBytes(copy);bytes[bytes.Length-1]^=1;File.WriteAllBytes(copy,bytes);
            bool caught=false;try{caught=VehicleGenerateAcceptanceTest.CompareOutputs(pb,lb,copy,lb,out _).Count>0;}catch{caught=true;}Check("acceptance file mutation",caught,"changed GLB rejected");
        }
        finally{Method("DestroyPreview").Invoke(w,null);UnityEngine.Object.DestroyImmediate(w);}
    }
    static void SecondSourceFingerprint()
    {
        string recipe=Path.Combine(fixtures,"recipes/fx_f_merge_textures.json");var json=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(recipe));string source=(string)json["srcFile"],second=(string)json["srcFile2"];
        var before=VehicleGenerateAcceptanceTest.GoldenInputs(recipe,source);Check("second source fingerprint",before.ContainsKey("source2"),"merged model belongs to golden provenance");
        byte[] bytes=File.ReadAllBytes(second);try{var changed=(byte[])bytes.Clone();changed[changed.Length-1]^=1;File.WriteAllBytes(second,changed);var after=VehicleGenerateAcceptanceTest.GoldenInputs(recipe,source);Check("second source mutation",before["source2"]!=after["source2"]&&before["recipe"]==after["recipe"],"changed second model invalidates provenance without a recipe edit");}finally{File.WriteAllBytes(second,bytes);}
    }

    static void StaleBaseline()
    {
        // Own every input: repairing a saved project's golden must not break this regression check.
        string work="Logs/stale_fixture/"+Guid.NewGuid().ToString("N");
        string recipe=Path.Combine(fixtures,"stale_baseline.json"),goldenDir=work+"/goldens";
        string golden=Path.Combine(goldenDir,"stale_baseline.txt");
        var json=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(fixtures,"recipes/fx_e2_stances.json")));
        File.WriteAllText(recipe,json.ToString());
        var captured=VehicleGenerateAcceptanceTest.RunRecipes(new[]{recipe},work+"/capture",goldenDir);
        Check("stale fixture captured",captured.fail==0&&captured.skip==1&&File.Exists(golden),captured.body);
        if(!File.Exists(golden))return;
        var intact=VehicleGenerateAcceptanceTest.RunRecipes(new[]{recipe},work+"/intact",goldenDir);
        Check("stale fixture intact",intact.fail==0&&intact.pass==1,intact.body);
        byte[] original=File.ReadAllBytes(golden);
        json["degrees"]=-180;File.WriteAllText(recipe,json.ToString());
        var stale=VehicleGenerateAcceptanceTest.RunRecipes(new[]{recipe},work+"/changed",goldenDir);
        Check("stale baseline rejected",stale.fail==1&&stale.body.Contains("stale by input"),stale.body);
        Check("stale golden preserved",original.SequenceEqual(File.ReadAllBytes(golden)),"existing golden is unchanged");
        var candidates=Directory.Exists(work+"/changed")?Directory.GetFiles(work+"/changed","*.candidate.txt",SearchOption.AllDirectories):new string[0];
        Check("stale candidate retained",candidates.Length==1,candidates.FirstOrDefault()??"none");
        if(candidates.Length!=1)return;
        File.Copy(candidates[0],golden,true); // This synthetic golden alone is deliberately refreshed.
        var refreshed=VehicleGenerateAcceptanceTest.RunRecipes(new[]{recipe},work+"/refreshed",goldenDir);
        Check("refreshed baseline verified",refreshed.fail==0&&refreshed.pass==1,refreshed.body);
    }

    // Real Generate entry point, including any Blender fallback, in the isolated project. These are measurements,
    // not timing assertions: CPU load changes timings. Every repeated run must keep its route and complete GLB bytes.
    public static void RunPerformance()
    {
        output = Arg("-hafGenerateOutput");
        try
        {
            string recipes = Arg("-hafGenerateRecipes");
            string requested = Arg("-hafGeneratePerformanceModels");
            var keys = (string.IsNullOrEmpty(requested) ? "khelandion_2,triconter_split,salegs_revenge_split_fused,spanish_battleship_espana_split_fused" : requested).Split(',');
            string runsText = Arg("-hafGeneratePerformanceRuns");
            int runs = string.IsNullOrEmpty(runsText) ? 3 : int.Parse(runsText, System.Globalization.CultureInfo.InvariantCulture);
            if (runs < 1 || runs > 10) throw new ArgumentOutOfRangeException("runs", "Use 1 to 10 repetitions.");
            foreach (string key in keys)
            {
                string referenceHash = null, referenceRoute = null;
                for (int i = 0; i < runs; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = VehicleLabWindow.GenerateHeadless(Path.Combine(recipes, key + ".json"), "Logs/performance/" + key, "csharp",
                        out _, out string status, out string path, out string route, out string fallback);
                    watch.Stop();
                    if (!ok) { Check("performance " + key, false, status); break; }
                    string hash;
                    using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
                    Check("performance " + key + " run " + (i + 1), referenceHash == null || (referenceHash == hash && referenceRoute == route),
                        watch.ElapsedMilliseconds + " ms; route=" + route + "; sha256=" + hash + (string.IsNullOrEmpty(fallback) ? "" : "; fallback=" + fallback));
                    referenceHash = hash; referenceRoute = route;
                }
            }
        }
        catch (Exception e) { Check("exception", false, e.ToString()); }
        Row("TOTAL failed " + failed); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    public static void RunChecks()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try
        {
            ParallelNormals();NativeRigMeshPersistence();ImageEncodingParity();Availability();SecondSourceFingerprint();foreach(string key in new[]{"fx_e2_stances","fx_e3_deploy","fx_f_layers"})Preview(key);
            StaleBaseline();
        }
        catch(Exception e){Check("exception",false,e.ToString());}
        Row("TOTAL failed "+failed);EditorApplication.Exit(failed==0?0:1);
    }

    // The saved recipes alone, or the subset -hafGenerateOnly names (comma-separated recipe stems): the way a stale Lab golden is
    // re-blessed - the drill deletes the named goldens from ITS copy of the project, this captures them from Blender and verifies
    // the capture by a second generation on both backends (Recipes). Nothing here touches the user's project.
    public static void RunSaved()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try
        {
            string saved=Arg("-hafGenerateRecipes");var only=(Arg("-hafGenerateOnly")??"").Split(new[]{','},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim()).Where(s=>s.Length>0).ToArray();
            if(!Directory.Exists(saved))Check("saved recipes",false,"recipes not found");
            else
            {
                var jobs=Directory.GetFiles(saved,"*.json").Where(j=>only.Length==0||only.Contains(Path.GetFileNameWithoutExtension(j),StringComparer.OrdinalIgnoreCase)).ToArray();
                Check("saved recipes selected",jobs.Length>0&&(only.Length==0||jobs.Length==only.Length),jobs.Length+" recipe(s)"+(only.Length>0?" of "+string.Join(",",only):""));
                if(jobs.Length>0)Recipes("saved",jobs,"Tools/lab_golden");
            }
        }
        catch(Exception e){Check("exception",false,e.ToString());}
        Row("TOTAL failed "+failed);EditorApplication.Exit(failed==0?0:1);
    }

    public static void RunPreview()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try { SecondSourceFingerprint(); foreach(string key in new[]{"fx_e2_stances","fx_e3_deploy","fx_f_layers"})Preview(key); }
        catch(Exception e){Check("exception",false,e.ToString());}
        Row("TOTAL failed "+failed);EditorApplication.Exit(failed==0?0:1);
    }

    public static void RunAvailability()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try{Availability();}catch(Exception e){Check("exception",false,e.ToString());}
        Row("TOTAL failed "+failed);EditorApplication.Exit(failed==0?0:1);
    }

    public static void Run()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try
        {
            var keys=new[]{"fx_e2_stances","fx_e3_deploy","fx_f_global","fx_f_layers","fx_f_merge_textures","fx_f_uv_inactive","fx_f_signed_zero"};
            var jobs=keys.Select(k=>Path.Combine(fixtures,"recipes",k+".json")).ToArray();
            ParallelNormals();NativeRigMeshPersistence();ImageEncodingParity();Availability();SecondSourceFingerprint();Persistence(jobs[0]);StaleBaseline();Recipes("fixtures",jobs,"Tools/fixture_goldens");
            foreach(string key in new[]{"fx_e2_stances","fx_e3_deploy","fx_f_layers"})Preview(key);
            string saved=Arg("-hafGenerateRecipes");if(Directory.Exists(saved))Recipes("saved",Directory.GetFiles(saved,"*.json"),"Tools/lab_golden");else Check("saved recipes",false,"recipes not found");
        }
        catch(Exception e){Check("exception",false,e.ToString());}
        Row("TOTAL failed "+failed);EditorApplication.Exit(failed==0?0:1);
    }
}
