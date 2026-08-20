using System.IO.Compression;
using System.Text;

namespace Etiq.Core;

/// <summary>
/// Minimal PDF writer for the gallery contact sheet (no packages — NuGet-free
/// by design). Pages carry Flate-compressed RGB image XObjects plus Helvetica
/// captions. Not a general-purpose PDF library; just enough for `etiq gallery`.
/// </summary>
public sealed class PdfWriter
{
    private sealed record Placed(PngImage Image, double X, double Y, double W, double H);
    private sealed record Caption(string Text, double X, double Y, double Size);
    private sealed class Page
    {
        public List<Placed> Images { get; } = new();
        public List<Caption> Captions { get; } = new();
    }

    private readonly double _pageW, _pageH;
    private readonly List<Page> _pages = new();
    private Page? _cur;

    /// <param name="pageW">page width in points (1/72 in)</param>
    /// <param name="pageH">page height in points</param>
    public PdfWriter(double pageW, double pageH) { _pageW = pageW; _pageH = pageH; }

    public void NewPage() => _pages.Add(_cur = new Page());

    /// <summary>x,y = top-left in points from page top-left (converted internally).</summary>
    public void DrawImage(PngImage img, double x, double y, double w, double h)
        => (_cur ?? throw new InvalidOperationException("NewPage first"))
           .Images.Add(new Placed(img, x, _pageH - y - h, w, h));

    public void DrawText(string text, double x, double y, double size)
        => (_cur ?? throw new InvalidOperationException("NewPage first"))
           .Captions.Add(new Caption(text, x, _pageH - y - size, size));

    public void Save(string path)
    {
        var objs = new List<byte[]>();          // 1-based object bodies
        int Add(byte[] body) { objs.Add(body); return objs.Count; }
        int AddText(string body) => Add(Encoding.Latin1.GetBytes(body));

        // Object 1: catalog, 2: pages tree, 3: Helvetica font (filled in later).
        AddText(""); AddText(""); AddText(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var pageIds = new List<int>();
        foreach (var page in _pages)
        {
            var xobjRefs = new StringBuilder();
            var content = new StringBuilder();
            int i = 0;
            foreach (var pl in page.Images)
            {
                int imgId = Add(ImageXObject(pl.Image));
                string name = $"Im{i++}";
                xobjRefs.Append($"/{name} {imgId} 0 R ");
                content.Append($"q {F(pl.W)} 0 0 {F(pl.H)} {F(pl.X)} {F(pl.Y)} cm /{name} Do Q\n");
            }
            foreach (var c in page.Captions)
                content.Append($"BT /F1 {F(c.Size)} Tf {F(c.X)} {F(c.Y)} Td ({Esc(c.Text)}) Tj ET\n");

            var cbytes = Encoding.Latin1.GetBytes(content.ToString());
            int contentId = Add(Encoding.Latin1.GetBytes(
                $"<< /Length {cbytes.Length} >>\nstream\n").Concat(cbytes)
                .Concat(Encoding.Latin1.GetBytes("\nendstream")).ToArray());
            int pageId = AddText(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(_pageW)} {F(_pageH)}] " +
                $"/Resources << /Font << /F1 3 0 R >> /XObject << {xobjRefs}>> >> " +
                $"/Contents {contentId} 0 R >>");
            pageIds.Add(pageId);
        }

        objs[0] = Encoding.Latin1.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objs[1] = Encoding.Latin1.GetBytes(
            $"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] >>");

        using var fs = File.Create(path);
        void W(string s) => fs.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        var offsets = new long[objs.Count + 1];
        for (int n = 1; n <= objs.Count; n++)
        {
            offsets[n] = fs.Position;
            W($"{n} 0 obj\n");
            fs.Write(objs[n - 1]);
            W("\nendobj\n");
        }
        long xref = fs.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        for (int n = 1; n <= objs.Count; n++)
            W($"{offsets[n]:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static byte[] ImageXObject(PngImage img)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(img.Rgb);
        var data = ms.ToArray();
        var head = Encoding.Latin1.GetBytes(
            $"<< /Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
            $"/Length {data.Length} >>\nstream\n");
        return head.Concat(data).Concat(Encoding.Latin1.GetBytes("\nendstream")).ToArray();
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
