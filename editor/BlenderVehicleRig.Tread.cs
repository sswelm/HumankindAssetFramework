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
        public TreadJob Job;                        // the link job, when the tread's link pitch was found (_link_jobs)
    }

    /// <summary>_link_jobs' entry: the measured ring path and the cells cut along it.</summary>
    public sealed class TreadJob
    {
        public string Prefix;                       // the carrier names' stem: Track_00_L_
        public int NC; public double CellL, L, Off; // the cells, a cell's length, the loop's length, the cut phase
        public double[] S; public float[][] Pts;    // the path: 512 samples (x, 0, z) and their running length (513 entries)
        public List<int> Cells = new List<int>();   // the cells with a bone of their own (the wrap arcs and their margin)
        public List<List<int>> Runs = new List<List<int>>();   // the straight runs: one shuttle bone each
        public int[] CellOf; public double[] SRest; // per vertex its cell; per cell its place along the path at rest
        public double AdvCells;                     // the advance per loop, in cells
        public string[] Aux;                        // the nine carrier bones, removed for the link bones
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
    static string TreadSkin(BlenderRigAnim.Stage1 s, BlenderRigAnim.SceneObject o, TrackInfo t, IList<(double m, float[] c)> clusters, Args a, List<string> log,
                            int clusterBones, bool turrets, bool guns, int trackCount)
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
        log.Add("VEHICLE tread '" + o.Name + "' link pitch: " + Py(pitch, 3) + ", physical link " + Py(fund, 3) + " (from " + xs.Count + " bottom-run verts)");

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
        log.Add("VEHICLE tread '" + o.Name + "' subdivided: " + nv0 + " -> " + nv + " verts (edge target " + Py(thr, 3) + ")");
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
        log.Add("VEHICLE tread '" + o.Name + "' wrap bands: sprocket " + Py(fullF, 2) + "/" + Py(fadeF, 2) + " (r=" + Py(sprR, 2) + "), idler " + Py(fullR, 2) + "/" + Py(fadeR, 2) + " (r=" + Py(idlR, 2)
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

        if (fund > 0.03)
        {
            var why = TreadLinkCells(o, t, clusters, a, log, d, fund, clusterBones, turrets, guns, trackCount, highCls,
                                     hasF, hasR, sprC, sprR, idlC, idlR, rotF, rotR, rrC, rrR, rotGr, rfC, rfR, rotGf);
            if (why != null) return why;
            // the carrier groups stay empty in link mode and are dropped; each vertex goes whole to its cell's bone - the run's
            // shuttle or the cell's own link -, the groups made in the order their first vertex comes
            var job = t.Job; var runOf = new Dictionary<int, int>();
            for (int ri = 0; ri < job.Runs.Count; ri++) foreach (int ci in job.Runs[ri]) runOf[ci] = ri;
            d.Groups = new List<string>(); var linkIx = new Dictionary<string, int>(StringComparer.Ordinal);
            d.DefNr = new List<int>[nv]; d.DefWeight = new List<float>[nv];
            for (int v = 0; v < nv; v++)
            {
                int ci = job.CellOf[v];
                string bn = runOf.TryGetValue(ci, out int ri) ? job.Prefix + "S" + ri.ToString("00", System.Globalization.CultureInfo.InvariantCulture) : job.Prefix + "L" + ci.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                if (!linkIx.TryGetValue(bn, out int g)) { linkIx[bn] = g = d.Groups.Count; d.Groups.Add(bn); }
                d.DefNr[v] = new List<int> { g }; d.DefWeight[v] = new List<float> { 1f };
            }
            Note(s, "a tread cut into link cells: link bones on the wraps, shuttle bones on the runs");
            o.Mesh = d; t.Skinned = true;
            return null;
        }

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
        log.Add("VEHICLE tread '" + o.Name + "' skinned (carrier blend fallback): " + string.Join(", ", stats.Select(kv => kv.Key + "=" + kv.Value)));
        Note(s, "a tread skinned on its carrier bones (no link pitch)");
        o.Mesh = d; t.Skinned = true;
        return null;
    }
    static double PyMod(double a, double b) { double r = a % b; if (r != 0.0) { if ((b < 0) != (r < 0)) r += b; } else r = 0.0; return r; }   // Python's float %: the divisor's sign
    static int BisectRight(double[] a, double x) { int lo = 0, hi = a.Length; while (lo < hi) { int mid = (lo + hi) / 2; if (x < a[mid]) hi = mid; else lo = mid + 1; } return lo; }
    /// <summary>Vector.lerp(other, fac): interp_vn_vn(other, self, 1.0f - fac) - t = 1.0f - fac, s = 1.0f - t, each component s * other + t * self.</summary>
    static float[] VecLerp(float[] self, float[] other, double fac)
    {
        float t = (float)(1.0f - (float)fac), sw = (float)(1.0f - t); var r = new float[3];
        for (int i = 0; i < 3; i++) r[i] = (float)((float)(sw * other[i]) + (float)(t * self[i]));
        return r;
    }

    /// <summary>_path_eval(job, s): the point on the ring path at s and the path's direction there.</summary>
    internal static void PathEval(TreadJob job, double sAt, out float[] p, out float[] dir)
    {
        int n = job.Pts.Length;
        sAt = PyMod(sAt, job.L);
        int b = Math.Min(n - 1, Math.Max(0, BisectRight(job.S, sAt) - 1)), b2 = (b + 1) % n;
        double seg = job.S[b + 1] - job.S[b], f = seg > 1e-9 ? (sAt - job.S[b]) / seg : 0.0;
        p = VecLerp(job.Pts[b], job.Pts[b2], f);
        var tv = Sub(job.Pts[b2], job.Pts[b]);
        if (VecLength(tv) < 1e-9) tv = Sub(job.Pts[(b2 + 1) % n], job.Pts[b]);
        dir = Normalized(tv);
    }

    /// <summary>The link cells of a tread whose link pitch was found (vehicle_rig.py 2625-2970, the conveyor: no rigid links): the
    /// belt path round the wheels' band circles (tangent to tangent, arcs sampled every 0.02), resampled to 512 points, pulled onto
    /// the tread's own centre line (the vertices' mean per sample, smoothed over one link each side); the loop cut into cells at
    /// the phase that crosses the fewest edges; the cells on a wrap arc (and the advance's margin around it) each get a bone, the
    /// straight runs one shuttle bone. Python doubles; the path's points are float32 Vectors.</summary>
    static string TreadLinkCells(BlenderRigAnim.SceneObject o, TrackInfo t, IList<(double m, float[] c)> clusters, Args a, List<string> log, BlenderRigAnim.MeshData d, double fund,
                                 int clusterBones, bool turrets, bool guns, int trackCount, List<int> highCls, bool hasF, bool hasR,
                                 float[] sprC, double sprR, float[] idlC, double idlR, double rotF, double rotR, float[] rrC, double rrR, double rotGr, float[] rfC, double rfR, double rotGf)
    {
        int nv = d.Vertices;
        double sto = PyMax2(0.03, idlR > 0.0 && rotR > idlR ? rotR - idlR : 0.08);
        var circ = new List<(double x, double z, double r)>();
        if (hasF && rotF > 0.02) circ.Add((sprC[0], sprC[2], rotF));
        foreach (int k in highCls.Select((k, i) => (k, i)).OrderBy(q => -(double)clusters[q.k].c[0]).ThenBy(q => q.i).Select(q => q.k))   // the top rollers, front to rear (a stable sort)
            circ.Add((clusters[k].c[0], clusters[k].c[2], clusters[k].m * 0.5 + sto));
        if (hasR && rotR > 0.02) circ.Add((idlC[0], idlC[2], rotR));
        if (rrR > 0.0 && rotGr > 0.02) circ.Add((rrC[0], rrC[2], rotGr));
        if (rfR > 0.0 && rotGf > 0.02) circ.Add((rfC[0], rfC[2], rotGf));
        int ncr = circ.Count;
        if (ncr < 2) return "the tread's link-cell rig on fewer than two band circles (d6) is not modelled yet";
        var norms = new (double x, double z)[ncr];
        for (int i = 0; i < ncr; i++)
        {
            var (x1, z1, r1) = circ[i]; var (x2, z2, r2) = circ[(i + 1) % ncr];
            double dxb = x2 - x1, dzb = z2 - z1, db = PyHypot(dxb, dzb); if (db == 0.0) db = 1e-9;
            double exb = dxb / db, ezb = dzb / db;
            double cpb = PyMax2(-1.0, PyMin2(1.0, (r1 - r2) / db)), spb = Math.Sqrt(1.0 - cpb * cpb);
            norms[i] = (exb * cpb + ezb * spb, ezb * cpb - exb * spb);
        }
        var raw = new List<float[]>(); var cum = new List<double> { 0.0 }; var arcRaw = new List<(double a, double b)>();
        void RawAdd(float[] p) { if (raw.Count > 0) cum.Add(cum[cum.Count - 1] + VecLength(Sub(p, raw[raw.Count - 1]))); raw.Add(p); }
        for (int i = 0; i < ncr; i++)
        {
            var (xc, zc, rc2) = circ[i]; var na = norms[(i - 1 + ncr) % ncr]; var nd = norms[i];
            double a0b = BlenderTrig.Atan2(na.z, na.x), a1b = BlenderTrig.Atan2(nd.z, nd.x);
            while (a1b < a0b - 1e-9) a1b += 2 * Math.PI;
            double stepsD = (a1b - a0b) * rc2 / 0.02;
            if (double.IsNaN(stepsD) || stepsD > 1e7) return "the tread's link-cell rig on a band circle without a finite arc (d6) is not modelled yet";
            int steps = Math.Max(1, (int)stepsD);
            double arcS0 = 0.0; bool started = false;
            for (int k = 0; k < steps; k++)
            {
                double ab = a0b + (a1b - a0b) * k / steps;
                RawAdd(new[] { (float)(xc + rc2 * BlenderTrig.Cos(ab)), 0f, (float)(zc + rc2 * BlenderTrig.Sin(ab)) });
                if (!started) { arcS0 = cum[cum.Count - 1]; started = true; }   // after the first sample: the jump before it stays outside the arc
            }
            RawAdd(new[] { (float)(xc + rc2 * nd.x), 0f, (float)(zc + rc2 * nd.z) });
            arcRaw.Add((arcS0, cum[cum.Count - 1]));
        }
        int nr = raw.Count; var SP = new double[nr + 1];
        for (int k = 0; k < nr; k++) SP[k + 1] = SP[k] + VecLength(Sub(raw[(k + 1) % nr], raw[k]));
        double LP = SP[nr];
        const int M = 512;
        var pts = new float[M][];
        for (int m = 0; m < M; m++)
        {
            double sT = m / (double)M * LP;
            int k = Math.Min(nr - 1, Math.Max(0, BisectRight(SP, sT) - 1));
            double seg = SP[k + 1] - SP[k], fr = seg > 1e-9 ? (sT - SP[k]) / seg : 0.0;
            pts[m] = VecLerp(raw[k], raw[(k + 1) % nr], fr);
        }
        double[] RunningLength(float[][] P) { var r = new double[M + 1]; for (int i = 0; i < M; i++) r[i + 1] = r[i] + VecLength(Sub(P[(i + 1) % M], P[i])); return r; }
        double[] Project(float[][] P, double[] SS)
        {
            var res = new double[nv];
            for (int v = 0; v < nv; v++)
            {
                double vx = d.Positions[3 * v], vz = d.Positions[3 * v + 2], bd = 1e18; int bmi = 0;
                for (int m = 0; m < M; m++)
                {
                    double ax = P[m][0] - vx, az = P[m][2] - vz, dd = ax * ax + az * az;
                    if (dd < bd) { bd = dd; bmi = m; }
                }
                res[v] = SS[bmi];
            }
            return res;
        }
        var S = RunningLength(pts); double L = S[M];
        var sOf = Project(pts, S);
        // the centre line: the vertices' mean per sample, a gap filled between its neighbours; the offset smoothed over one link each side
        var cbx = new double[M]; var cbz = new double[M]; var cbc = new int[M];
        for (int v = 0; v < nv; v++)
        {
            double binD = sOf[v] / L * M;
            if (double.IsNaN(binD)) return "the tread's link-cell rig on a path without a length (d6) is not modelled yet";
            int bi = Math.Min(M - 1, (int)binD);
            cbx[bi] += d.Positions[3 * v]; cbz[bi] += d.Positions[3 * v + 2]; cbc[bi]++;
        }
        var ctr = new float[M][];
        for (int i = 0; i < M; i++) if (cbc[i] != 0) ctr[i] = new[] { (float)(cbx[i] / cbc[i]), 0f, (float)(cbz[i] / cbc[i]) };
        for (int i = 0; i < M; i++)
        {
            if (ctr[i] != null) continue;
            int j = 1; while (ctr[(i + j) % M] == null) j++;
            int k2 = 1; while (ctr[((i - k2) % M + M) % M] == null) k2++;
            ctr[i] = VecLerp(ctr[((i - k2) % M + M) % M], ctr[(i + j) % M], k2 / (double)(k2 + j));
        }
        var delta = new float[M][]; for (int m = 0; m < M; m++) delta[m] = Sub(ctr[m], pts[m]);
        int hw2 = Math.Max(4, (int)PyRound(fund / (L / M)));
        float invCount = (float)(1.0f / (float)(2 * hw2 + 1.0));
        var moved = new float[M][];
        for (int m = 0; m < M; m++)
        {
            var acc = new float[3];
            for (int k = -hw2; k <= hw2; k++) acc = Add(acc, delta[((m + k) % M + M) % M]);
            moved[m] = Add(pts[m], Scale(acc, invCount));
        }
        pts = moved; S = RunningLength(pts); L = S[M];
        sOf = Project(pts, S);
        double cpl = a.TreadCellsPerLink;
        double ncD = PyRound(L / (fund / cpl));
        if (double.IsNaN(ncD) || ncD > 1e7) return "the tread's link-cell rig on a path without a finite length (d6) is not modelled yet";
        int NC = Math.Max(8, (int)ncD);
        int nonLink = clusterBones + 1 + (turrets ? 1 : 0) + (guns ? 1 : 0);
        int linkBudget = Math.Max(8, (int)Math.Floor((240 - nonLink) / (double)Math.Max(1, trackCount)));
        if (NC > linkBudget)
        {
            log.Add("VEHICLE tread '" + o.Name + "' CELL CLAMP: " + NC + " cells -> " + linkBudget + " (" + clusterBones + " wheels+aux leave " + linkBudget
                    + " cells/track) — cells grow accordingly; lower Tread detail to regain smoothness headroom");
            NC = linkBudget;
        }
        double cellL = L / NC;
        // the cut phase: of 24 offsets, the first that crosses the fewest edges
        double bestOff = 0.0; int bestCross = -1; int ne = d.Edges.Length / 2;
        for (int k = 0; k < 24; k++)
        {
            double off = k / 24.0 * cellL; int cross = 0;
            for (int e = 0; e < ne; e++)
                if ((int)(PyMod(sOf[d.Edges[2 * e]] + off, L) / cellL) != (int)(PyMod(sOf[d.Edges[2 * e + 1]] + off, L) / cellL)) cross++;
            if (bestCross < 0 || cross < bestCross) { bestOff = off; bestCross = cross; }
        }
        var cellOf = new int[nv];
        for (int v = 0; v < nv; v++) cellOf[v] = Math.Min(NC - 1, (int)(PyMod(sOf[v] + bestOff, L) / cellL));
        var arcs = arcRaw.Select(r => (a: r.a * L / LP, b: r.b * L / LP)).Where(r => r.b - r.a >= cellL).ToList();
        double margin = a.TreadAdvCells * cellL;
        bool InArcs(double sq)
        {
            foreach (var (a0r, a1r) in arcs)
            {
                double lo2 = a0r - margin, hi2 = a1r + margin, sw = sq;
                if (lo2 < 0 && sw > L + lo2) sw -= L;
                if (hi2 > L && sw < hi2 - L) sw += L;
                if (lo2 <= sw && sw <= hi2) return true;
            }
            return false;
        }
        var job = new TreadJob { Prefix = t.Bones[0].Substring(0, t.Bones[0].Length - 3), NC = NC, CellL = cellL, L = L, S = S, Pts = pts, Off = bestOff, CellOf = cellOf, AdvCells = a.TreadAdvCells, Aux = (string[])t.Bones.Clone() };
        var shuttle = new List<int>();
        foreach (int ci in new SortedSet<int>(cellOf))
            (InArcs(PyMod((ci + 0.5) * cellL - bestOff, L)) ? job.Cells : shuttle).Add(ci);
        foreach (int ci in shuttle)
        {
            if (job.Runs.Count > 0 && ci == job.Runs[job.Runs.Count - 1].Last() + 1) job.Runs[job.Runs.Count - 1].Add(ci);
            else job.Runs.Add(new List<int> { ci });
        }
        if (job.Runs.Count > 1 && job.Runs[0][0] == 0 && job.Runs[job.Runs.Count - 1].Last() == NC - 1)
        {
            job.Runs[0] = job.Runs[job.Runs.Count - 1].Concat(job.Runs[0]).ToList(); job.Runs.RemoveAt(job.Runs.Count - 1);
        }
        job.SRest = new double[NC]; for (int ci = 0; ci < NC; ci++) job.SRest[ci] = PyMod((ci + 0.5) * cellL - bestOff, L);
        t.Job = job;
        log.Add("VEHICLE tread '" + o.Name + "' HYBRID v2: " + job.Cells.Count + " link bones on wraps + " + job.Runs.Count + " shuttle run(s) covering " + shuttle.Count + " cells (cell "
                + Py(cellL, 3) + ", loop " + Py(L, 2) + ", cut phase " + Py(bestOff, 3) + ")");
        return null;
    }
    /// <summary>normalize_v3_v3 in float32: the length (0 for a vector whose squared length is not above 1e-35, which becomes zero).</summary>
    static float NormalizeF(float[] v)
    {
        float d = (float)((float)((float)(v[0] * v[0]) + (float)(v[1] * v[1])) + (float)(v[2] * v[2]));
        if (d > 1.0e-35f)
        {
            d = (float)Math.Sqrt((double)d); float f = (float)(1.0f / d);
            v[0] = (float)(v[0] * f); v[1] = (float)(v[1] * f); v[2] = (float)(v[2] * f);
            return d;
        }
        v[0] = v[1] = v[2] = 0f; return 0f;
    }

    /// <summary>Vector.rotation_difference(other): both normalized (float32), then rotation_between_vecs_to_quat - the axis their
    /// cross product, the angle angle_normalized_v3v3 (2 asinf(|a - b| / 2), from pi on the far side), the quaternion
    /// (cosf(angle / 2), axis sinf(angle / 2)); a pair with no cross product is the identity, or half a turn about ortho_v3_v3.</summary>
    internal static float[] RotationDifference(float[] self, float[] other)
    {
        var va = (float[])self.Clone(); var vb = (float[])other.Clone(); NormalizeF(va); NormalizeF(vb);
        var axis = Cross(va, vb);
        float Dotf(float[] x, float[] y) => (float)((float)((float)(x[0] * y[0]) + (float)(x[1] * y[1])) + (float)(x[2] * y[2]));
        float SafeAsin(float x) => x <= -1.0f ? (float)(-Math.PI / 2.0) : x >= 1.0f ? (float)(Math.PI / 2.0) : BlenderTrig.Asinf(x);
        float Len(float[] x, float[] y) { var dd = new[] { (float)(x[0] - y[0]), (float)(x[1] - y[1]), (float)(x[2] - y[2]) }; return (float)Math.Sqrt((double)Dotf(dd, dd)); }
        if (NormalizeF(axis) > 1.1920929e-7f)   // FLT_EPSILON
        {
            float angle;
            if (Dotf(va, vb) >= 0.0f) angle = (float)(2.0f * SafeAsin((float)(Len(va, vb) / 2.0f)));
            else { var nb = new[] { -vb[0], -vb[1], -vb[2] }; angle = (float)((float)Math.PI - (float)(2.0f * SafeAsin((float)(Len(va, nb) / 2.0f)))); }
            float phi = (float)(0.5f * angle), si = BlenderTrig.Sinf(phi), co = BlenderTrig.Cosf(phi);
            return new[] { co, (float)(axis[0] * si), (float)(axis[1] * si), (float)(axis[2] * si) };
        }
        if (Dotf(va, vb) > 0.0f) return new[] { 1f, 0f, 0f, 0f };
        // opposed: half a turn about a vector square to the first (ortho_v3_v3 by its dominant axis; axis_angle_to_quat normalizes it)
        float ax = Math.Abs(va[0]), ay = Math.Abs(va[1]), az = Math.Abs(va[2]);
        int dom = ax > ay ? (ax > az ? 0 : 2) : (ay > az ? 1 : 2);
        float[] o = dom == 0 ? new[] { (float)(-va[1] - va[2]), va[0], va[0] } : dom == 1 ? new[] { va[1], (float)(-va[0] - va[2]), va[1] } : new[] { va[2], va[2], (float)(-va[0] - va[1]) };
        if (NormalizeF(o) == 0.0f) return new[] { 1f, 0f, 0f, 0f };
        float phiH = (float)(0.5f * (float)Math.PI), siH = BlenderTrig.Sinf(phiH), coH = BlenderTrig.Cosf(phiH);
        return new[] { coH, (float)(o[0] * siH), (float)(o[1] * siH), (float)(o[2] * siH) };
    }

    /// <summary>Matrix.Translation(v): the identity with v in its last column (row-major, as MatMulMathutils takes it).</summary>
    static float[] TranslationRow(float x, float y, float z) { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f; m[3] = x; m[7] = y; m[11] = z; return m; }

    /// <summary>The pose matrix of a link bone carried along the path: Translation(P1) @ q.to_matrix().to_4x4() @ Translation(-P0) @
    /// rest (mathutils products, row-major) with q = t0.rotation_difference(t1).</summary>
    internal static float[] LinkPoseMatrix(float[] p0, float[] t0, float[] p1, float[] t1, float[] restRowMajor)
    {
        var q = RotationDifference(t0, t1);
        var m3 = BlenderChannelExport.QuatToMat3(q);   // Blender's mat[column][row]
        var r4 = new float[16]; r4[15] = 1f;
        for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++) r4[4 * row + col] = m3[col][row];
        var m = VehicleProbe.MatMulMathutils(TranslationRow(p1[0], p1[1], p1[2]), r4);
        m = VehicleProbe.MatMulMathutils(m, TranslationRow(-p0[0], -p0[1], -p0[2]));
        return VehicleProbe.MatMulMathutils(m, restRowMajor);
    }
}
