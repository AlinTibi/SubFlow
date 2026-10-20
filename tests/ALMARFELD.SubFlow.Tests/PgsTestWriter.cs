using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>
/// Writes synthetic Blu-ray PGS (.sup) subtitle streams: text is rendered with a system font, so
/// OCR tests need no copyrighted media. 16 gray levels, white text, transparent background.
/// </summary>
public static class PgsTestWriter
{
    public const int VideoWidth = 1920, VideoHeight = 1080;

    public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    public static void Write(string path, IReadOnlyList<Cue> cues)
    {
        using var output = File.Create(path);
        var composition = 0;
        foreach (var cue in cues)
        {
            var (width, height, indexes) = Render(cue.Text);
            var x = (VideoWidth - width) / 2;
            var y = VideoHeight - height - 60;
            var start = Pts(cue.Start);
            // Display set that shows the subtitle.
            Segment(output, start, 0x16, Pcs(composition++, epochStart: true, objects: [(0, x, y)]));
            Segment(output, start, 0x17, Wds(x, y, width, height));
            Segment(output, start, 0x14, Palette());
            Segment(output, start, 0x15, Ods(width, height, EncodeRle(indexes, width, height)));
            Segment(output, start, 0x80, []);
            // Display set that clears it.
            var end = Pts(cue.End);
            Segment(output, end, 0x16, Pcs(composition++, epochStart: false, objects: []));
            Segment(output, end, 0x17, Wds(x, y, width, height));
            Segment(output, end, 0x80, []);
        }
    }

    /// <summary>Renders text centred, white on transparent, as palette indexes 0–15.</summary>
    public static (int Width, int Height, byte[] Indexes) Render(string text)
    {
        using var font = new Font("Arial", 44, FontStyle.Regular, GraphicsUnit.Pixel);
        Size size;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            size = Size.Ceiling(g.MeasureString(text, font, VideoWidth - 40, StringFormat.GenericTypographic));
        var width = size.Width + 20;
        var height = size.Height + 16;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 8, width, height - 8), format);
        }
        var indexes = new byte[width * height];
        for (var py = 0; py < height; py++)
        for (var px = 0; px < width; px++)
            indexes[py * width + px] = (byte)(bitmap.GetPixel(px, py).R / 17);
        return (width, height, indexes);
    }

    public static List<byte> EncodeRle(byte[] indexes, int width, int height)
    {
        var rle = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            var x = 0;
            while (x < width)
            {
                var color = indexes[y * width + x];
                var run = 1;
                while (x + run < width && indexes[y * width + x + run] == color && run < 16383) run++;
                if (color == 0)
                {
                    if (run < 64) rle.AddRange([0, (byte)run]);
                    else rle.AddRange([0, (byte)(0x40 | (run >> 8)), (byte)(run & 0xFF)]);
                }
                else if (run <= 2)
                {
                    for (var i = 0; i < run; i++) rle.Add(color);
                }
                else if (run < 64) rle.AddRange([0, (byte)(0x80 | run), color]);
                else rle.AddRange([0, (byte)(0xC0 | (run >> 8)), (byte)(run & 0xFF), color]);
                x += run;
            }
            rle.AddRange([0, 0]);
        }
        return rle;
    }

    private static uint Pts(TimeSpan t) => (uint)(t.TotalMilliseconds * 90);

    private static void Segment(Stream output, uint pts, byte type, IReadOnlyCollection<byte> body)
    {
        var header = new byte[13];
        header[0] = (byte)'P';
        header[1] = (byte)'G';
        WriteUInt32(header, 2, pts);
        header[10] = type;
        header[11] = (byte)(body.Count >> 8);
        header[12] = (byte)body.Count;
        output.Write(header);
        output.Write(body.ToArray());
    }

    private static byte[] Pcs(int composition, bool epochStart, IReadOnlyList<(int Id, int X, int Y)> objects)
    {
        var b = new List<byte>();
        AddUInt16(b, VideoWidth);
        AddUInt16(b, VideoHeight);
        b.Add(0x10); // 23.976 fps
        AddUInt16(b, composition);
        b.Add(epochStart ? (byte)0x80 : (byte)0x00);
        b.Add(0x00); // not a palette-only update
        b.Add(0x00); // palette id
        b.Add((byte)objects.Count);
        foreach (var (id, x, y) in objects)
        {
            AddUInt16(b, id);
            b.Add(0); // window id
            b.Add(0); // not cropped
            AddUInt16(b, x);
            AddUInt16(b, y);
        }
        return b.ToArray();
    }

    private static byte[] Wds(int x, int y, int width, int height)
    {
        var b = new List<byte> { 1, 0 };
        AddUInt16(b, x);
        AddUInt16(b, y);
        AddUInt16(b, width);
        AddUInt16(b, height);
        return b.ToArray();
    }

    private static byte[] Palette()
    {
        var b = new List<byte> { 0, 0 };
        for (var i = 0; i < 16; i++) b.AddRange([(byte)i, (byte)(i * 17), 128, 128, (byte)(i * 17)]);
        return b.ToArray();
    }

    private static byte[] Ods(int width, int height, List<byte> rle)
    {
        var b = new List<byte>();
        AddUInt16(b, 0); // object id
        b.Add(0); // version
        b.Add(0xC0); // first and last fragment
        var length = rle.Count + 4;
        b.AddRange([(byte)(length >> 16), (byte)(length >> 8), (byte)length]);
        AddUInt16(b, width);
        AddUInt16(b, height);
        b.AddRange(rle);
        return b.ToArray();
    }

    private static void AddUInt16(List<byte> b, int value) => b.AddRange([(byte)(value >> 8), (byte)value]);

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
