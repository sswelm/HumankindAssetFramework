// BlenderVehicleRig.Tread.cs - vehicle_rig.py's TREAD rig in C# (part d6 of the vehicle_rig port, 2026-11-16).
// This file: the skinning of one live tread part (vehicle_rig.py 2333-2980, the `if o.name in _track_by_name` branch):
//   - the link pitch: the bottom run's vertex x's against every period 0.04 .. 0.5 in steps of 0.002 (a circular
//     autocorrelation in Python doubles: math.cos / math.sin are the C library's, sum() is compensated - PySum);
//   - the subdivision: up to three passes of bmesh.ops.subdivide_edges over the edges longer than the target
//     (BlenderSubdivide), after which the mesh holds polygons (MeshData.PolyStart);
//   - the wrap bands around the sprocket, the idler and the first and last road wheel, each measured on the tread's
//     own vertices; the weight of every vertex on the wrap bones and its shuttle region; one smoothing pass over the
//     edges; the vertex groups (one per carrier bone, by name), written in the order the script's dicts hold them.
// What is NOT here yet, left to Blender by name: a tread whose link pitch was found (the link cells, the hybrid and
// the rigid-links rigs), and the script's own crash on a tread with no sprocket or idler cluster.
using System;
using System.Collections.Generic;
using System.Linq;

public static partial class BlenderVehicleRig
{
    /// <summary>One live tread: track_infos' tuple, and what the skinning leaves for the keys.</summary>
    public sealed class TrackInfo
    {
        public string Part;
        public string[] Bones;                      // Bot, Top, RampF, RampR, RampRT, WrapF, WrapR, WrapGF, WrapGR
        public int Front = -1, Rear = -1, RoadF = -1, RoadR = -1;   // cluster indices, -1 for None
        public float[] Centre;                      // the part's world box centre at bone time
        public double Pitch, Fund;                  // _link_pitch, _link_fund
        public float[] FDir = { -1f, 0f, 0f }, RDir = { -1f, 0f, 0f }, RtDir = { 1f, 0f, 0f };   // _tread_dirs
        public Dictionary<string, double> BandRot = new Dictionary<string, double>(StringComparer.Ordinal);   // _band_rot by wrap bone
        public bool Skinned;                        // the skinning ran (a part the scene no longer holds never does)
    }

    /// <summary>A Python dict of group -> weight: insertion order kept, a repeated key overwrites in place.</summary>
    sealed class WeightDict
    {
        public readonly List<string> Keys = new List<string>(4); public readonly List<double> Values = new List<double>(4);
        public void Set(string k, double v) { int i = Keys.IndexOf(k); if (i < 0) { Keys.Add(k); Values.Add(v); } else Values[i] = v; }
        public double Get(string k) { int i = Keys.IndexOf(k); return i < 0 ? 0.0 : Values[i]; }
    }

    static double PyMax2(double a, double b) => b > a ? b : a;   // max(a, b): the first unless the second is greater
    static double PyMin2(double a, double b) => b < a ? b : a;

