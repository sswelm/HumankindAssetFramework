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
            string copy=pb+".compare.glb";File.Copy(pb,copy,true);string gun=pb+".gun.txt";if(File.Exists(gun))File.Copy(gun,copy+".gun.txt",true);
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

    public static void RunChecks()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try
        {
            SecondSourceFingerprint();foreach(string key in new[]{"fx_e2_stances","fx_e3_deploy","fx_f_layers"})Preview(key);
            string recipe=Path.Combine(Arg("-hafGenerateRecipes"),"spanish_battleship_espana_split_fused.json");
            string golden="Tools/lab_golden/spanish_battleship_espana_split_fused.txt";byte[] original=File.ReadAllBytes(golden);
            var r=VehicleGenerateAcceptanceTest.RunRecipes(new[]{recipe},"Logs/stale_baseline","Tools/lab_golden");foreach(string l in r.body.Split('\n'))Row(l);
            Check("stale baseline rejected",r.fail==1&&r.body.Contains("stale"),"changed input provenance is not accepted");
            Check("stale golden preserved",original.SequenceEqual(File.ReadAllBytes(golden)),"existing golden is unchanged");
            var candidates=Directory.GetFiles("Logs/stale_baseline","*.candidate.txt",SearchOption.AllDirectories);Check("stale candidate retained",candidates.Length==1,candidates.FirstOrDefault()??"none");
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

    public static void Run()
    {
        output=Arg("-hafGenerateOutput");fixtures=Arg("-hafGenerateFixtures");
        try
        {
            var keys=new[]{"fx_e2_stances","fx_e3_deploy","fx_f_global","fx_f_layers","fx_f_merge_textures","fx_f_uv_inactive","fx_f_signed_zero"};
            var jobs=keys.Select(k=>Path.Combine(fixtures,"recipes",k+".json")).ToArray();
            SecondSourceFingerprint();Persistence(jobs[0]);Recipes("fixtures",jobs,"Tools/fixture_goldens");
            foreach(string key in new[]{"fx_e2_stances","fx_e3_deploy","fx_f_layers"})Preview(key);
            string saved=Arg("-hafGenerateRecipes");if(Directory.Exists(saved))Recipes("saved",Directory.GetFiles(saved,"*.json"),"Tools/lab_golden");else Check("saved recipes",false,"recipes not found");
        }
        catch(Exception e){Check("exception",false,e.ToString());}
        Row("TOTAL failed "+failed);EditorApplication.Exit(failed==0?0:1);
    }
}
