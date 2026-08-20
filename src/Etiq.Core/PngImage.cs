using System.Buffers.Binary;
using System.IO.Compression;

namespace Etiq.Core;

/// <summary>
/// Minimal PNG decoder (no packages — NuGet-free by design for the recon CLI).
/// Supports non-interlaced 8-bit gray / RGB / RGBA / indexed, and 1-bit
/// gray/indexed (the .btw preview #2 flavor). Decodes to RGB24 rows.
/// </summary>
public sealed class PngImage
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public byte[] Rgb { get; private init; } = Array.Empty<byte>(); // W*H*3

    public static PngImage Decode(byte[] png)
    {
        if (png.Length < 33 || BinaryPrimitives.ReadUInt64BigEndian(png) != 0x89504E470D0A1A0AUL)
            throw new InvalidDataException("not a PNG");

        int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();

        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            int dataOff = p + 8;
            if (dataOff + len > png.Length) break;
            switch (type)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataOff));
                    h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataOff + 4));
                    bitDepth = png[dataOff + 8];
                    colorType = png[dataOff + 9];
                    interlace = png[dataOff + 12];
                    break;
                case "PLTE":
                    palette = png[dataOff..(dataOff + len)];
                    break;
                case "IDAT":
                    idat.Write(png, dataOff, len);
                    break;
            }
            if (type == "IEND") break;
            p = dataOff + len + 4; // skip CRC
        }

        if (w <= 0 || h <= 0) throw new InvalidDataException("bad IHDR");
        if (interlace != 0) throw new NotSupportedException("interlaced PNG");
        if (bitDepth != 8 && bitDepth != 1)
            throw new NotSupportedException($"bit depth {bitDepth}");

        int channels = colorType switch
        {
            0 => 1,  // gray
            2 => 3,  // rgb
            3 => 1,  // indexed
            4 => 2,  // gray+alpha
            6 => 4,  // rgba
            _ => throw new NotSupportedException($"color type {colorType}"),
        };

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = (w * channels * bitDepth + 7) / 8;
        var raw = new byte[(stride + 1) * h];
        int read = 0;
        while (read < raw.Length)
        {
            int n = inflate.Read(raw, read, raw.Length - read);
            if (n == 0) break;
            read += n;
        }

        // Un-filter (per-scanline filter byte), then expand to RGB24.
        int bpp = Math.Max(1, channels * bitDepth / 8);
        var prev = new byte[stride];
        var cur = new byte[stride];
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int rowOff = y * (stride + 1);
            byte filter = raw[rowOff];
            Array.Copy(raw, rowOff + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0;
                int b = prev[i];
                int c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = (byte)(cur[i] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"filter {filter}"),
                });
            }
            ExpandRow(cur, rgb.AsSpan(y * w * 3, w * 3), w, bitDepth, colorType, palette);
            (prev, cur) = (cur, prev);
        }
        return new PngImage { Width = w, Height = h, Rgb = rgb };

        static int Paeth(int a, int b, int c)
        {
            int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    private static void ExpandRow(byte[] row, Span<byte> outRgb, int w,
                                  int bitDepth, int colorType, byte[]? palette)
    {
        for (int x = 0; x < w; x++)
        {
            byte r, g, b;
            if (bitDepth == 1)
            {
                int bit = (row[x >> 3] >> (7 - (x & 7))) & 1;
                if (colorType == 3 && palette is not null)
                    (r, g, b) = (palette[bit * 3], palette[bit * 3 + 1], palette[bit * 3 + 2]);
                else { byte v = (byte)(bit * 255); (r, g, b) = (v, v, v); }
            }
            else switch (colorType)
            {
                case 0: { byte v = row[x]; (r, g, b) = (v, v, v); break; }
                case 2: (r, g, b) = (row[x * 3], row[x * 3 + 1], row[x * 3 + 2]); break;
                case 3:
                {
                    int idx = row[x] * 3;
                    (r, g, b) = palette is not null && idx + 2 < palette.Length
                        ? (palette[idx], palette[idx + 1], palette[idx + 2])
                        : ((byte)0, (byte)0, (byte)0);
                    break;
                }
                case 4: { byte v = row[x * 2]; (r, g, b) = (v, v, v); break; }
                case 6: (r, g, b) = (row[x * 4], row[x * 4 + 1], row[x * 4 + 2]); break;
                default: (r, g, b) = (0, 0, 0); break;
            }
            outRgb[x * 3] = r; outRgb[x * 3 + 1] = g; outRgb[x * 3 + 2] = b;
        }
    }

    /// <summary>Box-average downscale so the longest side ≤ max (thumbnail).</summary>
    public PngImage Thumbnail(int max)
    {
        if (Width <= max && Height <= max) return this;
        double scale = Math.Min((double)max / Width, (double)max / Height);
        int nw = Math.Max(1, (int)(Width * scale)), nh = Math.Max(1, (int)(Height * scale));
        var dst = new byte[nw * nh * 3];
        for (int y = 0; y < nh; y++)
        {
            int sy0 = y * Height / nh, sy1 = Math.Max(sy0 + 1, (y + 1) * Height / nh);
            for (int x = 0; x < nw; x++)
            {
                int sx0 = x * Width / nw, sx1 = Math.Max(sx0 + 1, (x + 1) * Width / nw);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int o = (sy * Width + sx) * 3;
                        r += Rgb[o]; g += Rgb[o + 1]; b += Rgb[o + 2]; n++;
                    }
                int d = (y * nw + x) * 3;
                dst[d] = (byte)(r / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(b / n);
            }
        }
        return new PngImage { Width = nw, Height = nh, Rgb = dst };
    }
}
