using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Haf.Schema;

// A plugin-side runtime class INHERITING the shared schema — this is the ModelEntry : HafModelSchema shape. It proves
// the inherited schema fields deserialize while the subclass carries its own runtime-only state (not in the JSON).
class ModelEntryPoc : HafModelSchemaPoc
{
    public bool texOwned;          // runtime-only
    public string coreDesc = "";   // runtime-only
}

static class Program
{
    static int Main()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + what); if (!ok) fails++; }

        // a pack.json-style model entry
        string json = "{\"resourceName\":\"MyTank\",\"pawnDescription\":\"Era6_X\",\"size\":8.0,\"animated\":true,\"skel\":[1,2,3,4]}";

        // 1) Newtonsoft (the plugin's parser) deserializes the SHARED schema type
        var s = JsonConvert.DeserializeObject<HafModelSchemaPoc>(json);
        Check(s.resourceName == "MyTank" && s.size == 8f && s.animated && s.skel.SequenceEqual(new[] { 1, 2, 3, 4 }),
              "Newtonsoft -> shared HafModelSchemaPoc (string/float/bool/int[])");

        // 2) a runtime subclass INHERITS the schema; Newtonsoft fills the inherited fields; runtime-only fields default
        var e = JsonConvert.DeserializeObject<ModelEntryPoc>(json);
        Check(e.resourceName == "MyTank" && e.skel.SequenceEqual(new[] { 1, 2, 3, 4 }) && !e.texOwned && e.coreDesc == "",
              "Newtonsoft -> ModelEntryPoc : HafModelSchemaPoc (inherited schema + runtime-only extras, no churn)");

        // 3) serialize back — the JSON SHAPE the editor's JsonUtility must also produce for the plugin to read it
        var back = JObject.Parse(JsonConvert.SerializeObject(s));
        Check((string)back["resourceName"] == "MyTank" && (int)back["skel"][0] == 1 && back["skel"].Count() == 4,
              "serialize -> same JSON shape (resourceName + skel int[])");

        Console.WriteLine(fails == 0
            ? "\nPOC PASS - a shared netstandard2.0 schema round-trips through Newtonsoft and is inheritance-friendly (plugin side proven)."
            : "\nPOC FAIL");
        return fails == 0 ? 0 : 1;
    }
}
