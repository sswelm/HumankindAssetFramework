// vehicle_rig.py's finishing and GLB export, stage 5. BMesh ordering follows Blender 5.1.2:
// bmo_dupe.cc, bmesh_core.cc (loop_reverse), bmesh_mesh_normals.cc. The existing GLB exporter
// supplies structure, materials, sampling and serialization with vehicle-specific preparation.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class BlenderVehicleRig
{
    static bool OarMesh(BlenderRigAnim.SceneObject o) => o.Mesh.Groups.Any(g => g.StartsWith("Oar_", StringComparison.Ordinal));
    static bool CanvasMesh(BlenderRigAnim.SceneObject o) => o.Mesh.Groups.Any(g => g == "Sail" || g == "Flag");

    // BM_mesh_normals_update: disk edges then radial loops, angle-weighted face normals; loose/zero
    // normals fall back to the vertex coordinate. This differs from Mesh's vertex-normal accumulation.
    static void BmNormals(BMesh bm)
    {
        BlenderDecimate.FaceNormalsUpdate(bm);
        for (int v = 0; v < bm.VertCount; v++)
        {
            if (!bm.VAlive[v]) continue;
            var no = new float[3]; int first = bm.VE[v];
            if (first >= 0)
            {
                int e = first;
                do
                {
                    int l0 = bm.EL[e];
                    if (l0 >= 0)
                    {
                        var d2 = BmUnit(Sub(BmCo(bm, bm.EV1[e]), BmCo(bm, bm.EV2[e])));
                        int l = l0;
                        do
                        {
                            if (bm.LV[l] == v)
                            {
                                int prev = bm.LPrev[l], ep = bm.LE[prev];
                                var d1 = BmUnit(Sub(BmCo(bm, bm.EV1[ep]), BmCo(bm, bm.EV2[ep])));
                                float dot = (float)((float)((float)(d1[0] * d2[0]) + (float)(d1[1] * d2[1])) + (float)(d1[2] * d2[2]));
                                if ((bm.EV1[ep] == bm.LV[prev]) ^ (bm.EV1[e] == bm.LV[l])) dot = -dot;
                                float fac = VehicleProbe.SafeAcosApprox(-dot);
                                for (int k = 0; k < 3; k++) no[k] = (float)(no[k] + (float)(bm.FNo[3 * bm.LF[l] + k] * fac));
                            }
                            l = bm.LRadialNext[l];
                        } while (l != l0);
                    }
                    e = bm.EDiskNext[2 * e + (bm.EV1[e] == v ? 0 : 1)];
                } while (e != first);
            }
            no = BmUnit(no); if (no.All(x => x == 0f)) no = BmUnit(BmCo(bm, v));
            Array.Copy(no, 0, bm.VNo, 3 * v, 3);
        }
    }
    static float[] BmCo(BMesh bm, int v) => new[] { bm.VCo[3 * v], bm.VCo[3 * v + 1], bm.VCo[3 * v + 2] };
    static float[] BmUnit(float[] v)
    {
        float d = (float)((float)((float)(v[0] * v[0]) + (float)(v[1] * v[1])) + (float)(v[2] * v[2]));
        return d > 1e-35f ? Scale(v, (float)(1f / (float)Math.Sqrt(d))) : new float[3];
    }

    static void ReverseBmFace(BMesh bm, int f)
    {
        int first = bm.FLFirst[f], prevEdge = bm.LE[bm.LPrev[first]], pn = bm.LRadialNext[bm.LPrev[first]], pp = bm.LRadialPrev[bm.LPrev[first]];
        bool boundary = pn == bm.LRadialNext[pn]; int l = first;
        do
        {
            int e = bm.LE[l], rn = bm.LRadialNext[l], rp = bm.LRadialPrev[l]; bool nextBoundary = rn == bm.LRadialNext[rn];
            if (boundary) { bm.LRadialNext[l] = l; bm.LRadialPrev[l] = l; }
            else { bm.LRadialNext[l] = pn; bm.LRadialPrev[l] = pp; bm.LRadialPrev[pn] = l; bm.LRadialNext[pp] = l; }
            if (bm.EL[e] == l) bm.EL[e] = bm.LNext[l];
            bm.LE[l] = prevEdge; (bm.LNext[l], bm.LPrev[l]) = (bm.LPrev[l], bm.LNext[l]);
            prevEdge = e; pn = rn; pp = rp; boundary = nextBoundary;
            l = bm.LPrev[l];
        } while (l != first);
        for (int k = 0; k < 3; k++) bm.FNo[3 * f + k] = -bm.FNo[3 * f + k];
    }

    // A BMesh round trip keeps deform weights and raw custom-normal shorts as custom data. The stage-2
    // converter predates skinning; restore those layers here, including duplicates in discovery order.
    static BlenderRigAnim.MeshData FinishBm(BMesh bm, BlenderWeld.Layers layers, BlenderRigAnim.MeshData old, List<int> vertexSource)
    {
        var d = ToMeshData(bm, layers, old); d.Groups = new List<string>(old.Groups); d.ArmatureModifier = old.ArmatureModifier;
        if (old.DefNr != null)
        {
            d.DefNr = vertexSource.Select(v => new List<int>(old.DefNr[v])).ToArray();
            d.DefWeight = vertexSource.Select(v => new List<float>(old.DefWeight[v])).ToArray();
        }
        return d;
    }

    static BlenderRigAnim.MeshData FinishMesh(BlenderRigAnim.MeshData d, bool insideOut, bool flip, double? inset, out int reversed, out int kept)
    {
        reversed = kept = 0;
        var layers = new BlenderWeld.Layers(); var bm = FromMeshData(d, layers, true, false);
        var vs = Enumerable.Range(0, d.Vertices).ToList(); // Python bmesh.from_mesh keeps raw custom-normal shorts.
        if (inset.HasValue) BmNormals(bm);
        if (insideOut)
        {
            var ys = Enumerable.Range(0, d.Vertices).Select(v => d.Positions[3 * v + 1]).ToArray();
            double cy = .5 * ((double)ys.Min() + ys.Max()); int step = Math.Max(1, d.Vertices / 5000);
            var zs = Enumerable.Range(0, d.Vertices).Where(v => v % step == 0).Select(v => d.Positions[3 * v + 2]).OrderBy(z => z).ToArray(); double cz = zs[zs.Length / 4];
            var seen = new HashSet<int>();
            for (int f = 0; f < bm.FaceCount; f++)
            {
                if (!seen.Add(f)) continue;
                var stack = new Stack<int>(); stack.Push(f); var island = new List<int>(); double sum = 0; int n = 0;
                while (stack.Count > 0)
                {
                    int face = stack.Pop(); island.Add(face); int first = bm.FLFirst[face], l = first; var center = new float[3];
                    do { for (int k = 0; k < 3; k++) center[k] = (float)(center[k] + bm.VCo[3 * bm.LV[l] + k]); l = bm.LNext[l]; } while (l != first);
                    center = Scale(center, 1f / 3f); var rad = new[] { 0f, (float)(center[1] - cy), (float)(center[2] - cz) };
                    if (VecLength(rad) > 1e-6) { var rn = Normalized(rad); sum += Dot(new[] { bm.FNo[3 * face], bm.FNo[3 * face + 1], bm.FNo[3 * face + 2] }, rn); n++; }
                    do
                    {
                        int r0 = bm.EL[bm.LE[l]], r = r0;
                        do { int other = bm.LF[r]; if (seen.Add(other)) stack.Push(other); r = bm.LRadialNext[r]; } while (r != r0);
                        l = bm.LNext[l];
                    } while (l != first);
                }
                if (n > 0 && sum / n < -.25) { foreach (int face in island) ReverseBmFace(bm, face); reversed++; } else kept++;
            }
        }
        if (flip) { for (int f = 0; f < bm.FaceCount; f++) ReverseBmFace(bm, f); }
        if (inset.HasValue)
        {
            for (int i = 0; i < layers.PointColor.Count; i++) { var a = layers.PointColor[i]; Array.Resize(ref a, 8 * d.Vertices); layers.PointColor[i] = a; }
            int nf = bm.FaceCount; var vm = new Dictionary<int, int>(); var em = new Dictionary<int, int>(); var copies = new List<int>();
            int CopyV(int v)
            {
                if (vm.TryGetValue(v, out int dst)) return dst;
                dst = bm.VertCreate(bm.VCo[3 * v], bm.VCo[3 * v + 1], bm.VCo[3 * v + 2]); vm[v] = dst; vs.Add(v);
                for (int i = 0; i < layers.PointColor.Count; i++) { var a = layers.PointColor[i]; Array.Copy(a, 4 * v, a, 4 * dst, 4); }
                return dst;
            }
            for (int f = 0; f < nf; f++)
            {
                int l0 = bm.FLFirst[f], l = l0; var loops = new List<int>();
                do { loops.Add(l); CopyV(bm.LV[l]); l = bm.LNext[l]; } while (l != l0);
                foreach (int src in loops)
                {
                    int e = bm.LE[src]; if (!em.ContainsKey(e)) em[e] = bm.EdgeCreate(vm[bm.EV1[e]], vm[bm.EV2[e]], e);
                }
                var newLoops = new int[3]; int copy = bm.FaceCreate(loops.Select(x => vm[bm.LV[x]]).ToArray(), loops.Select(x => em[bm.LE[x]]).ToArray(), 3, f, newLoops); copies.Add(copy);
                for (int k = 0; k < 3; k++) { layers.CopyLoop(loops[k], newLoops[k]); }
            }
            BmNormals(bm); // duplicate's NORMALS_CALC finish precedes the displacement
            float offset = (float)inset.Value;
            foreach (int v in vm.Values) for (int k = 0; k < 3; k++) bm.VCo[3 * v + k] = (float)(bm.VCo[3 * v + k] - (float)(bm.VNo[3 * v + k] * offset));
            foreach (int f in copies) ReverseBmFace(bm, f);
        }
        return FinishBm(bm, layers, d, vs);
    }

    static bool PrepareExportMaterials(BlenderRigAnim.Stage1 s, Args a, out HafModel combined, out BlenderNames.Result names)
    {
        combined = new HafModel(); var mapped = new Dictionary<(int, int, bool), string>(); var order = new List<string>();
        names = new BlenderNames.Result { MaterialOf = mapped, MaterialsInOrder = order };
        var used = new HashSet<string>(s.Objects.Where(o => o.Type == "MESH").SelectMany(o => o.Mesh.Materials).Where(n => n != null), StringComparer.Ordinal);
        int scopeIndex = 0;
        foreach (var scope in a.MaterialInputs)
        {
            int materialOffset = combined.Materials.Count, textureOffset = combined.Textures.Count, imageOffset = combined.Images.Count, samplerOffset = combined.Samplers.Count;
            int Tex(int index) => index < 0 ? index : index + textureOffset;
            foreach (var kv in scope.names.MaterialOf) mapped[(kv.Key.meshNode < 0 ? -1 : kv.Key.meshNode + scopeIndex * 1000000, kv.Key.material < 0 ? -1 : kv.Key.material + materialOffset, kv.Key.vertexColor)] = kv.Value;
            order.AddRange(scope.names.MaterialsInOrder);
            foreach (var source in scope.model.Materials)
            {
                string extensions = source.ExtensionsJson;
                if (extensions != null && textureOffset != 0)
                {
                    var json = JObject.Parse(extensions);
                    foreach (var property in json.Descendants().OfType<JProperty>().Where(p => p.Name.EndsWith("Texture", StringComparison.Ordinal)))
                        if (property.Value is JObject info && info["index"] != null) info["index"] = (int)info["index"] + textureOffset;
                    extensions = json.ToString(Newtonsoft.Json.Formatting.None);
                }
                var mat = new HafMaterial
                {
                    Name = source.Name, NameAbsent = source.NameAbsent, BaseColorFactor = source.BaseColorFactor == null ? new double[] { 1, 1, 1, 1 } : (double[])source.BaseColorFactor.Clone(),
                    BaseColorTexture = Tex(source.BaseColorTexture), BaseColorTexCoord = source.BaseColorTexCoord,
                    MetallicFactor = source.MetallicFactor, RoughnessFactor = source.RoughnessFactor, MetallicRoughnessTexture = Tex(source.MetallicRoughnessTexture), MetallicRoughnessTexCoord = source.MetallicRoughnessTexCoord,
                    NormalTexture = Tex(source.NormalTexture), NormalTexCoord = source.NormalTexCoord, NormalScale = source.NormalScale,
                    OcclusionTexture = Tex(source.OcclusionTexture), OcclusionTexCoord = source.OcclusionTexCoord, OcclusionStrength = source.OcclusionStrength, OcclusionStrengthJson = source.OcclusionStrengthJson,
                    EmissiveTexture = Tex(source.EmissiveTexture), EmissiveTexCoord = source.EmissiveTexCoord, EmissiveFactor = source.EmissiveFactor, EmissiveFactorJson = source.EmissiveFactorJson,
                    AlphaMode = source.AlphaMode, AlphaCutoff = source.AlphaCutoff, DoubleSided = source.DoubleSided, ExtensionsJson = extensions, ExtrasJson = source.ExtrasJson,
                };
                int index = combined.Materials.Count - materialOffset;
                bool materialUsed = scope.names.MaterialOf.Any(kv => kv.Key.material == index && used.Contains(kv.Value));
                double factor = PyMin(4, PyMax(.2, scope.bright));
                if (materialUsed && Math.Abs(factor - 1) >= 1e-3)
                {
                    var bc = mat.BaseColorFactor; bool plainTexture = source.BaseColorTexture >= 0 && bc.Take(3).All(c => c == 1.0);
                    for (int c = 0; c < 3; c++)
                    {
                        double old = (float)bc[c], next = Math.Min(1.0, old * factor);
                        double residual = plainTexture ? factor : next <= 0 ? 1 : old * factor / next;
                        if (source.BaseColorTexture >= 0 && Math.Abs(residual - 1) >= 1e-3) { s.Fallback = "stage 5: the export of brightness-edited image bytes is not modelled yet"; return false; }
                        bc[c] = (float)next;
                    }
                }
                combined.Materials.Add(mat);
            }
            foreach (var texture in scope.model.Textures) combined.Textures.Add(new HafTexture { Name = texture.Name, Source = texture.Source < 0 ? texture.Source : texture.Source + imageOffset, Sampler = texture.Sampler < 0 ? texture.Sampler : texture.Sampler + samplerOffset });
            for (int i = 0; i < scope.model.Images.Count; i++)
            {
                var image = scope.model.Images[i];
                string name = image.Name;
                if (string.IsNullOrEmpty(name) && (string.IsNullOrEmpty(image.Uri) || image.Uri.StartsWith("data:", StringComparison.Ordinal))) name = "Image_" + i;
                combined.Images.Add(new HafImage { Name = name, Uri = image.Uri, MimeType = image.MimeType, Bytes = image.Bytes });
            }
            combined.Samplers.AddRange(scope.model.Samplers);
            foreach (string extension in scope.model.ExtensionsUsed) if (!combined.ExtensionsUsed.Contains(extension)) combined.ExtensionsUsed.Add(extension);
            scopeIndex++;
        }
        return true;
    }

    // Generate can decline known material limits before reduction/rigging. Use only slots referenced by faces
    // after stage 1 (ignored parts are already gone); unused and vertex-only slots cannot force a fallback.
    // This builds the same material context as stage 5 without replacing the import scene's model or name maps.
    internal static string ExportMaterialFallback(BlenderRigAnim.Stage1 s, Args a, bool preserveJpegAlpha = false)
    {
        if (!PrepareExportMaterials(s, a, out var model, out var names)) return s.Fallback;
        var used = new List<string>(); var uvSets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in s.Objects.Where(o => o.Type == "MESH"))
        {
            var mesh = o.Mesh;
            foreach (int slot in mesh.FaceMaterial.Distinct())
            {
                string name = slot >= 0 && slot < mesh.Materials.Count ? mesh.Materials[slot] : null;
                if (name == null) continue;
                if (!uvSets.ContainsKey(name)) used.Add(name);
                uvSets[name] = Math.Max(uvSets.TryGetValue(name, out int count) ? count : 0, mesh.UvAll.Count);
            }
        }
        string why = BlenderMaterialExport.Build(model, names, used, uvSets, preserveJpegAlpha).Fallback;
        return why == null ? null : "stage 5: the GLB export (" + why + ") is not modelled yet";
    }

    public static BlenderDeployExport.Result RunStage5(BlenderRigAnim.Stage1 s, Args a, bool preserveJpegAlpha = false)
    {
        if (s.Fallback != null || s.Exits || s.Dies != null) return null;
        if (!PrepareExportMaterials(s, a, out var materials, out var materialNames)) return null;
        s.Model = materials; s.Names.MaterialOf = materialNames.MaterialOf; s.Names.MaterialsInOrder = materialNames.MaterialsInOrder;
        if (VehicleProbe.Input.PyStrip(a.PreviewFbx).Length > 0) { s.Fallback = "stage 5: the FBX preview export is not modelled yet"; return null; }
        var st = (RigState)s.VehicleState;
        var meshes = s.Objects.Where(o => o.Type == "MESH" && o.Mesh.Faces.Length > 0).ToList();
        int reversed = 0, kept = 0;
        if (a.FixInsideOut)
        {
            foreach (var o in meshes)
            {
                if (OarMesh(o) || CanvasMesh(o) || o.Name.StartsWith("Mesh___rud__", StringComparison.Ordinal) || o.Name.StartsWith("Mesh___keep__", StringComparison.Ordinal)) continue;
                o.Mesh = FinishMesh(o.Mesh, true, false, null, out int r, out int k); reversed += r; kept += k;
            }
            s.Logs5.Add("VEHICLE inside-out fix: " + reversed + " island(s) reversed (interior-facing), " + kept + " kept as authored; Sail/Oar meshes untouched");
        }
        int flipped = 0;
        foreach (var o in meshes.Where(o => o.Name.StartsWith("Mesh___flip__", StringComparison.Ordinal))) { o.Mesh = FinishMesh(o.Mesh, false, true, null, out _, out _); flipped++; }
        if (flipped > 0) s.Logs5.Add("VEHICLE FLIP applied: " + flipped + " part(s) winding reversed once" + (a.FixInsideOut ? " (on top of the inside-out fix — cancelling where it flipped)" : ""));
        var targets = a.DoubleSided ? meshes.Where(o => !OarMesh(o) && !o.Name.StartsWith("Mesh___keep__", StringComparison.Ordinal)).ToList()
            : meshes.Where(o => (CanvasMesh(o) || o.Name.StartsWith("Mesh___rud__", StringComparison.Ordinal)) && !o.Name.StartsWith("Mesh___rigsail__", StringComparison.Ordinal)).ToList();
        if (targets.Count > 0)
        {
            // Bound boxes come from the evaluated modifier state, not necessarily the original's final pose.
            var pose = st.EvaluatedPose;
            var corners = meshes.SelectMany(o => DeformedCorners(o, st.Arm, s.Armature, pose, st)).ToList();
            double dim = Enumerable.Range(0, 3).Max(k => (double)corners.Max(p => p[k]) - corners.Min(p => p[k])); double offset = Math.Max(1e-5, dim * .0015);
            foreach (var o in targets)
            {
                VehicleProbe.Mat4ToLocRotSize(o.World, out _, out _, out var scale);
                double avg = (Math.Abs((double)scale[0]) + Math.Abs((double)scale[1]) + Math.Abs((double)scale[2])) / 3.0;
                double local = avg > 1e-9 ? offset / avg : offset; int old = o.Mesh.Vertices;
                o.Mesh = FinishMesh(o.Mesh, false, false, local, out _, out _);
                s.Logs5.Add("VEHICLE double-sided '" + o.Name + "': " + old + " -> " + o.Mesh.Vertices + " verts (back shell inset " + BlenderRigAnim.PyFixed(local, 4) + " local)");
            }
            s.Logs5.Add("VEHICLE double-sided: " + targets.Count + " mesh(es) made two-sided at the source (whole-model inset " + BlenderRigAnim.PyFixed(offset, 4) + " world)");
        }
        var all = s.Objects.Where(o => o.Type == "MESH" && o.Mesh.Vertices > 0).ToList(); int verts = all.Sum(o => o.Mesh.Vertices), tris = all.Sum(o => o.Mesh.Faces.Length / 3);
        s.Logs5.Add("VEHICLE export totals: " + verts + " verts, " + tris + " tris across " + all.Count + " mesh(es)");
        int current = s.FrameCurrent;
        var x = BlenderRigExport.BuildVehicle(s, f => EvaluateExportFrame(s, f), preserveJpegAlpha);
        if (x.Fallback != null) { s.Fallback = "stage 5: the GLB export (" + x.Fallback + ") is not modelled yet"; return x; }
        // The exporter validates the original mesh; totals above were printed before that validation.
        foreach (var o in all)
        {
            var me = o.Mesh;
            var r = new BlenderReduce.Result { VertexCount = me.Vertices, FaceCount = me.Faces.Length / 3, Positions = me.Positions, Faces = me.Faces, Edges = me.Edges, FaceMaterial = me.FaceMaterial, FaceSharp = me.FaceSharp, CustomNormal = me.CustomNormal };
            r.Uv.AddRange(me.UvAll); r.Colors.AddRange(me.Colors);
            var v = BlenderExport.Validated(r);
            if (!ReferenceEquals(v, r)) { me.Faces = v.Faces; me.FaceMaterial = v.FaceMaterial; me.FaceSharp = v.FaceSharp; me.CustomNormal = v.CustomNormal; me.UvAll = v.Uv; me.Uv = me.Uv == null ? null : v.Uv.FirstOrDefault(); me.Colors = v.Colors; }
        }
        int Count(string role) => role == "guns" ? a.Names["guns"].Concat(a.Names["muzzles"]).Concat(a.Names["cradles"]).Distinct(StringComparer.Ordinal).Count() : a.Names[role].Count;
        string F(float v) => PyFloatRepr.Repr(v);
        var axes = new List<string>();
        for (int i = 0; i < st.ClusterBones.Count; i++) axes.Add(PyStrRepr(st.ClusterBones[i]) + ": (" + string.Join(", ", st.WheelAxes[i].Select(F)) + ")");
        int repeats = (int)Math.Max(1, PyRound((double)s.FrameEnd / (double)System.Numerics.BigInteger.Max(1, a.RockFrames)));
        s.Logs5.Add("VEHICLE RIG DONE: " + Count("wheels") + " wheel part(s) clustered into " + st.Clusters.Count + " wheel(s) {" + string.Join(", ", axes) + "}, " + Count("turrets") + " turret part(s) on one Turret bone, " + Count("guns") + " gun part(s) on one Gun bone"
            + (Count("guns") > 0 && Count("turrets") > 0 ? " (child of Turret)" : "") + ", " + (a.TracksStatic ? 0 : Count("tracks")) + " track loop(s) on own static bones, Spin 0.." + s.FrameEnd + " " + BlenderRigAnim.PyFixed(a.Degrees, 0) + " deg"
            + (a.RockOn ? ", wave rock " + BlenderRigAnim.PyFixed(a.RockDeg, 1) + " deg x" + repeats + " fitted cycle(s)" : "") + ", exported " + verts + " verts / " + tris + " tris -> " + a.OutGlb);
        var visited = s.Actions.Where(act => act.Slots.Any(sl => sl.FCurves.Any(fc => fc.Bone != null))).OrderBy(act => BlenderRigAnim.PyLower(act.Name), BlenderRigAnim.PyStringOrder).ToList();
        if (visited.Count > 1 || (visited.Count > 0 && visited[0].Name != st.Arm.Action))
            foreach (int b in s.Armature.BoneNodes) { s.Armature.PoseProps[b] = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } }; s.Armature.PoseEuler[b] = new float[3]; }
        if (visited.Count > 0 && visited.Last().Name == st.Arm.Action)
        {
            var range = BlenderRigExport.ActionRange(visited.Last());
            EvaluateExportFrame(s, range.end, range.start);
        }
        if (visited.Count > 0 && st.Arm.Slot == null) { st.Arm.Action = null; st.Arm.Slot = null; }
        EvaluateExportFrame(s, current);
        return x;
    }

    internal static void EvaluateExportFrame(BlenderRigAnim.Stage1 s, int frame, int? sampledStart = null)
    {
        var st = (RigState)s.VehicleState; var ad = s.Armature;
        var slot = s.Actions.FirstOrDefault(a => a.Name == st.Arm.Action)?.Slots.FirstOrDefault(sl => sl.Identifier == st.Arm.Slot);
        if (slot != null) foreach (var fc in slot.FCurves)
        {
            if (fc.Keys.Count == 0 || fc.Bone == null || !st.BoneId.TryGetValue(fc.Bone, out int b)) continue;
            var values = fc.Prop == "rotation_euler" ? (ad.PoseEuler.TryGetValue(b, out var e) ? e : ad.PoseEuler[b] = new float[3]) : ad.PoseProps[b][BlenderRigAnim.ChannelIndex(fc.Prop)];
            float v = BlenderRigAnim.EvaluateCurve(fc.Keys, frame); bool wroteNonzero = false;
            // Endpoint evaluation is sufficient except when unequal-only RNA write-back crosses zero.
            // Constant zero curves keep the original sign; a prior nonzero sample makes the final zero write.
            if (sampledStart.HasValue && v == 0f && values[fc.Index] == 0f && fc.Keys.Any(k => k.Value != 0f))
            {
                var keys = BlenderRigAnim.ToArm(fc.Keys);
                for (int f = sampledStart.Value; f < frame; f++) if (BlenderFCurve.Evaluate(keys, f, constant: true) != 0f) { wroteNonzero = true; break; }
            }
            if (values[fc.Index] != v || wroteNonzero) values[fc.Index] = v;
        }
        s.FrameCurrent = frame; EvaluateObjects(s, frame); ad.PoseMats = VehiclePoseMats(ad);
    }
}