    /// <summary>The skinning of one tread part. Returns null, or why the part is left to Blender. The part's mesh is replaced
    /// by the subdivided one, its groups and weights set; t receives the pitch, the flow directions and the band radii.</summary>
    static string TreadSkin(BlenderRigAnim.Stage1 s, BlenderRigAnim.SceneObject o, TrackInfo t, IList<(double m, float[] c)> clusters, Args a)
    {
        var d0 = o.Mesh; int nv0 = d0.Vertices;
        string botb = t.Bones[0], topb = t.Bones[1], rampfb = t.Bones[2], ramprb = t.Bones[3], ramprtb = t.Bones[4];
        string wrapfb = t.Bones[5], wraprb = t.Bones[6], wrapgfb = t.Bones[7], wrapgrb = t.Bones[8];
        bool hasF = t.Front >= 0, hasR = t.Rear >= 0;
        float[] tc = t.Centre;

        // ---- the link pitch, before the subdivision
        var zs = new double[nv0]; var xsAll = new double[nv0];
        for (int v = 0; v < nv0; v++) { xsAll[v] = d0.Positions[3 * v]; zs[v] = d0.Positions[3 * v + 2]; }
        double zlo = PyMinOf(zs), zhi = PyMaxOf(zs), xlo = PyMinOf(xsAll), xhi = PyMaxOf(xsAll), xspan = xhi - xlo;
        var xs = new List<double>();
        for (int v = 0; v < nv0; v++)
            if (zs[v] < zlo + 0.12 * (zhi - zlo) && xlo + 0.25 * xspan < xsAll[v] && xsAll[v] < xhi - 0.25 * xspan) xs.Add(xsAll[v]);
        double pitch = 0.0, fund = 0.0, best = 0.0;
        if (xs.Count >= 24)
        {
            var scores = new List<(double pc, double R)>();
            double pc = 0.04;
            while (pc <= 0.5)
            {
                double p = pc;
                double sr = PySum(xs.Select(x => BlenderTrig.Cos(2 * Math.PI * x / p)));
                double si = PySum(xs.Select(x => BlenderTrig.Sin(2 * Math.PI * x / p)));
                double R = Math.Sqrt(sr * sr + si * si) / xs.Count;
                scores.Add((pc, R));
                best = PyMax2(best, R);
                pc += 0.002;
            }
            if (best > 0.3)
            {
                bool any = false;
                foreach (var (p, R) in scores) if (R >= 0.95 * best) { if (!any || p < pitch) pitch = p; any = true; }
                for (int i = scores.Count - 1; i >= 0; i--) if (scores[i].R >= 0.85 * best) { fund = scores[i].pc; break; }
            }
        }
        t.Pitch = pitch; t.Fund = fund;
        s.Logs3.Add("VEHICLE tread '" + o.Name + "' link pitch: " + Py(pitch, 3) + ", physical link " + Py(fund, 3) + " (from " + xs.Count + " bottom-run verts)");

        // ---- the subdivision
        var wrapRs = new List<double>();
        foreach (int k in new[] { t.Front, t.Rear }) if (k >= 0 && clusters[k].m > 1e-6) wrapRs.Add(clusters[k].m * 0.5);
        double thr = wrapRs.Count > 0 ? PyMax2(0.06, 0.35 * PyMinOf(wrapRs)) : 0.15;
        var layers = new BlenderWeld.Layers();
        var bm = FromMeshData(d0, layers, faceNormals: false, vertexNormals: false);
        for (int pass = 0; pass < 3; pass++)
        {
            var lng = new List<int>();
            for (int e = 0; e < bm.EdgeCount; e++)
            {
                if (!bm.EAlive[e]) continue;
                int v1 = bm.EV1[e], v2 = bm.EV2[e];
                var dv = new[] { (float)(bm.VCo[3 * v1] - bm.VCo[3 * v2]), (float)(bm.VCo[3 * v1 + 1] - bm.VCo[3 * v2 + 1]), (float)(bm.VCo[3 * v1 + 2] - bm.VCo[3 * v2 + 2]) };
                if (VecLength(dv) > thr) lng.Add(e);
            }
            if (lng.Count == 0) break;
            BlenderSubdivide.SubdivideEdges(bm, layers, lng);
        }
        var d = ToMeshDataPolygons(bm, layers, d0);
        int nv = d.Vertices;
        s.Logs3.Add("VEHICLE tread '" + o.Name + "' subdivided: " + nv0 + " -> " + nv + " verts (edge target " + Py(thr, 3) + ")");
        Note(s, "a tread subdivided for its wrap arcs");
        if (d.PolyStart != null) Note(s, "a tread mesh of polygons");

        // ---- the regions
        bool sidePos = tc[1] >= 0;
        var sideCls = Enumerable.Range(0, clusters.Count).Where(k => (clusters[k].c[1] >= 0) == sidePos).ToList();
        if (sideCls.Count == 0) sideCls = Enumerable.Range(0, clusters.Count).ToList();
        string sprb = hasF ? wrapfb : botb, idlb = hasR ? wraprb : botb;
        int roadF = t.RoadF, roadR = t.RoadR;
        float[] RimDir(int ca, int cb, double asign, double bsign)
        {
            double az = clusters[ca].c[2] + asign * clusters[ca].m * 0.5, bz = clusters[cb].c[2] + bsign * clusters[cb].m * 0.5;
            var v = new[] { (float)((double)clusters[cb].c[0] - (double)clusters[ca].c[0]), 0f, (float)(bz - az) };
            return VecLength(v) > 1e-6 ? Normalized(v) : new[] { -1f, 0f, 0f };
        }
        t.FDir = hasF && roadF >= 0 && roadF != t.Front ? RimDir(t.Front, roadF, -1, -1) : new[] { -1f, 0f, 0f };
        t.RDir = hasR && roadR >= 0 && roadR != t.Rear ? RimDir(roadR, t.Rear, -1, -1) : new[] { -1f, 0f, 0f };
        var highCls = sideCls.Where(k => clusters[k].c[2] > tc[2] && k != t.Front && k != t.Rear).ToList();
        int rollR = -1; foreach (int k in highCls) if (rollR < 0 || clusters[k].c[0] < clusters[rollR].c[0]) rollR = k;
        t.RtDir = hasR && rollR >= 0 ? RimDir(t.Rear, rollR, 1, 1) : new[] { 1f, 0f, 0f };
        var groupNames = new SortedSet<string>(new[] { botb, topb, rampfb, ramprb, ramprtb, sprb, idlb, wrapgfb, wrapgrb }, StringComparer.Ordinal).ToList();
        var far = new[] { 1e9f, 1e9f, 1e9f };
        float[] sprC = hasF ? clusters[t.Front].c : far; double sprR = hasF ? clusters[t.Front].m * 0.5 : 0.0;
        float[] idlC = hasR ? clusters[t.Rear].c : far; double idlR = hasR ? clusters[t.Rear].m * 0.5 : 0.0;
        // _wrap_band returns TWO values for a radius of zero and the script unpacks three: its own ValueError
        if (sprR <= 0.0 || idlR <= 0.0) return "the tread without a sprocket or an idler cluster (d6: the script's own ValueError) is not modelled yet";

        float[] P(int v) => new[] { d.Positions[3 * v], d.Positions[3 * v + 1], d.Positions[3 * v + 2] };
        double Dist(float[] p, float[] c) => VecLength(Sub(p, c));
        (double full, double fade, double rot) WrapBand(float[] wc, double r0, double sgn)
        {
            var ds = new List<double>();
            for (int v = 0; v < nv; v++)
            {
                var p = P(v);
                if (sgn * ((double)p[0] - wc[0]) < 0.3 * r0) continue;
                if (p[2] < wc[2] - r0 * 2.2 || p[2] > wc[2] + r0 * 2.2) continue;
                double dd = Dist(p, wc);
                if (dd < 2.4 * r0) ds.Add(dd);
            }
            if (ds.Count < 8) return (r0 * 1.15, r0 * 1.4, r0 * 1.1);
            ds.Sort();
            double lo = ds[(int)(0.05 * (ds.Count - 1))], hi = ds[(int)(0.95 * (ds.Count - 1))];
            return (hi * 1.02, hi * 1.02 + 0.4 * r0, 0.5 * (lo + hi));
        }
        var (fullF, fadeF, rotF) = WrapBand(sprC, sprR, 1.0);
        var (fullR, fadeR, rotR) = WrapBand(idlC, idlR, -1.0);
        (float[] c, double r, double full, double fade, double rot) UnderBand(int cl)
        {
            if (cl < 0 || cl == t.Front || cl == t.Rear) return (far, 0.0, 0.0, 0.0, 0.0);
            var c = clusters[cl].c; double r = clusters[cl].m * 0.5;
            var ds = new List<double>();
            for (int v = 0; v < nv; v++)
            {
                var p = P(v);
                if (Math.Abs((double)p[0] - c[0]) < 0.6 * r && p[2] < c[2])
                {
                    double dd = Dist(p, c);
                    if (dd < 2.4 * r) ds.Add(dd);
                }
            }
            if (ds.Count < 8) return (c, r, r * 1.15, r * 1.5, r * 1.1);
            ds.Sort();
            double lo = ds[(int)(0.05 * (ds.Count - 1))], hi = ds[(int)(0.95 * (ds.Count - 1))];
            return (c, r, hi * 1.02, hi * 1.02 + 0.7 * r, 0.5 * (lo + hi));
        }
        var (rfC, rfR, fullGf, fadeGf, rotGf) = UnderBand(roadF);
        var (rrC, rrR, fullGr, fadeGr, rotGr) = UnderBand(roadR);
        double advEst = 0.04 <= pitch && pitch <= 0.3 ? pitch : Math.PI * PyMaxOf(clusters.Select(c => c.m)) * (Math.Abs(a.Degrees) / 3.0) / 360.0;
        t.BandRot[wrapfb] = rotF; t.BandRot[wraprb] = rotR; t.BandRot[wrapgfb] = rotGf; t.BandRot[wrapgrb] = rotGr;
        s.Logs3.Add("VEHICLE tread '" + o.Name + "' wrap bands: sprocket " + Py(fullF, 2) + "/" + Py(fadeF, 2) + " (r=" + Py(sprR, 2) + "), idler " + Py(fullR, 2) + "/" + Py(fadeR, 2) + " (r=" + Py(idlR, 2)
                    + "), roadF " + Py(fullGf, 2) + "/" + Py(fadeGf, 2) + " (r=" + Py(rfR, 2) + "), roadR " + Py(fullGr, 2) + "/" + Py(fadeGr, 2) + " (r=" + Py(rrR, 2) + ")");

        string ShuttleRegion(float[] p)
        {
            if (roadF >= 0 && p[0] > clusters[roadF].c[0] && p[2] > clusters[roadF].c[2] && (!hasF || p[2] < clusters[t.Front].c[2])) return rampfb;
            if (roadR >= 0 && p[0] < clusters[roadR].c[0] && hasR && p[2] > clusters[t.Rear].c[2]) return ramprtb;
            if (roadR >= 0 && p[0] < clusters[roadR].c[0] && p[2] > clusters[roadR].c[2]) return ramprb;
            return p[2] > tc[2] ? topb : botb;
        }

        // ---- the weight of every vertex: the wrap bones that hold it, the rest on its shuttle region
        var wmap = new WeightDict[nv];
        var wraps = new (float[] wc, double r0, string wb, double sd, double full, double fade, bool road)[]
        {
            (sprC, sprR, sprb, 1.0, fullF, fadeF, false), (idlC, idlR, idlb, -1.0, fullR, fadeR, false),
            (rfC, rfR, wrapgfb, 1.0, fullGf, fadeGf, true), (rrC, rrR, wrapgrb, -1.0, fullGr, fadeGr, true),
        };
        for (int v = 0; v < nv; v++)
        {
            var p = P(v); double px = p[0], pz = p[2];
            var caps = new List<(string b, double w)>();
            foreach (var (wc, r0, wb, sd, full, fade, road) in wraps)
            {
                double dist0 = Dist(p, wc);
                if (r0 <= 0.0 || full <= 0.0) continue;
                double hz = (pz - (wc[2] - fade)) / (0.2 * r0);
                if (hz <= 0.0) continue;
                double fa = PyMin2(1.0, hz);
                if (road)
                {
                    if (pz > wc[2] + 0.4 * r0) continue;
                    if (pz > wc[2]) fa *= 1.0 - (pz - wc[2]) / (0.4 * r0);
                    double sx = sd * (px - wc[0]);
                    if (sx <= 0.0) continue;
                    if (sx < advEst) fa *= sx / advEst;
                    if (fa <= 0.0) continue;
                }
                else if (pz > wc[2])
                {
                    double mrg = sd > 0 ? 0.35 * r0 : PyMax2(-0.4 * r0, 0.35 * r0 - advEst);
                    double ex = -(sd * (px - wc[0])) - mrg;
                    if (ex > 0.0)
                    {
                        fa *= 1.0 - ex / (0.5 * r0);
                        if (fa <= 0.0) continue;
                    }
                }
                double w; bool has = false;
                if (dist0 <= full) { w = fa; has = true; }
                else if (dist0 <= fade) { double tt = (dist0 - full) / PyMax2(1e-6, fade - full); w = (1.0 - tt * tt) * fa; has = true; }
                else w = 0.0;
                if (has && w > 0.001) caps.Add((wb, w));
            }
            var pairs = new List<(string b, double w)>();
            if (caps.Count == 0) pairs.Add((ShuttleRegion(p), 1.0));
            else
            {
                double tot = PySum(caps.Select(c => c.w));
                if (tot >= 0.999) foreach (var (b, w) in caps) pairs.Add((b, w / tot));
                else { pairs.AddRange(caps); pairs.Add((ShuttleRegion(p), 1.0 - tot)); }
            }
            var wd = new WeightDict();
            foreach (var (b, w) in pairs) if (w > 0.001) wd.Set(b, w);
            wmap[v] = wd;
        }
        // one smoothing pass over the edges: half the vertex's own weights, half its neighbours' mean
        var nbr = new List<int>[nv]; for (int v = 0; v < nv; v++) nbr[v] = new List<int>();
        for (int e = 0; e < d.Edges.Length / 2; e++) { int ea = d.Edges[2 * e], eb = d.Edges[2 * e + 1]; nbr[ea].Add(eb); nbr[eb].Add(ea); }
        var smoothed = new WeightDict[nv];
        for (int v = 0; v < nv; v++)
        {
            if (nbr[v].Count == 0) { smoothed[v] = wmap[v]; continue; }
            var acc = new WeightDict();
            for (int i = 0; i < wmap[v].Keys.Count; i++) acc.Set(wmap[v].Keys[i], acc.Get(wmap[v].Keys[i]) + 0.5 * wmap[v].Values[i]);
            double sh = 0.5 / nbr[v].Count;
            foreach (int nb in nbr[v])
                for (int i = 0; i < wmap[nb].Keys.Count; i++) acc.Set(wmap[nb].Keys[i], acc.Get(wmap[nb].Keys[i]) + sh * wmap[nb].Values[i]);
            double tt = PySum(acc.Values); if (tt == 0.0) tt = 1.0;   // `sum(...) or 1.0`
            var nw = new WeightDict();
            for (int i = 0; i < acc.Keys.Count; i++) if (acc.Values[i] / tt > 0.005) nw.Set(acc.Keys[i], acc.Values[i] / tt);
            smoothed[v] = nw;
        }
        wmap = smoothed;

        if (fund > 0.03) return "the tread's link-cell rig (d6: a link pitch was found) is not modelled yet";

        // ---- the groups: one per carrier bone by name; a vertex's weights in its dict's order
        d.Groups = groupNames; var groupIx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int g = 0; g < groupNames.Count; g++) groupIx[groupNames[g]] = g;
        d.DefNr = new List<int>[nv]; d.DefWeight = new List<float>[nv];
        var stats = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int v = 0; v < nv; v++)
        {
            d.DefNr[v] = new List<int>(); d.DefWeight[v] = new List<float>();
            for (int i = 0; i < wmap[v].Keys.Count; i++)
            {
                string g = wmap[v].Keys[i];
                d.DefNr[v].Add(groupIx[g]); d.DefWeight[v].Add((float)wmap[v].Values[i]);
                stats[g] = (stats.TryGetValue(g, out int n) ? n : 0) + 1;
            }
        }
        s.Logs3.Add("VEHICLE tread '" + o.Name + "' skinned (carrier blend fallback): " + string.Join(", ", stats.Select(kv => kv.Key + "=" + kv.Value)));
        Note(s, "a tread skinned on its carrier bones (no link pitch)");
        o.Mesh = d; t.Skinned = true;
        return null;
    }
}
