// Minimal stand-ins for the Unity APIs SingleSourceRegistry touches, so the REAL engine source runs against real
// files outside Unity. JsonUtility's contract as the engine relies on it: FromJson of a non-object throws or returns
// null, `{}` gives an object with its field initialisers, FromJsonOverwrite REPLACES lists.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnityEngine
{
    public static class JsonUtility
    {
        static readonly JsonSerializerSettings S = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
        public static T FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default(T);
            var t = json.TrimStart();
            if (!t.StartsWith("{")) throw new ArgumentException("JSON parse error: not an object");
            return JsonConvert.DeserializeObject<T>(json, S);
        }
        public static string ToJson(object o) => JsonConvert.SerializeObject(o);
        public static string ToJson(object o, bool pretty) => JsonConvert.SerializeObject(o, pretty ? Formatting.Indented : Formatting.None);
        public static void FromJsonOverwrite(string json, object o) => JsonConvert.PopulateObject(json, o, S);
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
