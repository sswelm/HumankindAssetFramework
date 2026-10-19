using System.Collections.Generic;
using System.Text;
using Xunit;

// The JPEG-retention guard (review of #193): the glTF subset passes, an RGB JPEG - Adobe transform 0, or 'R','G','B' component IDs
// with no Adobe segment - stays with Blender, as do the headers the glTF exporters never write.
public class PortableJpegHeaderTests
{
    static byte[] Jpeg(int components, byte[] ids = null, int? adobe = null, byte sof = 0xc0, byte precision = 8, bool sosFirst = false, int width = 16)
    {
        var b = new List<byte> { 0xff, 0xd8 };
        if (sosFirst) b.AddRange(new byte[] { 0xff, 0xda, 0, 2 });
        if (adobe != null) { b.AddRange(new byte[] { 0xff, 0xee, 0, 14 }); b.AddRange(Encoding.ASCII.GetBytes("Adobe")); b.AddRange(new byte[] { 0, 100, 0, 0, 0, 0, (byte)adobe.Value }); }
        b.AddRange(new byte[] { 0xff, 0xe0, 0, 4, 0, 0 });   // a short APP0 on the way, skipped by its length
        b.AddRange(new byte[] { 0xff, sof, 0, (byte)(8 + 3 * components), precision, 0, 16, (byte)(width >> 8), (byte)width, (byte)components });
        for (int i = 0; i < components; i++) b.AddRange(new byte[] { ids != null ? ids[i] : (byte)(i + 1), 0x11, 0 });
        b.AddRange(new byte[] { 0xff, 0xda, 0, 2 });
        return b.ToArray();
    }
    static readonly byte[] Rgb = { (byte)'R', (byte)'G', (byte)'B' };

    [Theory] [InlineData(0, false)] [InlineData(1, true)]
    public void Adobe_colour_transform_is_honoured_after_the_frame_header(int transform, bool expected)
    {
        var jpeg = new List<byte>(Jpeg(3));
        var app14 = new List<byte> { 0xff, 0xee, 0, 14 };
        app14.AddRange(Encoding.ASCII.GetBytes("Adobe"));
        app14.AddRange(new byte[] { 0, 100, 0, 0, 0, 0, (byte)transform });
        jpeg.InsertRange(jpeg.Count - 4, app14);
        Assert.Equal(expected, BlenderMaterialExport.PortableJpegHeader(jpeg.ToArray()));
    }

    [Fact] public void Grayscale_baseline_passes() => Assert.True(BlenderMaterialExport.PortableJpegHeader(Jpeg(1)));
    [Fact] public void YCbCr_baseline_passes() => Assert.True(BlenderMaterialExport.PortableJpegHeader(Jpeg(3)));
    [Fact] public void YCbCr_progressive_passes() => Assert.True(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, sof: 0xc2)));
    [Fact] public void Adobe_transform_1_passes_even_with_RGB_ids() => Assert.True(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, Rgb, adobe: 1)));
    [Fact] public void Adobe_transform_0_is_RGB_and_stays_with_Blender() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, adobe: 0)));
    [Theory] [InlineData(2)] [InlineData(255)]
    public void Unsupported_Adobe_colour_transforms_stay_with_Blender(int transform) => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, adobe: transform)));
    [Fact] public void RGB_component_ids_without_Adobe_stay_with_Blender() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, Rgb)));
    [Fact] public void Four_components_fail() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(4)));
    [Fact] public void Twelve_bit_precision_fails() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, precision: 12)));
    [Fact] public void Lossless_SOF3_fails() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, sof: 0xc3)));
    [Fact] public void Arithmetic_SOF9_fails() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, sof: 0xc9)));
    [Fact] public void Zero_width_fails() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, width: 0)));
    [Fact] public void A_scan_before_the_frame_fails() => Assert.False(BlenderMaterialExport.PortableJpegHeader(Jpeg(3, sosFirst: true)));
    [Fact] public void Not_a_jpeg_fails() => Assert.False(BlenderMaterialExport.PortableJpegHeader(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0, 0, 0, 0 }));
    [Fact] public void Null_and_short_fail() { Assert.False(BlenderMaterialExport.PortableJpegHeader(null)); Assert.False(BlenderMaterialExport.PortableJpegHeader(new byte[] { 0xff, 0xd8 })); }
}
