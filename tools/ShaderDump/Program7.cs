// Pass 7: batch-disassemble ALL DXBC blobs in the segment; report any variant that loads the IBP
// scale word (offset 28 of t1) or reads the bone-entry scale in a second place (l(20), t3).
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

class Program7
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

    static void Main(string[] args)
    {
        byte[] data = File.ReadAllBytes(args[0]);
        int scanned = 0, vs = 0, ibpScaleLoads = 0, entryScaleLoads = 0, fails = 0;
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] != 'D' || data[i + 1] != 'X' || data[i + 2] != 'B' || data[i + 3] != 'C') continue;
            if (i + 28 > data.Length) break;
            uint total = BitConverter.ToUInt32(data, i + 24);
            if (total < 200 || total > 4_000_000 || i + total > data.Length) continue;
            var blob = new byte[total];
            Array.Copy(data, i, blob, 0, total);
            scanned++;
            string asm = Disasm(blob);
            if (asm == null) { fails++; i += 4; continue; }
            if (asm.Contains("vs_4_0") || asm.Contains("vs_5_0")) vs++;
            if (asm.Contains("l(28), t1")) { ibpScaleLoads++; Console.WriteLine($"blob 0x{i:X}: LOADS IBP.Scale (t1 offset 28)!"); }
            // any load from t3 at offset 20 as a standalone (scale-only) read
            if (asm.Contains("l(20), t3")) { entryScaleLoads++; Console.WriteLine($"blob 0x{i:X}: standalone entry-scale load (t3 offset 20)"); }
            i += (int)total - 1;
        }
        Console.WriteLine($"scanned {scanned} (fails {fails}), vertex shaders {vs}, IBP-scale loads {ibpScaleLoads}, standalone entry-scale loads {entryScaleLoads}");
    }
}
