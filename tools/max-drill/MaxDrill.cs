// MaxDrill.cs - the native 3ds Max reader outside Unity (tools/max_drill.sh compiles and runs it):
//   MaxDrill stats   <file.max>            one PART line per mesh part, in WORLD space and 3ds Max axes (compared with
//                                          tools/max-drill/max_world.py, an independent Python reader of the same file)
//   MaxDrill convert <file.max> <out.glb>  the conversion the tools' file pickers run, with the report
// JPEG pixels come from System.Drawing here (Unity's loader in the editor); the composition code is the same.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

static class MaxDrill
{
    static int Main(string[] a)
    {
        try
        {
            if (a.Length >= 2 && a[0] == "stats") return Stats(a[1]);
            if (a.Length >= 3 && a[0] == "convert")
            {
                var rep = MaxModel.ConvertToGlb(a[1], a[2], Decode);
                Console.WriteLine(rep.Summary());
                var back = GlbReader.Read(a[2]);
                Console.WriteLine("READ BACK: " + back.Nodes.Count + " nodes, " + back.Meshes.Count + " meshes, " + back.TriangleCount + " triangles, " + back.Materials.Count + " materials, " + back.Images.Count + " images; generator '" + back.Generator + "'; ours " + MaxModel.IsOurs(a[2]));
                return 0;
            }
            Console.Error.WriteLine("usage: MaxDrill stats <file.max> | convert <file.max> <out.glb>");
            return 2;
        }
        catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is IOException)
        {
            Console.WriteLine("REFUSED " + e.GetType().Name + ": " + e.Message);
            return 3;
        }
    }

    static string G(double v) => v.ToString("0.000000e+00", CultureInfo.InvariantCulture);

    static int Stats(string path)
    {
        var m = MaxModel.Read(path, Decode, out var rep);
        var lines = new List<string>();
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = m.Nodes[i];
            if (n.Mesh < 0) continue;
            double tx = 0, ty = 0, tz = 0;
            for (int at = i; at >= 0; at = m.Nodes[at].Parent) { tx += m.Nodes[at].Translation[0]; ty += m.Nodes[at].Translation[1]; tz += m.Nodes[at].Translation[2]; }
            long tris = 0; double area = 0, vol = 0; bool uv = false;
            double[] lo = { 1e300, 1e300, 1e300 }, hi = { -1e300, -1e300, -1e300 };
            foreach (var p in m.Meshes[n.Mesh].Primitives)
            {
                if (p.Uv0 != null) uv = true;
                foreach (var t in p.Triangles())
                {
                    var c = new double[9]; int k = 0;
                    foreach (int v in new[] { t.a, t.b, t.c })
                    {
                        double gx = p.Positions[v * 3] + tx, gy = p.Positions[v * 3 + 1] + ty, gz = p.Positions[v * 3 + 2] + tz;
                        c[k] = gx; c[k + 1] = -gz; c[k + 2] = gy; k += 3;      // back to 3ds Max axes
                    }
                    for (int j = 0; j < 9; j++) { if (c[j] < lo[j % 3]) lo[j % 3] = c[j]; if (c[j] > hi[j % 3]) hi[j % 3] = c[j]; }
                    double ux = c[3] - c[0], uy = c[4] - c[1], uz = c[5] - c[2], vx = c[6] - c[0], vy = c[7] - c[1], vz = c[8] - c[2];
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    vol += (c[0] * (c[4] * c[8] - c[5] * c[7]) - c[1] * (c[3] * c[8] - c[5] * c[6]) + c[2] * (c[3] * c[7] - c[4] * c[6])) / 6;
                    tris++;
                }
            }
            lines.Add("PART " + n.Name.Trim().Replace(' ', '_') + " tris=" + tris + " uv=" + (uv ? 1 : 0) + " area=" + G(area) + " vol=" + G(vol)
                + " lo=" + G(lo[0]) + "," + G(lo[1]) + "," + G(lo[2]) + " hi=" + G(hi[0]) + "," + G(hi[1]) + "," + G(hi[2]));
        }
        lines.Sort(StringComparer.Ordinal);
        foreach (var l in lines) Console.WriteLine(l);
        Console.WriteLine("TOTAL parts=" + rep.Parts + " tris=" + rep.Triangles + " uvparts=" + rep.PartsWithUv + " hidden=" + rep.HiddenFaces);
        return 0;
    }

    static bool Decode(byte[] file, string name, out int w, out int h, out byte[] rgba)
    {
        w = h = 0; rgba = null;
        try
        {
            using (var ms = new MemoryStream(file))
            using (var src = new System.Drawing.Bitmap(ms))
            using (var bmp = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(src, new System.Drawing.Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel);
                }
                w = bmp.Width; h = bmp.Height;
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var raw = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, raw, y * w * 4, w * 4);
                bmp.UnlockBits(data);
                rgba = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i += 4) { rgba[i] = raw[i + 2]; rgba[i + 1] = raw[i + 1]; rgba[i + 2] = raw[i]; rgba[i + 3] = raw[i + 3]; }
                return true;
            }
        }
        catch (Exception) { rgba = null; return false; }
    }
}
