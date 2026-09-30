// Minimal stand-ins for the Unity APIs SingleSourceRegistry touches, so the REAL engine source runs against real
// files outside Unity. They must be no KINDER than Unity, or they hide bugs (review of PR #103):
//   * JsonUtility matches keys CASE-SENSITIVELY and ignores unknown ones — Newtonsoft matches case-insensitively, so
//     keys that aren't an exact field name are pruned before deserialising ("Items" is not "items");
//   * FromJson of a non-object throws, of blank text returns null; `{}` gives an object with its field initialisers;
//   * no FromJsonOverwrite: the engine doesn't use it, and a stand-in that did would be an unverified guess.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityEngine
{
    public static class JsonUtility
    {
        public static T FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default(T);
            var tok = JToken.Parse(json);   // throws on garbage, like Unity's parse error
            if (!(tok is JObject obj)) throw new ArgumentException("JSON must represent an object type.");
            Prune(obj, typeof(T));
            return obj.ToObject<T>();
        }

        // Drop every key that is not EXACTLY a public instance field of the target type (Unity's rule), recursively.
        static void Prune(JToken tok, Type t)
        {
            if (tok is JObject o)
            {
                foreach (var p in o.Properties().ToList())
                {
                    var f = t.GetField(p.Name, BindingFlags.Public | BindingFlags.Instance);   // case-sensitive
                    if (f == null) { p.Remove(); continue; }
                    Prune(p.Value, f.FieldType);
                }
            }
            else if (tok is JArray a)
            {
                var et = t.IsArray ? t.GetElementType() : t.IsGenericType ? t.GetGenericArguments()[0] : typeof(object);
                foreach (var e in a) Prune(e, et);
            }
        }

        public static string ToJson(object o) => JsonConvert.SerializeObject(o);
        public static string ToJson(object o, bool pretty) => JsonConvert.SerializeObject(o, pretty ? Formatting.Indented : Formatting.None);
    }

    public static class Debug
    {
        public static readonly List<string> Lines = new List<string>();
        public static void Log(object m) { Lines.Add("INFO " + m); }
        public static void LogWarning(object m) { Lines.Add("WARN " + m); }
        public static void LogError(object m) { Lines.Add("ERROR " + m); }
    }

    public static class Application { public static string dataPath = ""; }
}

namespace UnityEditor
{
    // Unity's EditorPrefs span every project on the machine; the drill's are cleared per scenario on purpose, and the
    // per-project migration marker is tested explicitly (M4).
    public static class EditorPrefs
    {
        public static readonly Dictionary<string, object> P = new Dictionary<string, object>();
        public static bool GetBool(string k, bool d) => P.TryGetValue(k, out var v) ? (bool)v : d;
        public static void SetBool(string k, bool v) => P[k] = v;
        public static string GetString(string k, string d) => P.TryGetValue(k, out var v) ? (string)v : d;
        public static void SetString(string k, string v) => P[k] = v;
        public static void DeleteKey(string k) => P.Remove(k);
    }
    public static class AssetDatabase { public static void Refresh() { } }
    public static class EditorApplication { public static double timeSinceStartup; }
}
