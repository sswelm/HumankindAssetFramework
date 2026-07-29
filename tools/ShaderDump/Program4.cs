// Pass 4: find the Shader (classId 48) that consumes _AnimatedSkeletonEntryBuffer, decompress its
// blob (LZ4 segments), extract DXBC programs containing the marker, disassemble them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

class Program4
{
    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DDisassemble(IntPtr pSrcData, IntPtr srcDataSize, uint flags, string comments, out IntPtr disassembly);

    [StructLayout(LayoutKind.Sequential)]
    struct BlobVtbl { public IntPtr QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize; }
    delegate IntPtr PtrDel(IntPtr self);

    static string Disasm(byte[] blob, int off, int len)
    {
        var b = new byte[len];
        Array.Copy(blob, off, b, 0, len);
        unsafe
        {
            fixed (byte* p = b)
            {
                int hr = D3DDisassemble((IntPtr)p, (IntPtr)b.Length, 0, null, out IntPtr ob);
                if (hr != 0 || ob == IntPtr.Zero) return null;
                var vtbl = Marshal.PtrToStructure<BlobVtbl>(Marshal.ReadIntPtr(ob));
                IntPtr bp = Marshal.GetDelegateForFunctionPointer<PtrDel>(vtbl.GetBufferPointer)(ob);
                long n = Marshal.GetDelegateForFunctionPointer<PtrDel>(vtbl.GetBufferSize)(ob).ToInt64();
                var bytes = new byte[n];
                Marshal.Copy(bp, bytes, 0, (int)n);
                return Encoding.ASCII.GetString(bytes);
            }
        }
    }

    // LZ4 block decompress (raw, no frame header) — Unity's shader blob segments
    static byte[] Lz4Decompress(byte[] src, int srcOff, int srcLen, int dstLen)
    {
        var dst = new byte[dstLen];
        int s = srcOff, d = 0, end = srcOff + srcLen;
        while (s < end && d < dstLen)
        {
            byte token = src[s++];
            int lit = token >> 4;
            if (lit == 15) { byte b; do { b = src[s++]; lit += b; } while (b == 255); }
            Array.Copy(src, s, dst, d, lit); s += lit; d += lit;
            if (s >= end) break;
            int offset = src[s] | (src[s + 1] << 8); s += 2;
            int match = token & 15;
            if (match == 15) { byte b; do { b = src[s++]; match += b; } while (b == 255); }
            match += 4;
            int m = d - offset;
            for (int i = 0; i < match; i++) dst[d++] = dst[m++];
        }
        return dst;
    }

    static void Main4Old2(string[] args)
    {
        string path = args[0];
        var mgr = new AssetsManager();
        var bun = mgr.LoadBundleFile(path, true);
        var afile = mgr.LoadAssetsFileFromBundle(bun, 0, false);
        byte[] marker = Encoding.ASCII.GetBytes("_AnimatedSkeletonEntryBuffer");
        foreach (var info in afile.file.AssetInfos)
        {
            if (info.TypeId != 48) continue;   // Shader
            var bf = mgr.GetBaseField(afile, info);
            string name = "?";
            try { name = bf["m_ParsedForm"]["m_Name"].AsString; } catch { try { name = bf["m_Name"].AsString; } catch { } }
            byte[] compressed = null;
            try { compressed = bf["compressedBlob"]["Array"].AsByteArray; } catch { try { compressed = bf["m_CompressedBlob"]["Array"].AsByteArray; } catch { } }
            if (compressed == null) { Console.WriteLine($"shader {name}: no blob field"); continue; }

            // segment tables: vector<vector<uint>> — outer = platform, inner = segments
            List<uint> offs = new(), clens = new(), dlens = new();
            void ReadArr(string fname, List<uint> into)
            {
                foreach (var plat in bf[fname]["Array"])
                {
                    try { into.Add(plat.AsUInt); continue; } catch { }
                    try { foreach (var x in plat["Array"]) into.Add(x.AsUInt); } catch { }
                }
            }
            ReadArr("offsets", offs); ReadArr("compressedLengths", clens); ReadArr("decompressedLengths", dlens);

            for (int seg = 0; seg < offs.Count; seg++)
            {
                byte[] blob;
                try { blob = Lz4Decompress(compressed, (int)offs[seg], (int)clens[seg], (int)dlens[seg]); }
                catch (Exception ex) { Console.WriteLine($"shader {name} seg {seg}: lz4 fail {ex.Message}"); continue; }
                // scan for marker
                bool hasMarker = false;
                for (int i = 0; i <= blob.Length - marker.Length && !hasMarker; i++)
                {
                    if (blob[i] != marker[0]) continue;
                    hasMarker = true;
                    for (int k = 1; k < marker.Length; k++) if (blob[i + k] != marker[k]) { hasMarker = false; break; }
                }
                if (!hasMarker) continue;
                Console.WriteLine($"shader '{name}' seg {seg}: MARKER FOUND ({blob.Length} bytes decompressed)");
                if (name.EndsWith("ParticleSkinnedMeshRender Implementation"))
                    File.WriteAllBytes($"psmr_seg{seg}.bin", blob);
                // extract DXBC programs containing the marker
                int found = 0;
                for (int i = 0; i <= blob.Length - 4; i++)
                {
                    if (blob[i] != 'D' || blob[i + 1] != 'X' || blob[i + 2] != 'B' || blob[i + 3] != 'C') continue;
                    if (i + 28 > blob.Length) break;
                    uint total = BitConverter.ToUInt32(blob, i + 24);
                    if (total < 200 || total > 4_000_000 || i + total > blob.Length) continue;
                    bool hit = false;
                    for (int j = i; j <= i + total - marker.Length && !hit; j++)
                    {
                        if (blob[j] != marker[0]) continue;
                        hit = true;
                        for (int k = 1; k < marker.Length; k++) if (blob[j + k] != marker[k]) { hit = false; break; }
                    }
                    if (hit)
                    {
                        string asm = Disasm(blob, i, (int)total);
                        if (asm != null)
                        {
                            string safe = name.Replace('/', '_').Replace(' ', '_');
                            string file = $"vs_{safe}_s{seg}_{found}.asm";
                            File.WriteAllText(file, asm);
                            string kind = asm.Contains("vs_5_0") ? "VERTEX" : asm.Contains("ps_5_0") ? "PIXEL" : asm.Contains("cs_5_0") ? "COMPUTE" : "?";
                            Console.WriteLine($"  DXBC @{i} len {total} {kind} -> {file}");
                            found++;
                        }
                    }
                    i += (int)total - 1;
                }
            }
        }
    }
}
