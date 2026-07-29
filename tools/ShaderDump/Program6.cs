// Pass 6: disassemble DXBC blobs from the dumped ParticleSkinnedMeshRender segment; keep vertex
// shaders that read a stride-40 structured buffer (AnimatedBoneEntry) and report scale usage.
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

class Program6
{
    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DDisassemble(IntPtr pSrcData, IntPtr srcDataSize, uint flags, string comments, out IntPtr disassembly);

    [StructLayout(LayoutKind.Sequential)]
    struct BlobVtbl { public IntPtr QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize; }
    delegate IntPtr PtrDel(IntPtr self);

    static string Disasm(byte[] b)
    {
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

    static void Main6Old(string[] args)
    {
        byte[] data = File.ReadAllBytes(args.Length > 0 ? args[0] : "psmr_seg0.bin");
        int kept = 0, scanned = 0;
        for (int i = 0; i <= data.Length - 4 && kept < 3; i++)
        {
            if (data[i] != 'D' || data[i + 1] != 'X' || data[i + 2] != 'B' || data[i + 3] != 'C') continue;
            if (i + 28 > data.Length) break;
            uint total = BitConverter.ToUInt32(data, i + 24);
            if (total < 200 || total > 4_000_000 || i + total > data.Length) { continue; }
            var blob = new byte[total];
            Array.Copy(data, i, blob, 0, total);
            scanned++;
            string asm = Disasm(blob);
            if (asm == null) { Console.WriteLine($"blob 0x{i:X} size {total}: disasm FAILED"); i += 4; continue; }
            if (scanned == 1) File.WriteAllText("psmr_first.asm", asm);
            if (asm.Contains("vs_5_0") && asm.Contains("stride=40"))
            {
                string file = $"psmr_vs_{i:X}.asm";
                File.WriteAllText(file, asm);
                Console.WriteLine($"VS with stride-40 buffer at 0x{i:X} -> {file} ({asm.Length} chars)");
                kept++;
            }
            i += (int)total - 1;
        }
        Console.WriteLine($"scanned {scanned} blobs, kept {kept}");
    }
}
