using System;

namespace Haf.Schema
{
    // POC slice of the shared model schema — a handful of the fields that TODAY are duplicated across the editor's
    // ModelDef (128 fields) and the plugin's ModelEntry (148 fields, schema + runtime state), plus the plugin's two
    // parse paths. A plain [Serializable] POCO: Unity's JsonUtility serializes it (editor), Newtonsoft deserializes it
    // (plugin), and a runtime class INHERITS it to add its non-schema state — so the ~hundreds of `e.<schemaField>`
    // call sites on the hot path don't change. (Vector3 fields like rotation/position are omitted from this slice on
    // purpose: they're the one design call — reference UnityEngine for exact JSON-shape parity, or store floats.)
    [Serializable]
    public class HafModelSchemaPoc
    {
        public string resourceName = "";
        public string pawnDescription = "";
        public float size = 1f;
        public bool animated = false;
        public int[] skel = new int[4];   // the {a,b,c,d} GUID as JsonUtility/Newtonsoft both serialize an int[]
    }
}
