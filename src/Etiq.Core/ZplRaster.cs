using System.Text;

namespace Etiq.Core;

/// <summary>
/// Raw-ZPL raster fast path (roadmap Phase 3): wrap a 1-bit monochrome
/// raster of the rendered label in ^GFA inside ^XA..^XZ, for direct RAW
/// writes to Zebra queues (203/300 dpi). Pure C#, no packages.
///
/// Input is RGB24 (PngImage.Rgb layout) at the printer's native DPI —
/// the caller renders at queue DPI first (DEVMODE-pinned), same rule as
/// the driver path.
/// </summary>
public static class ZplRaster
{
    /// <summary>
    /// RGB24 → 1-bit rows, MSB-first, 1 = black dot.
    /// threshold: luma below this prints black (0-255). Barcodes/text on
    /// label stock are hard black/white, so fixed threshold (no dithering)
    /// is correct here — dithering would break barcode edges.
    /// </summary>
    public static byte[] ToMono(byte[] rgb, int width, int height, int threshold = 128)
    {
        int stride = (width + 7) / 8;
        var mono = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 3;
                // ITU-R BT.601 luma, integer math
                int luma = (299 * rgb[o] + 587 * rgb[o + 1] + 114 * rgb[o + 2]) / 1000;
                if (luma < threshold)
                    mono[y * stride + x / 8] |= (byte)(0x80 >> (x & 7));
            }
        return mono;
    }

    /// <summary>
    /// Build a complete ZPL job: ^XA ^FO0,0 ^GFA,... ^FS ^PQn ^XZ.
    /// compress=true uses Zebra's hex run-length scheme (G..Y/g..z counts +
    /// per-row ',' = rest-of-row-white / '!' = rest-of-row-black shortcuts),
    /// understood by every ZPL II printer incl. the shop's ZT230.
    /// </summary>
    public static string BuildJob(byte[] mono, int width, int height,
                                  int copies = 1, bool compress = true)
    {
        int stride = (width + 7) / 8;
        if (mono.Length != stride * height)
            throw new ArgumentException($"mono length {mono.Length} != stride {stride} x height {height}");
        string data = compress ? CompressHex(mono, stride) : PlainHex(mono);
        var sb = new StringBuilder();
        sb.Append("^XA");
        sb.Append("^FO0,0");
        sb.Append($"^GFA,{mono.Length},{mono.Length},{stride},");
        sb.Append(data);
        sb.Append("^FS");
        if (copies != 1) sb.Append($"^PQ{copies}");
        sb.Append("^XZ");
        return sb.ToString();
    }

    private static string PlainHex(byte[] mono) => Convert.ToHexString(mono);

    /// <summary>
    /// Zebra ^GF hex compression: repeat counts before a hex digit
    /// (G-Y = 1-19 units of 1, g-z = 20-400 in units of 20, combinable,
    /// count applies to the NEXT single hex digit), ',' = fill rest of the
    /// row with 0, '!' = fill rest of the row with F, ':' = repeat previous
    /// row. Typical labels (lots of white, repeated scanlines) shrink ~20x.
    /// </summary>
    private static string CompressHex(byte[] mono, int stride)
    {
        var sb = new StringBuilder();
        int rows = mono.Length / stride;
        string? prevRow = null;
        for (int y = 0; y < rows; y++)
        {
            string row = Convert.ToHexString(mono, y * stride, stride);
            if (row == prevRow) { sb.Append(':'); continue; }
            prevRow = row;
            int i = 0;
            while (i < row.Length)
            {
                char c = row[i];
                int run = 1;
                while (i + run < row.Length && row[i + run] == c) run++;
                bool restOfRow = i + run == row.Length;
                if (restOfRow && c == '0') { sb.Append(','); break; }
                if (restOfRow && c == 'F') { sb.Append('!'); break; }
                AppendRun(sb, run, c);
                i += run;
            }
        }
        return sb.ToString();

        static void AppendRun(StringBuilder sb, int run, char c)
        {
            if (run == 1) { sb.Append(char.ToUpperInvariant(c)); return; }
            while (run >= 20)
            {
                int twenties = Math.Min(run / 20, 20); // g=20 .. z=400
                sb.Append((char)('f' + twenties));
                run -= twenties * 20;
                if (run >= 20) continue;
            }
            if (run > 0) sb.Append((char)('F' + run)); // G=1 .. Y=19
            sb.Append(char.ToUpperInvariant(c));
        }
    }

    /// <summary>
    /// Decode the data portion of a ^GFA back to raw bytes — used by tests
    /// to prove the compressed stream round-trips, and handy for debugging
    /// captured jobs from other systems.
    /// </summary>
    public static byte[] DecodeGfData(string data, int stride, int rows)
    {
        var outp = new byte[stride * rows];
        int row = 0, col = 0; // col in hex digits (2 per byte)
        int digitsPerRow = stride * 2;
        int pending = 0;
        var prevRowStart = -1;
        void PutDigit(char hex)
        {
            int v = Convert.ToInt32(hex.ToString(), 16);
            int byteIdx = row * stride + col / 2;
            outp[byteIdx] |= (byte)((col & 1) == 0 ? v << 4 : v);
            if (++col == digitsPerRow) { prevRowStart = row * stride; row++; col = 0; }
        }
        foreach (char c in data)
        {
            if (c is >= 'G' and <= 'Y') { pending += c - 'F'; }
            else if (c is >= 'g' and <= 'z') { pending += (c - 'f') * 20; }
            else if (c == ',') { row++; col = 0; pending = 0; prevRowStart = (row - 1) * stride; }
            else if (c == '!')
            {
                int fill = digitsPerRow - col;   // col wraps to 0 when the row
                for (int k = 0; k < fill; k++)   // completes — count, don't test col
                    PutDigit('F');
                pending = 0;
            }
            else if (c == ':')
            {
                if (prevRowStart >= 0)
                    Array.Copy(outp, prevRowStart, outp, row * stride, stride);
                prevRowStart = row * stride; row++; col = 0; pending = 0;
            }
            else if (Uri.IsHexDigit(c))
            {
                int n = Math.Max(1, pending);
                for (int k = 0; k < n; k++) PutDigit(c);
                pending = 0;
            }
        }
        return outp;
    }
}
