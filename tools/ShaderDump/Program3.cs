// Pass 3: extract each kernel's `code` bytes from the AmpliAnimation ComputeShader and disassemble.
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

class Program3
{
    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DDisassemble(IntPtr pSrcData, IntPtr srcDataSize, uint flags, string comments, out IntPtr disassembly);

    [StructLayout(LayoutKind.Sequential)]
    struct BlobVtbl { public IntPtr QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize; }
    delegate IntPtr PtrDel(IntPtr self);

    static string Disasm(byte[] blob)
    {
        unsafe
        {
            fixed (byte* p = blob)
            {
                int hr = D3DDisassemble((IntPtr)p, (IntPtr)blob.Length, 0, null, out IntPtr ob);
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

    static void Main3Old(string[] args)
    {
        string path = args[0];
        var mgr = new AssetsManager();
        var bun = mgr.LoadBundleFile(path, true);
        var afile = mgr.LoadAssetsFileFromBundle(bun, 0, false);
        foreach (var info in afile.file.AssetInfos)
        {
            if (info.TypeId != 72) continue;
            var bf = mgr.GetBaseField(afile, info);
            if (bf["m_Name"].AsString != "AmpliAnimation") continue;
            foreach (var variant in bf["variants"]["Array"])
            {
                int renderer = variant["targetRenderer"].AsInt;
                foreach (var kernel in variant["kernels"]["Array"])
                {
                    string kname = kernel["name"].AsString;
                    foreach (var pair in kernel["variantMap"]["Array"])
                    {
                        var code = pair["second"]["code"]["Array"].AsByteArray;
                        string tag = $"r{renderer}_{kname}";
                        File.WriteAllBytes(tag + ".bin", code);
                        string head = BitConverter.ToString(code, 0, Math.Min(16, code.Length));
                        string note = "";
                        if (code.Length > 4 && code[0] == (byte)'D' && code[1] == (byte)'X')
                        {
                            var asm = Disasm(code);
                            if (asm != null) { File.WriteAllText(tag + ".asm", asm); note = $" DISASSEMBLED -> {tag}.asm ({asm.Length} chars)"; }
                            else note = " disasm FAILED";
                        }
                        Console.WriteLine($"{tag}: {code.Length} bytes, head {head}{note}");
                    }
                }
            }
        }
    }
}
