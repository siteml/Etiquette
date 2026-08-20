using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>
/// Print-exact renderer: draws an EditorDoc onto any Graphics in WORLD
/// units (mils) — the caller sets the transform (PrintService scales mils
/// to the device). Same geometry rules as every other Etiquette renderer:
/// text baseline = SVG y (cell-ascent corrected), data-width shrink is a
/// horizontal squeeze, barcode rect is a fill-the-box TARGET, rotation via
/// transform="rotate(a x y)". Editor chrome (hatch, captions, hidden
/// layers) never prints; layers with data-print="false" are skipped.
/// </summary>
public static class LabelRenderer
{
    public static void Draw(Graphics g, EditorDoc doc,
                            IReadOnlyDictionary<string, string>? values,
                            ITextMeasurer measurer)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        string? baseDir = doc.Path is null ? null : System.IO.Path.GetDirectoryName(doc.Path);
        foreach (var o in doc.Objects)
        {
            var layer = o.Layer;
            if (layer is not null && (!layer.Visible || !layer.Printed)) continue;
            DrawObject(g, o, values, measurer, baseDir);
        }
    }

    private static void DrawObject(Graphics g, EditorObject o,
                                   IReadOnlyDictionary<string, string>? values,
                                   ITextMeasurer measurer, string? baseDir)
    {
        double rot = o.RotationDeg;
        var state = g.Save();
        if (rot != 0)
        {
            var pv = o.RotationPivot;
            g.TranslateTransform((float)pv.X, (float)pv.Y);
            g.RotateTransform((float)rot);
            g.TranslateTransform((float)-pv.X, (float)-pv.Y);
        }
        try
        {
            switch (o.Kind)
            {
                case ObjectKind.Line:
                {
                    using var pen = new Pen(Color.Black, (float)o.GetNum("stroke-width", 1));
                    g.DrawLine(pen,
                        (float)o.GetNum("x1"), (float)o.GetNum("y1"),
                        (float)o.GetNum("x2"), (float)o.GetNum("y2"));
                    break;
                }
                case ObjectKind.Box:
                {
                    var b = o.Bounds(measurer);
                    string fill = (string?)o.El.Attribute("fill") ?? "none";
                    if (fill != "none")
                        g.FillRectangle(Brushes.Black, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                    using var pen = new Pen(Color.Black, (float)o.GetNum("stroke-width", 1));
                    g.DrawRectangle(pen, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                    break;
                }
                case ObjectKind.Barcode:
                {
                    var b = o.Bounds(measurer);
                    string? field = (string?)o.El.Attribute("data-field");
                    string? content = field is not null && values is not null
                        ? values.GetValueOrDefault(field)
                        : (string?)o.El.Attribute("data-value");
                    if (string.IsNullOrEmpty(content)) break;
                    // unsupported/unencodable never prints garbage (skip)
                    DrawBarcode(g, b, (string?)o.El.Attribute("data-barcode"), content,
                        (string?)o.El.Attribute("data-ecc"),
                        (int)o.GetNum("data-columns", 0),
                        (string?)o.El.Attribute("data-logo"), baseDir,
                        (int)o.GetNum("data-logo-scale", 0));
                    break;
                }
                case ObjectKind.Text:
                {
                    string text = values is not null &&
                        (string?)o.El.Attribute("data-field") is { } f &&
                        values.TryGetValue(f, out var v) ? v : o.El.Value;
                    DrawText(g, o, text, measurer);
                    break;
                }
                // Image: not supported in the print path yet
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>THE text renderer — canvas and print both call this so they
    /// cannot drift. Handles multiline ("\n" + data-line-height), the
    /// data-width shrink squeeze (uniform, by the longest line), and box
    /// alignment (data-align within data-width, data-valign within
    /// data-height). Rotation is the CALLER's transform.</summary>
    public static void DrawText(Graphics g, EditorObject o, string text, ITextMeasurer measurer)
    {
        // data-line: a line-stack element shows ONE line of its field's
        // value (SVG-pure multiline: fixed single-line elements, variable
        // content indexed after collapse-blank-lines)
        if ((string?)o.El.Attribute("data-line") is { } dl &&
            int.TryParse(dl, out int lineNo) && lineNo >= 0 &&
            (string?)o.El.Attribute("data-field") is not null)
        {
            var vlines = text.Split('\n');
            text = lineNo < vlines.Length ? vlines[lineNo] : "";
        }
        if (text == "") return;
        double size = o.GetNum("font-size", 12);
        double origSize = size;
        var b = o.Bounds(measurer);
        var lines = text.Split('\n');
        double lineH = o.GetNum("data-line-height", size * 1.2);
        double boxW = o.GetNum("data-width", 0);
        double boxH = o.GetNum("data-height", 0);
        string fit = o.FitMode;   // none | width | box (see EditorObject)

        double natural = 0;
        var lineWs = new double[lines.Length];
        void Measure()
        {
            natural = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                lineWs[i] = measurer.Width(lines[i], size, o.FontFamily, o.Bold);
                natural = Math.Max(natural, lineWs[i]);
            }
        }
        Measure();

        double squeeze = 1;   // width fit: horizontal squeeze only
        if (fit == "width" && boxW > 0 && natural > boxW &&
            ((string?)o.El.Attribute("data-overflow") ?? "shrink") == "shrink")
            squeeze = boxW / natural;
        if (fit == "box")
        {
            // fixed box: shrink the FONT uniformly until the block fits both
            // dimensions (re-measure — glyph widths aren't perfectly linear)
            for (int iter = 0; iter < 4; iter++)
            {
                double blockH0 = size + (lines.Length - 1) * lineH;
                double s = Math.Min(1.0, Math.Min(
                    boxW > 0 ? boxW / Math.Max(natural, 0.001) : 1.0,
                    boxH > 0 ? boxH / Math.Max(blockH0, 0.001) : 1.0));
                if (s >= 0.999) break;
                size *= s;
                lineH *= s;
                Measure();
            }
        }

        using var font = new Font(o.FontFamily, (float)size,
            o.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

        // vertical placement of the line block inside the data-height box
        double blockH = size + (lines.Length - 1) * lineH;
        double yOff = ((string?)o.El.Attribute("data-valign")) switch
        {
            "middle" when boxH > blockH => (boxH - blockH) / 2,
            "bottom" when boxH > blockH => boxH - blockH,
            _ => 0,
        };

        double ascent = size * font.FontFamily.GetCellAscent(font.Style)
                             / font.FontFamily.GetEmHeight(font.Style);
        // box fit shrinks the font: re-anchor the first baseline so the text
        // block stays at the box TOP (the authored y belongs to the original
        // size); other modes keep the authored baseline exactly
        double baseY = fit == "box" && size < origSize
            ? o.GetNum("y") - origSize * 0.8 + size * 0.8
            : o.GetNum("y");
        double top0 = baseY - ascent; // GDI+ cell top for line 0

        var st = g.Save();
        // hard clip: in "none" mode any box is a WINDOW — overlong text cuts
        // off at the boundary instead of spilling over the label; width mode
        // clips when data-overflow="clip" asks for it
        bool clip = (fit == "none" && (boxW > 0 || boxH > 0))
                 || (fit == "width" && boxW > 0 &&
                     (string?)o.El.Attribute("data-overflow") == "clip");
        if (clip)
            g.SetClip(new RectangleF(
                (float)b.X, (float)(o.GetNum("y") - origSize * 0.8),
                (float)(boxW > 0 ? boxW : 1e6), (float)(boxH > 0 ? boxH : 1e6)),
                System.Drawing.Drawing2D.CombineMode.Intersect);
        if (squeeze < 1)
        {
            g.TranslateTransform((float)b.X, 0);
            g.ScaleTransform((float)squeeze, 1f);
            g.TranslateTransform((float)-b.X, 0);
        }
        string align = (string?)o.El.Attribute("data-align") ?? "left";
        double bw = boxW > 0 ? boxW : natural * squeeze;
        for (int i = 0; i < lines.Length; i++)
        {
            double lw = lineWs[i] * squeeze;
            double off = align switch
            {
                "center" => Math.Max(0, (bw - lw) / 2),
                "right" => Math.Max(0, bw - lw),
                _ => 0,
            };
            // off is a PHYSICAL offset: undo the squeeze for the coordinate
            g.DrawString(lines[i], font, Brushes.Black,
                (float)(b.X + off / squeeze),
                (float)(top0 + yOff + i * lineH),
                StringFormat.GenericTypographic);
        }
        g.Restore(st);
    }

    /// <summary>Symbologies with a real encoder today; everything else
    /// (iqr — proprietary, no open decoder to verify against) shows a
    /// placeholder in the editor and is skipped on print.</summary>
    public static bool IsImplemented(string? symbology) =>
        symbology is "code128" or "code39" or "code39ext"
                  or "qr" or "datamatrix" or "pdf417";

    /// <summary>Encode a 2D symbology to its module matrix; null when the
    /// symbology is linear/unknown or the content doesn't fit. ecc applies
    /// to qr (L|M|Q|H, default M); columns to pdf417 (1-30, default 6).</summary>
    public static bool[,]? TryEncodeMatrix(string? symbology, string content,
                                           string? ecc = null, int columns = 0,
                                           int minVersion = 1)
    {
        try
        {
            return symbology switch
            {
                "qr" => Etiq.Core.QrCode.Encode(content,
                            string.IsNullOrEmpty(ecc) ? 'M' : char.ToUpperInvariant(ecc[0]),
                            minVersion),
                "datamatrix" => Etiq.Core.DataMatrix.Encode(content),
                "pdf417" => Etiq.Core.Pdf417.Encode(content,
                            columns is >= 1 and <= 30 ? columns : 6),
                _ => null,
            };
        }
        catch { return null; }   // encoder edge case: placeholder, never garbage
    }

    /// <summary>Draw any implemented barcode into the box. Linear codes and
    /// pdf417 fill the box; qr/datamatrix keep square modules (largest
    /// square centered in the box). A qr logo overlay (data-logo) FORCES
    /// ECC level H. Returns false when unencodable — the caller decides
    /// between placeholder (canvas) and skip (print).</summary>
    public static bool DrawBarcode(Graphics g, RectD box, string? symbology,
                                   string content, string? ecc = null, int columns = 0,
                                   string? logo = null, string? baseDir = null,
                                   int logoScale = 0)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var mods = TryEncode(symbology, content);
        if (mods is not null) { DrawBars(g, box, mods); return true; }
        bool withLogo = symbology == "qr" && !string.IsNullOrEmpty(logo);
        // a logo forces ECC H AND version ≥2: v1's 21-module grid can't
        // spare a readable-size keepout even at H
        var m = TryEncodeMatrix(symbology, content, withLogo ? "H" : ecc, columns,
                                withLogo ? 2 : 1);
        if (m is null) return false;
        var drawn = DrawMatrix(g, box, m, keepSquare: symbology is "qr" or "datamatrix");
        if (withLogo) DrawQrLogo(g, drawn, m.GetLength(0), logo!, baseDir, logoScale);
        return true;
    }

    /// <summary>Render a module matrix into the box, merging horizontal
    /// runs into single fills. keepSquare centers the largest square-module
    /// rendering; otherwise the matrix stretches to the box (pdf417 rows
    /// get their height from the box, per spec row height is free).
    /// Returns the rect actually drawn.</summary>
    public static RectD DrawMatrix(Graphics g, RectD box, bool[,] m, bool keepSquare)
    {
        int mh = m.GetLength(0), mw = m.GetLength(1);
        double sx = box.W / mw, sy = box.H / mh;
        if (keepSquare) sx = sy = Math.Min(sx, sy);
        double ox = box.X + (box.W - mw * sx) / 2;
        double oy = box.Y + (box.H - mh * sy) / 2;
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                if (!m[y, x]) continue;
                int x2 = x;
                while (x2 + 1 < mw && m[y, x2 + 1]) x2++;
                g.FillRectangle(Brushes.Black,
                    (float)(ox + x * sx), (float)(oy + y * sy),
                    (float)((x2 - x + 1) * sx), (float)sy);
                x = x2;
            }
        return new(ox, oy, mw * sx, mh * sy);
    }

    /// <summary>Center logo overlay for QR: logo box = 25% of the symbol
    /// side snapped to whole modules, white keepout = logo + 1 module all
    /// around, image aspect-fit inside. 25% + ECC H + version ≥2 was
    /// locked empirically: every fuzz symbol still decodes in BOTH zbar
    /// and zxing-cpp; at 28% the smallest symbols start failing and at
    /// 30% larger ones join them. A missing/broken logo image degrades to
    /// no overlay — the code stays scannable either way.</summary>
    private static void DrawQrLogo(Graphics g, RectD symbol, int n, string logo,
                                   string? baseDir, int logoScale)
    {
        var img = LoadLogo(logo, baseDir);
        double module = symbol.W / n;
        int logoMods = Math.Max(3, (int)Math.Round(n * 0.25));
        if ((n - logoMods) % 2 != 0) logoMods++;   // keep the overlay centered on the grid
        // v1 hard cap (only reachable if the version floor is bypassed):
        // its 21-module grid tolerates no more than a 3-module logo
        while (logoMods > 3 && n < 25 && (logoMods + 2) * 10 > n * 3) logoMods -= 2;
        double ls = logoMods * module;
        double lx = symbol.X + (symbol.W - ls) / 2;
        double ly = symbol.Y + (symbol.H - ls) / 2;
        g.FillRectangle(Brushes.White,
            (float)(lx - module), (float)(ly - module),
            (float)(ls + 2 * module), (float)(ls + 2 * module));
        if (img is null) return;
        // data-logo-scale: absent = FILL — the image auto-scales to the
        // keepout limit; a number = manual % of the reserved box. Either
        // way the image is clamped INSIDE the keepout — it can never touch
        // the code's modules, so decodability stays exactly as verified.
        double limit = ls + 2 * module;
        double target = logoScale <= 0
            ? limit
            : Math.Min(ls * Math.Clamp(logoScale, 25, 130) / 100.0, limit);
        double scale = Math.Min(target / img.Width, target / img.Height);
        double w = img.Width * scale, h = img.Height * scale;
        g.DrawImage(img,
            (float)(lx + (ls - w) / 2), (float)(ly + (ls - h) / 2), (float)w, (float)h);
    }

    private static readonly Dictionary<string, Image?> LogoCache = new();

    /// <summary>Resolve a data-logo file path against the template folder.</summary>
    public static string ResolveLogoPath(string spec, string? baseDir) =>
        System.IO.Path.IsPathRooted(spec) || baseDir is null
            ? spec : System.IO.Path.Combine(baseDir, spec);

    /// <summary>Raw bytes behind a data-logo value: "etiq" = the embedded
    /// icon, "data:…;base64,…" inline, http(s) URL (8-second timeout),
    /// else a file path (relative resolves against the template folder).
    /// Null on any failure. Also feeds the editor's Embed feature.</summary>
    public static byte[]? FetchLogoBytes(string spec, string? baseDir)
    {
        try
        {
            if (spec == "etiq")
            {
                using var s = typeof(LabelRenderer).Assembly
                    .GetManifestResourceStream("etiq.icon.png");
                if (s is null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            if (spec.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = spec.IndexOf(',');
                return comma > 0 ? Convert.FromBase64String(spec[(comma + 1)..]) : null;
            }
            if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new System.Net.Http.HttpClient
                    { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("etiqedit");
                return http.GetByteArrayAsync(spec).GetAwaiter().GetResult();
            }
            string path = ResolveLogoPath(spec, baseDir);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    /// <summary>data-logo value → image (source forms per FetchLogoBytes).
    /// Failures cache as null so a bad path/URL never spams exceptions or
    /// re-downloads per paint. File sources key on the file's mtime, so
    /// editing the external logo file updates templates on next paint;
    /// web sources fetch once per session.</summary>
    private static Image? LoadLogo(string spec, string? baseDir)
    {
        string key = spec + "|" + baseDir;
        if (spec != "etiq" && !spec.StartsWith("data:") && !spec.StartsWith("http"))
        {
            try
            {
                string p = ResolveLogoPath(spec, baseDir);
                if (File.Exists(p)) key += "|" + File.GetLastWriteTimeUtc(p).Ticks;
            }
            catch { /* key without mtime */ }
        }
        if (LogoCache.TryGetValue(key, out var hit)) return hit;
        Image? img = null;
        try
        {
            var bytes = FetchLogoBytes(spec, baseDir);
            if (bytes is not null) img = Image.FromStream(new MemoryStream(bytes));
        }
        catch { img = null; }
        if (img is not null)
        {
            // trim transparent / near-white margins baked into the source —
            // otherwise the mark aspect-fits small inside its own padding
            try
            {
                var bmp = new Bitmap(img);
                img.Dispose();
                img = TrimLogo(bmp);
                if (!ReferenceEquals(img, bmp)) bmp.Dispose();
            }
            catch { /* keep the untrimmed image */ }
        }
        LogoCache[key] = img;
        return img;
    }

    /// <summary>Crop to the bounding box of visible pixels (alpha > ~6% and
    /// not near-white). Returns the input when nothing trims.</summary>
    private static Image TrimLogo(Bitmap bmp)
    {
        int x1 = bmp.Width, y1 = bmp.Height, x2 = -1, y2 = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A < 16 || (c.R > 244 && c.G > 244 && c.B > 244)) continue;
                if (x < x1) x1 = x;
                if (x > x2) x2 = x;
                if (y < y1) y1 = y;
                if (y > y2) y2 = y;
            }
        if (x2 < 0 || (x1 == 0 && y1 == 0 && x2 == bmp.Width - 1 && y2 == bmp.Height - 1))
            return bmp;   // blank or already tight
        var src = new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        var outp = new Bitmap(src.Width, src.Height);
        using var g = Graphics.FromImage(outp);
        g.DrawImage(bmp, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
        return outp;
    }

    /// <summary>Encode content for a symbology; null when the symbology is
    /// not implemented or the content is unencodable.</summary>
    public static int[]? TryEncode(string? symbology, string content)
    {
        try
        {
            return symbology switch
            {
                "code128" when Etiq.Core.Code128.CanEncode(content)
                    => Etiq.Core.Code128.Modules(content),
                "code39" when Etiq.Core.Code39.CanEncode(content)
                    => Etiq.Core.Code39.Modules(content),
                "code39ext" when Etiq.Core.Code39.CanEncode(content, extended: true)
                    => Etiq.Core.Code39.Modules(content, extended: true),
                _ => null,
            };
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Fill-the-box: modules scale so the symbol spans the whole
    /// rect (same rule as labelprint / the convention).</summary>
    public static void DrawBars(Graphics g, RectD box, int[] mods)
    {
        int total = 0;
        foreach (var m in mods) total += m;
        double mw = box.W / total;
        double x = box.X;
        for (int i = 0; i < mods.Length; i++)
        {
            double next = x + mods[i] * mw;
            if (i % 2 == 0) // even index = bar
                g.FillRectangle(Brushes.Black,
                    (float)x, (float)box.Y, (float)(next - x), (float)box.H);
            x = next;
        }
    }
}
