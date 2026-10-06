using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

/// <summary>Integration-shaped tests for the GLB reader and writer beyond the happy round trip (the round-trip
/// diversity PR, 2026-10-02): a CORRUPTION SWEEP - the full fixture truncated at every 4-byte boundary and every
/// byte of its JSON chunk mutated four ways - must be refused by name or read, never crash (no IndexOutOfRange,
/// NullReference, Overflow, InvalidCast, ArgumentOutOfRange from inside), and whatever the reader accepts the writer
/// either writes or refuses by name; the writer's bytes do not depend on the thread's CULTURE (a Dutch machine
/// formats 0,5); eight THREADS reading and writing at once get the sequential answer.</summary>
public class GlbRobustnessTests
{
    static readonly Type[] CrashTypes = { typeof(IndexOutOfRangeException), typeof(NullReferenceException), typeof(OverflowException), typeof(InvalidCastException), typeof(ArgumentOutOfRangeException), typeof(DivideByZeroException), typeof(ArithmeticException), typeof(InvalidOperationException), typeof(OutOfMemoryException), typeof(StackOverflowException) };

    /// <summary>Reads the bytes; a refusal must be an InvalidDataException or a JSON parse error (the two named
    /// ways to say no), never one of the crash types. Returns the model when it read, null when refused.</summary>
    static HafModel ReadOrRefuse(byte[] bytes, string what)
    {
        try { return GlbReader.Read(bytes); }
        catch (InvalidDataException) { return null; }
        catch (Newtonsoft.Json.JsonException) { return null; }
        catch (EndOfStreamException) { return null; }
        catch (Exception e)
        {
            // anything else is a crash: the crash types by name, and whatever a future change throws that is not a refusal
            Assert.Fail($"{what}: the reader threw {e.GetType().Name} ({e.Message}) - a refusal is an InvalidDataException by name, not a crash{(CrashTypes.Contains(e.GetType()) ? "" : " (an unexpected type)")}");
            return null;
        }
    }

    static void WriteOrRefuse(HafModel m, string what)
    {
        try { var bytes = GlbWriter.Write(m); Assert.NotNull(GlbReader.Read(bytes)); }
        catch (InvalidDataException) { }
        catch (Exception e) { Assert.Fail($"{what}: the writer threw {e.GetType().Name} ({e.Message}) on a model the reader accepted - it refuses by name or writes"); }
    }

    [Fact]
    public void Every_truncation_of_the_fixture_is_refused_by_name_or_read_never_a_crash()
    {
        var full = GlbReaderTests.FullGlb();
        int read = 0, refused = 0;
        for (int len = 0; len < full.Length; len += 4)
        {
            var cut = new byte[len]; Buffer.BlockCopy(full, 0, cut, 0, len);
            var m = ReadOrRefuse(cut, $"truncated to {len} of {full.Length} bytes");
            if (m == null) refused++; else { read++; WriteOrRefuse(m, $"truncated to {len}"); }
        }
        Assert.True(refused > 0, "no truncation was refused");
        Assert.Equal(0, read);   // the container declares its length and every chunk its own: a shorter file is never whole
    }

    [Fact]
    public void Every_byte_of_the_JSON_chunk_mutated_four_ways_is_refused_by_name_or_read_never_a_crash()
    {
        var full = GlbReaderTests.FullGlb();
        int jsonLen = BitConverter.ToInt32(full, 12);
        int read = 0, refused = 0;
        foreach (byte with in new byte[] { (byte)'9', (byte)'0', (byte)'"', (byte)'}' })
            for (int i = 20; i < 20 + jsonLen; i++)
            {
                if (full[i] == with) continue;
                var mut = (byte[])full.Clone(); mut[i] = with;
                var m = ReadOrRefuse(mut, $"JSON byte {i - 20} ('{(char)full[i]}') replaced by '{(char)with}'");
                if (m == null) refused++; else { read++; WriteOrRefuse(m, $"JSON byte {i - 20} replaced by '{(char)with}'"); }
            }
        Assert.True(refused > read, $"{refused} refused, {read} read: a mutated JSON should mostly be refused");
    }

