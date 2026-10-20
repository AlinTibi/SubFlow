using System.Buffers.Binary;

namespace ALMARFELD.SubFlow.Core.Subtitles;

/// <summary>
/// One displayed PGS subtitle as an 8-bit grayscale image prepared for OCR: dark text on a white
/// background (bright, opaque subtitle pixels become dark; outlines and transparency become light).
/// </summary>
public sealed record PgsBitmap(TimeSpan Start, TimeSpan End, int Width, int Height, byte[] Gray);

/// <summary>
/// Minimal reader for Blu-ray PGS (<c>.sup</c>) subtitle streams: decodes presentation, palette and
/// object segments into one image per displayed subtitle with its timing. No video is decoded.
/// </summary>
public static class PgsReader
{
    private const byte PaletteSegment = 0x14, ObjectSegment = 0x15, PresentationSegment = 0x16, EndSegment = 0x80;

    private sealed record Placement(int ObjectId, int X, int Y);

    private sealed class PgsObject
    {
        public int Width, Height;
        public List<byte> Data = new();
    }

    public static IReadOnlyList<PgsBitmap> Read(string path) => Read(File.ReadAllBytes(path));

    public static IReadOnlyList<PgsBitmap> Read(ReadOnlySpan<byte> data)
    {
        var result = new List<PgsBitmap>();
        var palettes = new Dictionary<int, (byte Y, byte A)[]>();
        var objects = new Dictionary<int, PgsObject>();
        // State of the display set being read.
        long pts = 0;
        int paletteId = 0;
        bool paletteOnly = false;
        List<Placement>? placements = null;
        // The subtitle currently on screen.
        PgsBitmap? open = null;

        var pos = 0;
        while (pos + 13 <= data.Length)
        {
            if (data[pos] != 'P' || data[pos + 1] != 'G')
            {
                pos++; // resynchronise on damaged data
                continue;
            }
            var segPts = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 2)..]);
            var type = data[pos + 10];
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 11)..]);
            var body = pos + 13 + size <= data.Length ? data.Slice(pos + 13, size) : ReadOnlySpan<byte>.Empty;
            pos += 13 + size;
            if (body.IsEmpty && type != EndSegment) continue;

            switch (type)
            {
                case PresentationSegment when body.Length >= 11:
                {
                    pts = segPts;
                    var state = body[7];
                    paletteOnly = body[8] == 0x80;
                    paletteId = body[9];
                    if ((state & 0x80) != 0) objects.Clear(); // epoch start
                    var count = body[10];
                    placements = new List<Placement>(count);
                    var p = 11;
                    for (var i = 0; i < count && p + 8 <= body.Length; i++)
                    {
                        var id = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
                        var cropped = (body[p + 3] & 0x40) != 0;
                        var x = BinaryPrimitives.ReadUInt16BigEndian(body[(p + 4)..]);
                        var y = BinaryPrimitives.ReadUInt16BigEndian(body[(p + 6)..]);
                        placements.Add(new Placement(id, x, y));
                        p += cropped ? 16 : 8;
                    }
                    break;
                }
                case PaletteSegment when body.Length >= 2:
                {
                    var id = body[0];
                    if (!palettes.TryGetValue(id, out var palette))
                        palettes[id] = palette = new (byte, byte)[256];
                    for (var p = 2; p + 5 <= body.Length; p += 5)
                        palette[body[p]] = (body[p + 1], body[p + 4]);
                    break;
                }
                case ObjectSegment when body.Length >= 4:
                {
                    var id = BinaryPrimitives.ReadUInt16BigEndian(body);
                    var first = (body[3] & 0x80) != 0;
                    if (first)
                    {
                        if (body.Length < 11) break;
                        var obj = new PgsObject
                        {
                            Width = BinaryPrimitives.ReadUInt16BigEndian(body[7..]),
                            Height = BinaryPrimitives.ReadUInt16BigEndian(body[9..]),
                        };
                        obj.Data.AddRange(body[11..].ToArray());
                        objects[id] = obj;
                    }
                    else if (objects.TryGetValue(id, out var obj))
                    {
                        obj.Data.AddRange(body[4..].ToArray());
                    }
                    break;
                }
                case EndSegment:
                {
                    if (placements is null) break;
                    var time = TimeSpan.FromMilliseconds(pts / 90.0);
                    // A palette-only update (fades) keeps the subtitle that is already shown.
                    if (paletteOnly && open is not null && placements.Count > 0) { placements = null; break; }

                    if (open is not null)
                    {
                        result.Add(open with { End = time > open.Start ? time : open.Start + TimeSpan.FromMilliseconds(500) });
                        open = null;
                    }
                    if (placements.Count > 0 && palettes.TryGetValue(paletteId, out var palette))
                    {
                        var image = Compose(placements, objects, palette);
                        if (image is not null) open = new PgsBitmap(time, time, image.Value.Width, image.Value.Height, image.Value.Gray);
                    }
                    placements = null;
                    break;
                }
            }
        }
        if (open is not null) result.Add(open with { End = open.Start + TimeSpan.FromSeconds(4) });
        return result;
    }

    private static (int Width, int Height, byte[] Gray)? Compose(List<Placement> placements, Dictionary<int, PgsObject> objects, (byte Y, byte A)[] palette)
    {
        var parts = new List<(Placement Place, PgsObject Obj)>();
        foreach (var p in placements)
            if (objects.TryGetValue(p.ObjectId, out var o) && o.Width > 0 && o.Height > 0) parts.Add((p, o));
        if (parts.Count == 0) return null;

        var left = parts.Min(p => p.Place.X);
        var top = parts.Min(p => p.Place.Y);
        var width = parts.Max(p => p.Place.X + p.Obj.Width) - left;
        var height = parts.Max(p => p.Place.Y + p.Obj.Height) - top;
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000) return null;

        var gray = new byte[width * height];
        Array.Fill(gray, (byte)255);
        foreach (var (place, obj) in parts)
        {
            var indexes = DecodeRle(obj.Data, obj.Width, obj.Height);
            for (var y = 0; y < obj.Height; y++)
            for (var x = 0; x < obj.Width; x++)
            {
                var (luma, alpha) = palette[indexes[y * obj.Width + x]];
                if (alpha == 0) continue;
                // Bright opaque text → dark; dark outline → light; blended by alpha over white.
                var value = 255 - luma * alpha / 255;
                gray[(place.Y - top + y) * width + (place.X - left + x)] = (byte)value;
            }
        }
        return (width, height, gray);
    }

    /// <summary>Decodes PGS run-length encoded palette indexes.</summary>
    internal static byte[] DecodeRle(List<byte> data, int width, int height)
    {
        var pixels = new byte[width * height];
        int x = 0, y = 0, i = 0;
        while (i < data.Count && y < height)
        {
            var b = data[i++];
            int run, color;
            if (b != 0) { run = 1; color = b; }
            else
            {
                if (i >= data.Count) break;
                var flags = data[i++];
                if (flags == 0) { x = 0; y++; continue; } // end of line
                var length = flags & 0x3F;
                if ((flags & 0x40) != 0 && i < data.Count) length = (length << 8) | data[i++];
                color = (flags & 0x80) != 0 && i < data.Count ? data[i++] : 0;
                run = length;
            }
            for (var k = 0; k < run && x < width; k++, x++) pixels[y * width + x] = (byte)color;
        }
        return pixels;
    }
}
