// DXBC extractor + disassembler for the AmpliAnimation compute shader (Resize Lab shader hunt, 2026-07-29).
// Scans a Unity asset bundle for DXBC blobs, keeps those whose bytes mention the pawn skinning buffers,
// and disassembles them via d3dcompiler_47.dll (stock Windows).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DDisassemble(IntPtr pSrcData, IntPtr srcDataSize, uint flags, string comments, out IntPtr disassembly);

    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DCreateBlob(IntPtr size, out IntPtr blob);

    // ID3DBlob vtable calls
    [StructLayout(LayoutKind.Sequential)]
    struct BlobVtbl { public IntPtr QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize; }

    delegate IntPtr GetBufferPointerDel(IntPtr self);
    delegate IntPtr GetBufferSizeDel(IntPtr self);

    static string BlobToString(IntPtr blob)
    {
        var vtbl = Marshal.PtrToStructure<BlobVtbl>(Marshal.ReadIntPtr(blob));
        var gbp = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDel>(vtbl.GetBufferPointer);
        var gbs = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDel>(vtbl.GetBufferSize);
        IntPtr p = gbp(blob);
        long n = gbs(blob).ToInt64();
        var bytes = new byte[n];
        Marshal.Copy(p, bytes, 0, (int)n);
        return Encoding.ASCII.GetString(bytes);
    }

    static void MainOld(string[] args)
    {
        string path = args[0];
        string outDir = args.Length > 1 ? args[1] : ".";
        byte[] data;
        if (path.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase))
        {
            // UnityFS bundle: decompress (LZ4/LZMA blocks) and concatenate the contained files
            var mgr = new AssetsTools.NET.Extra.AssetsManager();
            var bun = mgr.LoadBundleFile(path, true);
            using var ms = new MemoryStream();
            var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
            for (int d = 0; d < dirs.Count; d++)
            {
                var fileData = AssetsTools.NET.Extra.BundleHelper.LoadAssetDataFromBundle(bun.file, d);
                Console.WriteLine($"bundle file {d}: {dirs[d].Name} ({fileData.Length} bytes)");
                ms.Write(fileData, 0, fileData.Length);
            }
            data = ms.ToArray();
            File.WriteAllBytes(Path.Combine(outDir, "decompressed.bin"), data);
        }
        else data = File.ReadAllBytes(path);
        byte[] magic = Encoding.ASCII.GetBytes("DXBC");
        byte[] marker = Encoding.ASCII.GetBytes("_SkeletonBoneBuffer");
        int found = 0, kept = 0;
        for (int i = 0; i < data.Length - 4; i++)
        {
            if (data[i] != magic[0] || data[i + 1] != magic[1] || data[i + 2] != magic[2] || data[i + 3] != magic[3]) continue;
            if (i + 32 > data.Length) break;
            uint total = BitConverter.ToUInt32(data, i + 24);
            if (total < 200 || total > 8_000_000 || i + total > data.Length) continue;
            found++;
            // keep only blobs that reference the skinning buffers
            bool hit = false;
            for (int j = i; j < i + total - marker.Length && !hit; j++)
            {
                if (data[j] != marker[0]) continue;
                hit = true;
                for (int k = 1; k < marker.Length; k++)
                    if (data[j + k] != marker[k]) { hit = false; break; }
            }
            if (!hit) { i += 4; continue; }
            kept++;
            var blob = new byte[total];
            Array.Copy(data, i, blob, 0, total);
            string asm = "(disassembly failed)";
            unsafe
            {
                fixed (byte* p = blob)
                {
                    int hr = D3DDisassemble((IntPtr)p, (IntPtr)blob.Length, 0, null, out IntPtr outBlob);
                    if (hr == 0 && outBlob != IntPtr.Zero) asm = BlobToString(outBlob);
                    else asm = $"(disassembly failed hr=0x{hr:X8})";
                }
            }
            string file = Path.Combine(outDir, $"blob_{i:X8}.asm");
            File.WriteAllText(file, asm);
            Console.WriteLine($"blob at 0x{i:X8} size {total} -> {file} ({asm.Length} chars)");
            i += (int)total - 1;
        }
        Console.WriteLine($"DXBC blobs scanned: {found}, with skinning buffers: {kept}");
    }
}