    [Fact]
    public void Every_byte_of_the_BIN_chunk_set_to_0xFF_is_read_or_refused_never_a_crash()
    {
        // binary data corrupted is still a readable file (floats become other floats) unless an index now points past
        // its vertices or a weight is not a number: the reader reads it, or refuses by name, and the writer does the same
        var full = GlbReaderTests.FullGlb();
        int jsonLen = BitConverter.ToInt32(full, 12);
        int binStart = 20 + jsonLen + 8, binLen = BitConverter.ToInt32(full, 20 + jsonLen);
        int read = 0, refused = 0;
        for (int i = binStart; i < binStart + binLen; i++)
        {
            var mut = (byte[])full.Clone(); mut[i] = 0xFF;
            var m = ReadOrRefuse(mut, $"BIN byte {i - binStart} set to 0xFF");
            if (m == null) refused++; else { read++; WriteOrRefuse(m, $"BIN byte {i - binStart} set to 0xFF"); }
        }
        Assert.True(read > 0, "no corrupted BIN was readable at all");
        Assert.True(refused > 0, "no corrupted BIN was refused (an index past its vertices must be)");
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version-1")]
    [InlineData("length-short")]
    [InlineData("length-long")]
    [InlineData("json-chunk-type")]
    [InlineData("bin-before-json")]
    [InlineData("two-json-chunks")]
    [InlineData("chunk-past-end")]
    [InlineData("empty")]
    [InlineData("header-only")]
    public void A_broken_container_is_refused_by_name(string flaw)
    {
        var full = GlbReaderTests.FullGlb();
        int jsonLen = BitConverter.ToInt32(full, 12);
        byte[] bytes = (byte[])full.Clone();
        switch (flaw)
        {
            case "magic": bytes[0] = (byte)'X'; break;
            case "version-1": BitConverter.GetBytes(1u).CopyTo(bytes, 4); break;
            case "length-short": BitConverter.GetBytes((uint)(full.Length - 8)).CopyTo(bytes, 8); break;
            case "length-long": BitConverter.GetBytes((uint)(full.Length + 8)).CopyTo(bytes, 8); break;
            case "json-chunk-type": BitConverter.GetBytes(0x12345678u).CopyTo(bytes, 16); break;
            case "bin-before-json":
            {
                int binLen = BitConverter.ToInt32(full, 20 + jsonLen);
                var swapped = new byte[full.Length];
                Buffer.BlockCopy(full, 0, swapped, 0, 12);
                Buffer.BlockCopy(full, 20 + jsonLen, swapped, 12, 8 + binLen);
                Buffer.BlockCopy(full, 12, swapped, 20 + binLen, 8 + jsonLen);
                bytes = swapped; break;
            }
            case "two-json-chunks":
            {
                var twice = new byte[full.Length + 8 + jsonLen];
                Buffer.BlockCopy(full, 0, twice, 0, 20 + jsonLen);
                Buffer.BlockCopy(full, 12, twice, 20 + jsonLen, 8 + jsonLen);
                Buffer.BlockCopy(full, 20 + jsonLen, twice, 28 + 2 * jsonLen, full.Length - 20 - jsonLen);
                BitConverter.GetBytes((uint)twice.Length).CopyTo(twice, 8);
                bytes = twice; break;
            }
            case "chunk-past-end": BitConverter.GetBytes((uint)(jsonLen + 1000)).CopyTo(bytes, 12); break;
            case "empty": bytes = new byte[0]; break;
            case "header-only": bytes = new byte[12]; Buffer.BlockCopy(full, 0, bytes, 0, 12); break;
        }
        var m = ReadOrRefuse(bytes, flaw);
        // a second JSON chunk, or the BIN first: the reader takes the first of each kind, which is still the same file - allowed; the rest must be refused
        if (flaw == "two-json-chunks" || flaw == "bin-before-json") { if (m != null) WriteOrRefuse(m, flaw); }
        else Assert.Null(m);
    }

    [Fact]
    public void A_gltf_whose_sidecars_are_missing_or_malformed_is_refused_by_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), "haf-gltf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "nobin.gltf"), "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":12,\"uri\":\"missing.bin\"}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":12}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"type\":\"VEC3\",\"count\":1,\"min\":[0,0,0],\"max\":[0,0,0]}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}");
            Assert.Throws<InvalidDataException>(() => GlbReader.Read(Path.Combine(dir, "nobin.gltf")));
            File.WriteAllText(Path.Combine(dir, "baddata.gltf"), "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":12,\"uri\":\"data:application/octet-stream;base64,!!!notbase64\"}]}");
            var ex = Record.Exception(() => GlbReader.Read(Path.Combine(dir, "baddata.gltf")));
            Assert.NotNull(ex); Assert.DoesNotContain(ex.GetType(), CrashTypes);
            File.WriteAllText(Path.Combine(dir, "notjson.gltf"), "this is not JSON");
            ex = Record.Exception(() => GlbReader.Read(Path.Combine(dir, "notjson.gltf")));
            Assert.NotNull(ex); Assert.DoesNotContain(ex.GetType(), CrashTypes);
            File.WriteAllText(Path.Combine(dir, "array.gltf"), "[1,2,3]");
            ex = Record.Exception(() => GlbReader.Read(Path.Combine(dir, "array.gltf")));
            Assert.NotNull(ex); Assert.DoesNotContain(ex.GetType(), CrashTypes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_written_bytes_do_not_depend_on_the_threads_culture()
    {
        var m = GlbReader.Read(GlbReaderTests.FullGlb()); m.Meshes[0].Primitives[0].MorphTargets = 0;
        m.Nodes[0].Translation = new[] { 0.5, -1.25, 1e-7 }; m.Materials[0].BaseColorFactor = new double[] { 0.5f, 0.25f, 0.125f, 1f }; m.Animations[0].Samplers[0].Times[1] = 0.75f;
        var was = CultureInfo.CurrentCulture;
        byte[] invariant, dutch, turkish;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; invariant = GlbWriter.Write(m);
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL"); dutch = GlbWriter.Write(m);
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); turkish = GlbWriter.Write(m);   // the dotless i: a ToUpper on "image/png" would change it
            // and reading under a comma-decimal culture gives the same model
            var back = GlbReader.Read(dutch);
            Assert.Null(HafModelDiff.FirstDifference(m, back));
        }
        finally { CultureInfo.CurrentCulture = was; }
        Assert.Equal(invariant, dutch); Assert.Equal(invariant, turkish);
        Assert.Contains("0.5,-1.25,1E-07", Encoding.UTF8.GetString(invariant));   // the number format the file carries
    }

    [Fact]
    public void Eight_threads_reading_and_writing_at_once_get_the_sequential_answer()
    {
        var glb = GlbReaderTests.FullGlb();
        var reference = GlbReader.Read(glb); reference.Meshes[0].Primitives[0].MorphTargets = 0;
        var expected = GlbWriter.Write(reference);
        var errors = new List<string>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                var m = GlbReader.Read(glb); m.Meshes[0].Primitives[0].MorphTargets = 0;
                var bytes = GlbWriter.Write(m);
                if (!bytes.SequenceEqual(expected)) lock (errors) errors.Add($"iteration {i}: the bytes differ");
                var again = GlbReader.Read(bytes);
                string diff = HafModelDiff.FirstDifference(reference, again);
                if (diff != null) lock (errors) errors.Add($"iteration {i}: {diff}");
            }
            catch (Exception e) { lock (errors) errors.Add($"iteration {i}: {e.GetType().Name}: {e.Message}"); }
        });
        Assert.Empty(errors);
    }
}
