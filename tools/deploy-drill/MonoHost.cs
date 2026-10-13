// Run the drill on Unity's bundled 64-bit Mono, without opening an editor or substituting the port's native math.
// Unity's standalone mono.exe is 32-bit: it cannot call the C runtime's float functions needed by a full deploy.
// This small .NET Framework bootstrap only enters mono_jit_exec; all drill code runs in the embedded Mono runtime.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

static class MonoHost
{
    const string Mono = "mono-2.0-bdwgc.dll";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)]
    static extern void mono_set_dirs([MarshalAs(UnmanagedType.LPUTF8Str)] string assemblies, [MarshalAs(UnmanagedType.LPUTF8Str)] string config);
    [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)]
    static extern void mono_config_parse(string path);
    [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr mono_domain_assembly_open(IntPtr domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)]
    static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, IntPtr argv);

    static IntPtr Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        IntPtr p = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, p, bytes.Length); return p;
    }

    static int Main(string[] args)
    {
        try
        {
            if (IntPtr.Size != 8 || args.Length < 2) throw new ArgumentException("expected 64-bit host, MonoBleedingEdge directory and assembly");
            string runtime = Path.GetFullPath(args[0]), exe = Path.GetFullPath(args[1]);
            if (LoadLibraryW(Path.Combine(runtime, "EmbedRuntime", Mono)) == IntPtr.Zero)
                throw new InvalidOperationException("could not load Unity's 64-bit Mono library (Win32 error " + Marshal.GetLastWin32Error() + ")");
            mono_set_dirs(Path.Combine(runtime, "lib"), Path.Combine(runtime, "etc"));
            mono_config_parse(null);
            IntPtr domain = mono_jit_init_version("haf-deploy-drill", "v4.0.30319");
            if (domain == IntPtr.Zero) throw new InvalidOperationException("Mono domain did not initialize");
            IntPtr assembly = mono_domain_assembly_open(domain, exe);
            if (assembly == IntPtr.Zero) throw new InvalidOperationException("Mono could not open " + exe);
            string[] command = new[] { exe }.Concat(args.Skip(2)).ToArray();
            IntPtr[] strings = command.Select(Utf8).ToArray();
            IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * (strings.Length + 1));
            try
            {
                for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
                Marshal.WriteIntPtr(argv, strings.Length * IntPtr.Size, IntPtr.Zero);
                return mono_jit_exec(domain, assembly, command.Length, argv);
            }
            finally { Marshal.FreeHGlobal(argv); foreach (IntPtr s in strings) Marshal.FreeHGlobal(s); }
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL Mono host: " + e.Message); return 1; }
    }
}
