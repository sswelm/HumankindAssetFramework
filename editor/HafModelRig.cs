// HafModelRig.cs - a HafModel as a LIVE Unity rig: the node hierarchy, its meshes (skinned ones as SkinnedMeshRenderers with
// the file's joints as bones and its inverse bind matrices as bindposes), and one legacy AnimationClip per glTF animation,
// baked at the dialog's frame rate from the reader's sampler arithmetic (HafTransforms.Sample: STEP, LINEAR with slerp,
// CUBICSPLINE) - so the Clip Range picker plays a .glb/.gltf in Unity's own skinning and transform path without Blender
// converting it to inspection FBXs first (step 4 of replacing Blender, 2026-10-03; inspect_fbx.py keeps FBX/.blend sources).
// The frame is HafUnityFrame's (X mirrored, as Unity's FBX import of a Blender export lands), faces rewound for it; the
// clips carry Blender's track names (BlenderNames.TrackNames), which is what rig_anim.py looks a clip spec up by.
// Every Unity object made goes to `assets` for the caller to destroy with the rig.
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

public static class HafModelRig
{
    public sealed class Result
    {
        public GameObject Root;
        public AnimationClip[] Clips = new AnimationClip[0];
        public string[] ClipNames = new string[0];   // Blender's track names: the exact names a clip spec carries
        public Transform[] Nodes;                    // per glTF node
        public int Meshes, SkinnedMeshes, Vertices, Triangles;
        public string Note = "";
    }

