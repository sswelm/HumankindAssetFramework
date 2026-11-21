// UnityMaxImportDrill.cs - the .max door INSIDE a Unity editor (tools/max_unity_drill.sh builds a temporary project and
// runs this in batch mode). What only Unity can show: MaxImport.Convert end to end with Unity's own image decoder
// (ImageConversion.LoadImage), the row order and channel order of that decoder's hand-over, and the door's small
// contracts (Picked, ExitGuiAfterDialogs, a failed conversion). The geometry is held to the reference .glb that the
// stand-alone drill's executable wrote from the same file with another decoder (System.Drawing).
//   -hafMaxOutput <result.txt>  -hafMaxList <list.txt>   (list lines: <copy of file.max>|<reference .glb>)
// Batch mode answers the replacement dialog with Cancel. The drill checks that Convert returns null and preserves an
// edited GLB. MaxReaderTests covers approval, and the dialog and IMGUI layout require a live editor to inspect.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class UnityMaxImportDrill
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder(); int failed = 0, checks = 0;
        void Check(string name, bool ok) { checks++; log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
        var args = Environment.GetCommandLineArgs();
        string Arg(string key) { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
        try
        {
            // 1. Unity's decoder hands pixels over top row first, RGBA: an asymmetric picture, encoded by the project's
            //    own PNG encoder (independent of Unity's), must come back byte for byte.
            {
                int w = 5, h = 3; var rgba = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++) { rgba[i * 4] = (byte)(10 + i * 13); rgba[i * 4 + 1] = (byte)(200 - i * 7); rgba[i * 4 + 2] = (byte)(i * 17); rgba[i * 4 + 3] = (byte)(255 - i * 3); }
                byte[] png = PngCodec.Encode(w, h, rgba, true, false);
                var decode = typeof(MaxImport).GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic);
                object[] a = { png, "probe.png", 0, 0, null };
                bool ok = (bool)decode.Invoke(null, a);
                var got = a[4] as byte[];
                bool same = ok && (int)a[2] == w && (int)a[3] == h && got != null && got.Length == rgba.Length;
                int firstDiff = -1;
                if (same) for (int i = 0; i < rgba.Length; i++) if (got[i] != rgba[i]) { same = false; firstDiff = i; break; }
                Check("Unity's decoder returns a 5x3 RGBA picture byte for byte, top row first" + (firstDiff >= 0 ? " (first difference at byte " + firstDiff + ": " + got[firstDiff] + " for " + rgba[firstDiff] + ")" : ""), same);
                object[] b = { new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "junk.jpg", 0, 0, null };
                Check("Unity's decoder refuses eight junk bytes instead of handing over a picture", !(bool)decode.Invoke(null, b) && b[4] == null);
            }

            // 1b. recorded, not judged: what GUI.FocusControl does outside a GUI pass (MaxImport.ConvertTyped uses
            //     GUIUtility.keyboardControl instead, and its comment says why)
            try { GUI.FocusControl(null); log.AppendLine("NOTE GUI.FocusControl(null) outside a GUI pass: no exception"); }
            catch (Exception e) { log.AppendLine("NOTE GUI.FocusControl(null) outside a GUI pass throws " + e.GetType().Name + ": " + e.Message); }

            // 2. The door's small contracts.
            Check("Picked(null) is null", MaxImport.Picked(null) == null);
            Check("Picked of a .glb is that path", MaxImport.Picked("C:/some/where/model.glb") == "C:/some/where/model.glb");
            bool threw = false;
            try { MaxImport.ExitGuiAfterDialogs(); } catch (ExitGUIException) { threw = true; }
            Check("ExitGuiAfterDialogs does nothing when no conversion ran", !threw);

            string list = Arg("-hafMaxList");
            var lines = list != null && File.Exists(list) ? File.ReadAllLines(list) : new string[0];
            Check("the list of files is not empty", lines.Length > 0);
            int n = 0;
            foreach (string line in lines)
            {
                if (line.Trim().Length == 0) continue;
                n++;
                string max = line.Split('|')[0], reference = line.Split('|')[1];
                string tag = Path.GetFileName(max);
                string expect = MaxModel.DefaultGlbPath(max);
                if (File.Exists(expect)) File.Delete(expect);

                // 3. the picker's call, then the pass's exit.
                string got = MaxImport.Picked(max);
                Check(tag + ": Picked returns name_max.glb beside the file", got != null && Path.GetFullPath(got) == Path.GetFullPath(expect) && File.Exists(expect));
                threw = false;
                try { MaxImport.ExitGuiAfterDialogs(); } catch (ExitGUIException) { threw = true; }
                Check(tag + ": ExitGuiAfterDialogs leaves the GUI pass after a conversion", threw);
                threw = false;
                try { MaxImport.ExitGuiAfterDialogs(); } catch (ExitGUIException) { threw = true; }
                Check(tag + ": ... and only once", !threw);
                if (got == null) continue;
                Check(tag + ": the written file is recognised as the reader's own", MaxModel.IsOurs(expect));

                // 4. against the reference written outside Unity with another decoder.
                var mine = GlbReader.Read(expect); var theirs = GlbReader.Read(reference);
                bool shape = mine.Nodes.Count == theirs.Nodes.Count && mine.Meshes.Count == theirs.Meshes.Count && mine.Materials.Count == theirs.Materials.Count && mine.Images.Count == theirs.Images.Count;
                Check(tag + ": nodes, meshes, materials and images count as in the reference (" + mine.Nodes.Count + "/" + mine.Meshes.Count + "/" + mine.Materials.Count + "/" + mine.Images.Count + " for " + theirs.Nodes.Count + "/" + theirs.Meshes.Count + "/" + theirs.Materials.Count + "/" + theirs.Images.Count + ")", shape);
                if (!shape) continue;
                long floats = 0, differ = 0;
                for (int m = 0; m < mine.Meshes.Count; m++)
                {
                    if (mine.Meshes[m].Primitives.Count != theirs.Meshes[m].Primitives.Count) { differ++; continue; }
                    for (int p = 0; p < mine.Meshes[m].Primitives.Count; p++)
                    {
                        var x = mine.Meshes[m].Primitives[p]; var y = theirs.Meshes[m].Primitives[p];
                        differ += Differ(x.Positions, y.Positions, ref floats) + Differ(x.Normals, y.Normals, ref floats) + Differ(x.Uv0, y.Uv0, ref floats);
                        if (x.Material != y.Material) differ++;
                    }
                }
                Check(tag + ": positions, normals and UVs equal to the reference bit for bit (" + floats + " floats, " + differ + " differ)", differ == 0 && floats > 0);

                // 5. the pictures: same sizes; pixels within a JPEG decoder's rounding of the reference (a mean of 2 in
                //    255 is the LIMIT; what a run measures is printed per picture), and never nearer to the reference
                //    turned upside down than to the reference (a flipped hand-over scores the other way round).
                int pictures = 0, decoded = 0, upsideDown = 0; double worstMean = 0; int worstMax = 0; bool sizes = true, rawSame = true;
                for (int i = 0; i < mine.Images.Count; i++)
                {
                    byte[] a = mine.Images[i].Bytes, b = theirs.Images[i].Bytes;
                    if (a == null || b == null) { if (a != b) rawSame = false; continue; }
                    pictures++;
                    if (!PngCodec.IsPng(a) || !PngCodec.IsPng(b)) { if (!Same(a, b)) rawSame = false; continue; }
                    byte[] pa = PngCodec.Decode(a, out var ia, out string wa), pb = PngCodec.Decode(b, out var ib, out string wb);
                    if (pa == null || pb == null || ia.Width != ib.Width || ia.Height != ib.Height || pa.Length != pb.Length) { sizes = false; continue; }
                    decoded++;
                    int bpp = pa.Length / (ia.Width * ia.Height);
                    long sum = 0, flip = 0; int largest = 0; int row = ia.Width * bpp;
                    for (int y = 0; y < ia.Height; y++)
                        for (int x = 0; x < row; x++)
                        {
                            int d = Math.Abs(pa[y * row + x] - pb[y * row + x]); sum += d; if (d > largest) largest = d;
                            flip += Math.Abs(pa[y * row + x] - pb[(ia.Height - 1 - y) * row + x]);
                        }
                    double mean = sum / (double)pa.Length, meanFlip = flip / (double)pa.Length;
                    log.AppendLine("  picture " + i + " " + ia.Width + "x" + ia.Height + ": mean difference " + mean.ToString("0.000", CultureInfo.InvariantCulture) + ", largest " + largest + ", against the reference upside down " + meanFlip.ToString("0.000", CultureInfo.InvariantCulture));
                    if (mean > worstMean) worstMean = mean; if (largest > worstMax) worstMax = largest;
                    if (mean > meanFlip) upsideDown++;
                }
                Check(tag + ": every picture has the reference's size, and pictures embedded as they are equal it byte for byte (" + pictures + " pictures, " + decoded + " decoded)", sizes && rawSame);
                if (decoded > 0)
                {
                    Check(tag + ": the composed pictures agree with the reference's within a decoder's rounding (worst mean difference " + worstMean.ToString("0.000", CultureInfo.InvariantCulture) + " of 255, limit 2; largest single " + worstMax + ")", worstMean <= 2.0);
                    Check(tag + ": no picture is nearer to the reference turned upside down than to the reference (" + upsideDown + " are)", upsideDown == 0);
                }

                // 6. the same file picked again: the copy holds these bytes already and is NOT written again (a tool
                //    that watches its source's write time - the Cutter with unsaved marks - sees no change).
                byte[] first = File.ReadAllBytes(expect);
                var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(expect, stamp);
                string again = MaxImport.Convert(max);
                Check(tag + ": converting again gives the same bytes and leaves the file untouched (its write time stays)", again != null && Same(first, File.ReadAllBytes(expect)) && File.GetLastWriteTimeUtc(expect) == stamp);

                // 6b. the typed-path button's deferred half, called as the editor calls it (outside any GUI pass):
                //     the field's setter receives the .glb and nothing throws.
                string typed = null; Exception typedFailed = null;
                try { MaxImport.ConvertTyped(max, g => typed = g); } catch (Exception e) { typedFailed = e; }
                Check(tag + ": the Convert button's deferred half hands the .glb to the field outside a GUI pass" + (typedFailed != null ? " (" + typedFailed.GetType().Name + ": " + typedFailed.Message + ")" : ""),
                    typedFailed == null && typed != null && Path.GetFullPath(typed) == Path.GetFullPath(expect));

                if (n == 1)
                {
                    // A HAF rewrite keeps the generator even when the contents have been edited.
                    mine.Nodes[0].Name += " (edited)";
                    GlbWriter.Write(mine, expect);
                    byte[] edited = File.ReadAllBytes(expect);
                    File.SetLastWriteTimeUtc(expect, stamp);
                    Check(tag + ": a derivative keeps the .max generator and has different contents", MaxModel.IsOurs(expect) && !Same(first, edited));
                    string declined = MaxImport.Convert(max);  // Unity answers Cancel in batch mode
                    Check(tag + ": cancelling replacement preserves the edited GLB", declined == null && Same(edited, File.ReadAllBytes(expect)) && File.GetLastWriteTimeUtc(expect) == stamp);
                }

                // 7. a cut-off copy fails with the user told, nothing thrown, and no file left behind.
                string cut = Path.Combine(Path.GetDirectoryName(max), "cut" + n + ".max");
                byte[] whole = File.ReadAllBytes(max); var part = new byte[whole.Length / 3]; Array.Copy(whole, part, part.Length);
                File.WriteAllBytes(cut, part);
                string none = "not run"; Exception escaped = null;
                try { none = MaxImport.Convert(cut); } catch (Exception e) { escaped = e; }
                Check(tag + ": a file cut to a third is refused without an exception" + (escaped != null ? " (" + escaped.GetType().Name + ": " + escaped.Message + ")" : ""), escaped == null && none == null);
                Check(tag + ": ... and leaves no .glb behind", !File.Exists(MaxModel.DefaultGlbPath(cut)));
            }

            // 8. a file that is no .max at all, and one that is not there.
            string work = Arg("-hafMaxWork");
            if (work != null)
            {
                string text = Path.Combine(work, "words.max"); File.WriteAllText(text, "this is not a compound file");
                string r = "not run"; Exception esc = null;
                try { r = MaxImport.Convert(text); } catch (Exception e) { esc = e; }
                Check("a text file named .max is refused without an exception", esc == null && r == null && !File.Exists(MaxModel.DefaultGlbPath(text)));
                r = "not run"; esc = null;
                try { r = MaxImport.Convert(Path.Combine(work, "absent.max")); } catch (Exception e) { esc = e; }
                Check("a .max that does not exist is refused without an exception" + (esc != null ? " (" + esc.GetType().Name + ")" : ""), esc == null && r == null);
                string foreign = Path.Combine(work, "words_max.glb"); File.WriteAllText(foreign, "somebody else's file");
                bool ours = true; esc = null;
                try { ours = MaxModel.IsOurs(foreign); } catch (Exception e) { esc = e; }
                Check("a foreign file at the target's name is not taken for the reader's own, without an exception", esc == null && !ours);
            }
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + ex); }
        log.AppendLine("TOTAL checks " + checks);
        log.AppendLine("TOTAL failed " + failed);
        string output = Arg("-hafMaxOutput");
        if (output != null) File.WriteAllText(output, log.ToString());
        Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static long Differ(float[] a, float[] b, ref long count)
    {
        if (a == null && b == null) return 0;
        if (a == null || b == null || a.Length != b.Length) return 1;
        long d = 0; count += a.Length;
        for (int i = 0; i < a.Length; i++) if (BitConverter.ToInt32(BitConverter.GetBytes(a[i]), 0) != BitConverter.ToInt32(BitConverter.GetBytes(b[i]), 0)) d++;
        return d;
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
