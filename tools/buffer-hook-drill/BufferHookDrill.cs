using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HumankindAssetFramework;

// An isolated process: patch the real HAF prefix/postfix/finalizer onto the decompiled Register signature and
// its array-write failure shape. No Unity native calls, game launch, GPU allocation or user's config is needed.
public static class BufferHookDrill
{
    public struct Mesh { public int VertexCount; }
    public struct Input { public string Guid; public int VertexCount, QuadCount; public bool Reject; }
    public class Data { public Mesh[] WriteContent = new Mesh[3]; public int Size => WriteContent.Length; }
    public class Pool { public int Size = 100; }
    public class Layer
    {
        public string name = Guid.NewGuid().ToString("N");
        public int currentMeshAddedCount = 1, currentVertexIndex = 90, currentIndexIndex = 1;
        public int baseVertexBufferSize = 100, baseIndexBufferSize = 1000, maxMeshCount = 3, maxMeshTriangleCount;
        public Pool vertexBuffer = new Pool(), indexBuffer = new Pool { Size = 1000 };
        public Data hxFxOneMeshComputeBuffer = new Data();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Register(string meshName, ref Input fxMeshContent)
        {
            int slot = currentMeshAddedCount;
            int padded = ((fxMeshContent.QuadCount * 4 + 63) / 64) * 64;
            bool refused = currentVertexIndex + fxMeshContent.VertexCount > vertexBuffer.Size;
            if (!refused)
            {
                currentVertexIndex += fxMeshContent.VertexCount;
                refused = fxMeshContent.Reject || currentIndexIndex + padded > indexBuffer.Size;
            }
            var mesh = new Mesh { VertexCount = refused ? 0 : fxMeshContent.VertexCount };
            // The engine logs a full mesh table and then still performs this write: it throws before a postfix runs.
            hxFxOneMeshComputeBuffer.WriteContent[currentMeshAddedCount] = mesh;
            currentMeshAddedCount++;
            if (!refused) currentIndexIndex += padded;
            return slot;
        }
    }
    public class Manager { public static int IntBufferChunkSize = 64; public Layer[] Layers; }
    public class Animation
    {
        public static Animation Instance { get; set; }
        public Manager FxComponentMeshContentManager;
        public int FXMeshLayerIndex;
    }
    static readonly Assembly Haf = typeof(Plugin).Assembly;
    static FieldInfo Field(Type t, string name) => t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    static Type Internal(string name) => Haf.GetType("HumankindAssetFramework." + name, true);
    static MethodInfo Method(Type t, string name) => t.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    static int[] Budgets(params Layer[] layers)
    {
        Animation.Instance = new Animation { FxComponentMeshContentManager = new Manager { Layers = layers } };
        var rows = (IList)Method(Internal("UniversalInject"), "ReadMeshBudget").Invoke(null, new object[] { null, -1 });
        return rows.Cast<object>().Select(r => (int)r.GetType().GetField("Dropped").GetValue(r)).ToArray();
    }
    public static int Main(string[] args)
    {
        int failed = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
        var errors = new List<string>(); var logger = new ManualLogSource("buffer-hook-drill");
        logger.LogEvent += (s, e) => { if ((e.Level & LogLevel.Error) != 0) errors.Add(e.Data?.ToString() ?? ""); };
        Field(typeof(Plugin), "Log").SetValue(null, logger);
        var types = (IDictionary)Field(Internal("GameBinding"), "_typeCache").GetValue(null);
        types["Amplitude.Graphics.Fx.FxComponentMeshContentManager"] = typeof(Manager);
        types["Amplitude.Mercury.Animation.AnimationManager"] = typeof(Animation);
        var patch = Internal("Hk_MeshDropWatch"); var harmony = new Harmony("haf.review.buffer-hook");
        HarmonyMethod Adapter(string name) => Method(patch, name) is MethodInfo method ? new HarmonyMethod(method) : null;
        try
        {
            harmony.Patch(typeof(Layer).GetMethod("Register"), prefix: Adapter("Prefix"), postfix: Adapter("Postfix"), finalizer: Adapter("Finalizer"));
            var input = new Input { Guid = "a", VertexCount = 20, QuadCount = 1 };
            var good = new Layer { currentVertexIndex = 1 }; good.Register("healthy", ref input);
            Check("healthy registration emits no drop", !errors.Any(e => e.Contains("DROPPED")));
            errors.Clear(); var refused = new Layer(); refused.Register("too-large", ref input);
            Check("capacity refusal is named without cursor movement", errors.Any(e => e.Contains("DROPPED") && e.Contains("too-large")) && refused.currentVertexIndex == 90);
            errors.Clear(); var tableFull = new Layer { currentMeshAddedCount = 3 }; bool threw = false;
            try { tableFull.Register("table-full", ref input); } catch (IndexOutOfRangeException) { threw = true; }
            Check("table overflow is named and original exception propagates", threw && errors.Any(e => e.Contains("DROPPED") && e.Contains("table-full")));
            errors.Clear(); var corrupt = new Layer { currentVertexIndex = 1 }; input.Reject = true; corrupt.Register("bad-data", ref input); input.Reject = false;
            Check("encoder rejection does not advise enlarging buffers", errors.Any(e => e.Contains("bad-data") && e.Contains("encoder")) && !errors.Any(e => e.Contains("Raise")));
            var namesake = new Layer { name = refused.name };
            Check("same-named layers have separate refusal counts", Budgets(refused, namesake).SequenceEqual(new[] { 1, 0 }));
            refused.hxFxOneMeshComputeBuffer = new Data();
            Check("a rebuilt buffer clears old refusals", Budgets(refused).SequenceEqual(new[] { 0 }));
            var repaired = new Layer(); repaired.Register("repair", ref input); repaired.hxFxOneMeshComputeBuffer.WriteContent[1].VertexCount = 20;
            Check("repaired entries clear their refusal", Budgets(repaired).SequenceEqual(new[] { 0 }));

            var config = new ConfigFile(Path.Combine(args[0], "test.cfg"), false) { SaveOnConfigSet = false };
            var entry = config.Bind("Buffers", "BufferOverrides", "");
            Field(typeof(Plugin), "BufferOverrides").SetValue(null, entry);
            void Apply(Layer layer, string spec)
            {
                entry.Value = spec; Field(Internal("Hk_DistrictBufferHeadroom"), "parsed").SetValue(null, null);
                Method(Internal("Hk_DistrictBufferHeadroom"), "Prefix").Invoke(null, new object[] { layer });
            }
            var overflow = new Layer(); Apply(overflow, overflow.name + ":verts=+2147483647");
            Check("vertex additions cannot overflow to negative sizes", overflow.baseVertexBufferSize == 100);
            var negative = new Layer(); Apply(negative, negative.name + ":idx=-1000");
            Check("index subtraction cannot create a zero-size buffer", negative.baseIndexBufferSize == 1000);
            var zero = new Layer { baseIndexBufferSize = 20_000_000 }; Apply(zero, zero.name + ":idx=0");
            Check("idx=0 cannot bypass the packed-index ceiling", zero.baseIndexBufferSize == 16_777_216);
        }
        catch (Exception ex) { Console.WriteLine("FAIL harness: " + ex.GetType().Name + ": " + ex.Message); failed++; }
        finally { harmony.UnpatchSelf(); }
        return failed == 0 ? 0 : 1;
    }
}