    public static Result Build(HafModel m, List<UnityEngine.Object> assets, float fps = 24f)
    {
        var r = new Result();
        var root = new GameObject("__hafRig") { hideFlags = HideFlags.HideAndDontSave }; assets.Add(root);
        r.Root = root;
        // ---- the hierarchy: a GameObject per node, every node (Blender imports them all, scene or not), named uniquely so a
        //      clip's curve path finds exactly one
        var tr = new Transform[m.Nodes.Count]; r.Nodes = tr;
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var go = new GameObject((m.Nodes[i].Name.Length > 0 ? m.Nodes[i].Name : "node") + " #" + i) { hideFlags = HideFlags.HideAndDontSave };
            tr[i] = go.transform;
        }
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            int p = m.Nodes[i].Parent;
            tr[i].SetParent(p >= 0 && p < tr.Length ? tr[p] : root.transform, false);
            HafUnityFrame.LocalTrs(m.Nodes[i], out var t, out var q, out var s);
            tr[i].localPosition = V3(t); tr[i].localRotation = Q(q); tr[i].localScale = V3(s);
        }
        // ---- the meshes
        var sh = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        var textures = new Dictionary<int, Texture2D>(); var materials = new Dictionary<int, Material>();
        var built = new Dictionary<(int mesh, int skin), (Mesh mesh, Material[] mats)>();
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var node = m.Nodes[i];
            if (node.Mesh < 0 || node.Mesh >= m.Meshes.Count) continue;
            var hm = m.Meshes[node.Mesh];
            if (hm.Primitives.All(p => p.TriangleCount == 0)) continue;
            bool skinned = node.Skin >= 0 && node.Skin < m.Skins.Count && hm.Primitives.Any(p => p.Skinned);
            var key = (node.Mesh, skinned ? node.Skin : -1);
            if (!built.TryGetValue(key, out var mb))
            {
                mb = BuildMesh(m, node.Mesh, skinned ? node.Skin : -1, sh, textures, materials, assets);
                built[key] = mb;
                r.Meshes++; r.Vertices += mb.mesh.vertexCount; r.Triangles += (int)hm.Primitives.Sum(p => p.TriangleCount);
            }
            var go = tr[i].gameObject;
            if (skinned)
            {
                var sk = m.Skins[node.Skin];
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mb.mesh;
                smr.bones = sk.Joints.Select(j => j >= 0 && j < tr.Length ? tr[j] : root.transform).ToArray();
                smr.rootBone = sk.Skeleton >= 0 && sk.Skeleton < tr.Length ? tr[sk.Skeleton] : (sk.Joints.Length > 0 && sk.Joints[0] < tr.Length ? tr[sk.Joints[0]] : root.transform);
                smr.updateWhenOffscreen = true;   // the bounds follow the pose: the picker frames and culls by them
                smr.sharedMaterials = mb.mats;
                r.SkinnedMeshes++;
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = mb.mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mb.mats;
            }
        }
        // ---- the clips: one legacy clip per animation, every animated node's position, rotation and scale keyed at every frame
        var names = BlenderNames.TrackNames(m);
        var clips = new List<AnimationClip>();
        for (int ai = 0; ai < m.Animations.Count; ai++)
        {
            var anim = m.Animations[ai];
            var clip = new AnimationClip { name = names[ai], legacy = true, frameRate = fps, hideFlags = HideFlags.HideAndDontSave };
            assets.Add(clip);
            int frames = Mathf.Max(1, Mathf.RoundToInt((float)(anim.Duration * fps)));
            var byNode = new Dictionary<int, (HafSampler t, HafSampler r, HafSampler s)>();
            foreach (var ch in anim.Channels)
            {
                if (ch.Node < 0 || ch.Node >= m.Nodes.Count || ch.Sampler < 0 || ch.Sampler >= anim.Samplers.Count) continue;
                var have = byNode.TryGetValue(ch.Node, out var v) ? v : (null, null, null);
                var smp = anim.Samplers[ch.Sampler];
                if (ch.Path == "translation") have.t = smp; else if (ch.Path == "rotation") have.r = smp; else if (ch.Path == "scale") have.s = smp; else continue;
                byNode[ch.Node] = have;
            }
            foreach (var kv in byNode)
            {
                string path = AnimationUtility.CalculateTransformPath(tr[kv.Key], root.transform);
                HafUnityFrame.LocalTrs(m.Nodes[kv.Key], out var t0, out var q0, out var s0);
                var keys = new List<Keyframe>[10]; for (int k = 0; k < 10; k++) keys[k] = new List<Keyframe>(frames + 1);
                double[] prevQ = null;
                for (int f = 0; f <= frames; f++)
                {
                    double time = f / (double)fps;
                    var tv = kv.Value.t != null ? HafTransforms.Sample(kv.Value.t, time) : null;
                    var rv = kv.Value.r != null ? HafTransforms.Sample(kv.Value.r, time) : null;
                    var sv = kv.Value.s != null ? HafTransforms.Sample(kv.Value.s, time) : null;
                    var t = tv != null && tv.Length == 3 ? HafUnityFrame.Point(tv[0], tv[1], tv[2]) : t0;
                    var q = rv != null && rv.Length == 4 ? HafUnityFrame.Rotation(rv) : q0;
                    var s = sv != null && sv.Length == 3 ? sv : s0;
                    // one hemisphere from key to key: Unity interpolates the components, and q and -q are the same rotation
                    if (prevQ != null && q[0] * prevQ[0] + q[1] * prevQ[1] + q[2] * prevQ[2] + q[3] * prevQ[3] < 0) q = new[] { -q[0], -q[1], -q[2], -q[3] };
                    prevQ = q;
                    float ft = (float)time;
                    for (int k = 0; k < 3; k++) keys[k].Add(new Keyframe(ft, (float)t[k]));
                    for (int k = 0; k < 4; k++) keys[3 + k].Add(new Keyframe(ft, (float)q[k]));
                    for (int k = 0; k < 3; k++) keys[7 + k].Add(new Keyframe(ft, (float)s[k]));
                }
                string[] props = { "localPosition.x", "localPosition.y", "localPosition.z", "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w", "localScale.x", "localScale.y", "localScale.z" };
                for (int k = 0; k < 10; k++)
                {
                    if (k < 3 && kv.Value.t == null) continue;
                    if (k >= 3 && k < 7 && kv.Value.r == null) continue;
                    if (k >= 7 && kv.Value.s == null) continue;
                    clip.SetCurve(path, typeof(Transform), props[k], Linear(keys[k]));
                }
            }
            clip.EnsureQuaternionContinuity();
            clips.Add(clip);
        }
        r.Clips = clips.ToArray(); r.ClipNames = names.Take(clips.Count).ToArray();
        return r;
    }

    /// <summary>A curve through the keys with linear tangents: the frame-to-frame value the sampler gave, nothing between.</summary>
    static AnimationCurve Linear(List<Keyframe> keys)
    {
        var arr = keys.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            float inT = i > 0 ? (arr[i].value - arr[i - 1].value) / Mathf.Max(1e-6f, arr[i].time - arr[i - 1].time) : 0f;
            float outT = i + 1 < arr.Length ? (arr[i + 1].value - arr[i].value) / Mathf.Max(1e-6f, arr[i + 1].time - arr[i].time) : 0f;
            arr[i] = new Keyframe(arr[i].time, arr[i].value, inT, outT);
        }
        return new AnimationCurve(arr);
    }

    static (Mesh mesh, Material[] mats) BuildMesh(HafModel m, int meshIndex, int skinIndex, Shader sh, Dictionary<int, Texture2D> textures, Dictionary<int, Material> materials, List<UnityEngine.Object> assets)
    {
        var hm = m.Meshes[meshIndex];
        var mesh = new Mesh { name = hm.Name.Length > 0 ? hm.Name : "mesh " + meshIndex, hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        assets.Add(mesh);
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
        var subs = new List<int[]>(); var mats = new List<Material>();
        var perVertex = new List<byte>(); var influences = new List<BoneWeight1>();   // 1..8 per vertex, descending weight
        bool allNormals = true; int maxInfluences = 1;
        foreach (var p in hm.Primitives)
        {
            if (p.TriangleCount == 0) continue;
            int baseIndex = verts.Count;
            var uvSet = p.Material >= 0 && p.Material < m.Materials.Count && m.Materials[p.Material].BaseColorTexCoord == 1 && p.Uv1 != null ? p.Uv1 : p.Uv0;
            for (int v = 0; v < p.VertexCount; v++)
            {
                verts.Add(new Vector3(-p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2]));
                if (p.Normals != null) norms.Add(new Vector3(-p.Normals[v * 3], p.Normals[v * 3 + 1], p.Normals[v * 3 + 2])); else allNormals = false;
                uvs.Add(uvSet != null ? new Vector2(uvSet[v * 2], 1f - uvSet[v * 2 + 1]) : Vector2.zero);
                if (skinIndex >= 0)
                {
                    var inf = new List<BoneWeight1>();
                    if (p.Skinned)
                        for (int set = 0; set < 2; set++)
                        {
                            var joints = set == 0 ? p.Joints : p.Joints1; var weights = set == 0 ? p.Weights : p.Weights1;
                            if (joints == null || weights == null) continue;
                            for (int k = 0; k < 4; k++) if (weights[v * 4 + k] > 0f) inf.Add(new BoneWeight1 { boneIndex = joints[v * 4 + k], weight = weights[v * 4 + k] });
                        }
                    if (inf.Count == 0) inf.Add(new BoneWeight1 { boneIndex = p.Skinned ? p.Joints[v * 4] : 0, weight = 1f });   // the importer's rule: a zero-sum vertex rides its first influence
                    float sum = inf.Sum(x => x.weight);
                    inf = inf.Select(x => new BoneWeight1 { boneIndex = x.boneIndex, weight = x.weight / sum }).OrderByDescending(x => x.weight).ToList();
                    perVertex.Add((byte)inf.Count); influences.AddRange(inf); maxInfluences = Math.Max(maxInfluences, inf.Count);
                }
            }
            var tri = new List<int>((int)p.TriangleCount * 3);
            foreach (var (a, b, c) in p.Triangles()) { tri.Add(baseIndex + a); tri.Add(baseIndex + c); tri.Add(baseIndex + b); }   // rewound for the mirror
            subs.Add(tri.ToArray());
            mats.Add(ModelPreview.MaterialFor(m, p.Material, true, sh, textures, materials, assets));
        }
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = subs.Count;
        for (int si = 0; si < subs.Count; si++) mesh.SetTriangles(subs[si], si, false);
        if (allNormals && norms.Count == verts.Count) mesh.SetNormals(norms); else mesh.RecalculateNormals();
        if (skinIndex >= 0)
        {
            var sk = m.Skins[skinIndex];
            var bind = new Matrix4x4[sk.Joints.Length];
            for (int j = 0; j < sk.Joints.Length; j++)
            {
                var ibm = sk.InverseBindMatrices != null ? HafUnityFrame.Matrix(sk.InverseBindMatrices.Skip(j * 16).Take(16).ToArray()) : HafTransforms.Identity;
                var mat = Matrix4x4.identity;
                for (int c = 0; c < 4; c++) for (int w = 0; w < 4; w++) mat[w, c] = (float)ibm[c * 4 + w];
                bind[j] = mat;
            }
            mesh.bindposes = bind;
            using (var counts = new NativeArray<byte>(perVertex.ToArray(), Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(influences.ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(counts, weights);
        }
        mesh.RecalculateBounds();
        return (mesh, mats.ToArray());
    }

    static Vector3 V3(double[] v) => new Vector3((float)v[0], (float)v[1], (float)v[2]);
    static Quaternion Q(double[] q) => new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
}
