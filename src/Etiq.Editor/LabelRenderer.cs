using Etiq.Editor.Core;
using System.Xml.Linq;

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
                        (int)o.GetNum("data-logo-scale", 0),
                        (string?)o.El.Attribute("data-dmshape") == "rect",
                        (string?)o.El.Attribute("data-hri"),
                        (string?)o.El.Attribute("data-symsize"),
                        HriStyle.From(o),
                        ModuleLock(o));
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
                case ObjectKind.Image:
                    DrawImage(g, o, baseDir);   // unreadable source = nothing printed
                    break;
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
        double size = o.GetNum("font-size", 12);
        double origSize = size;
        var b = o.Bounds(measurer);
        // inverse text ("MASTER LOAD" white on black): data-plate="black"
        // draws a black plate the size of the text's box first; fill="white"
        // draws the glyphs white. Both are plain SVG / plain attributes, so
        // Inkscape round-trips them and every print path (GDI driver, ZPL
        // raster) gets them from this one renderer. The plate is the
        // element's box (design bounds / data-width box), so it stays when
        // the bound value is empty — an inverted field with nothing in it is
        // a black bar, not a hole in the label.
        if ((string?)o.El.Attribute("data-plate") == "black")
            g.FillRectangle(Brushes.Black, (float)b.X, (float)b.Y, (float)b.W, (float)b.H);
        if (text == "") return;
        var ink = string.Equals((string?)o.El.Attribute("fill"), "white", StringComparison.OrdinalIgnoreCase)
            ? Brushes.White : Brushes.Black;
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
            g.DrawString(lines[i], font, ink,
                (float)(b.X + off / squeeze),
                (float)(top0 + yOff + i * lineH),
                StringFormat.GenericTypographic);
        }
        g.Restore(st);
    }

    /// <summary>Symbologies with a real encoder today; anything else (e.g.
    /// a legacy template naming a symbology we dropped, like iqr) shows a
    /// placeholder in the editor and is skipped on print.</summary>
    public static bool IsImplemented(string? symbology) =>
        symbology is "code128" or "code39" or "code39ext" or "gs1-128" or "itf14"
                  or "qr" or "rmqr" or "aztec" or "datamatrix" or "pdf417";

    /// <summary>The exact rect the renderer draws for a qr/datamatrix
    /// element inside its current box (largest square modules, centered) —
    /// the target for the tight-box (data-tight="1") snap. Null when not a
    /// 2D-square symbology, the box is degenerate, or it is already tight.
    /// Unknown content (unresolved field): falls back to squaring the box.</summary>
    public static RectD? TightBarcodeRect(EditorObject o, ITextMeasurer measurer)
    {
        string sym = (string?)o.El.Attribute("data-barcode") ?? "";
        if (sym is not ("qr" or "datamatrix" or "aztec" or "rmqr")) return null;
        var b = o.Bounds(measurer);
        if (b.W <= 0 || b.H <= 0) return null;
        // same fallback chain the canvas draws with, so the snap target
        // matches the symbol on screen even for field-bound elements
        string content = (string?)o.El.Attribute("data-value")
            ?? (string?)o.El.Attribute("data-field") ?? "SAMPLE";
        bool withLogo = sym == "qr" && !string.IsNullOrEmpty((string?)o.El.Attribute("data-logo"));
        var m = string.IsNullOrEmpty(content) ? null
            : TryEncodeMatrix(sym, content,
                withLogo ? "H" : (string?)o.El.Attribute("data-ecc"),
                0, withLogo ? 2 : 1,
                (string?)o.El.Attribute("data-dmshape") == "rect",
                b.W / b.H,    // same aspect rule as the draw, so the snap matches
                (string?)o.El.Attribute("data-symsize"));
        double w, h;
        if (m is null)
        {
            w = h = Math.Min(b.W, b.H);
        }
        else
        {
            int mh = m.GetLength(0), mw = m.GetLength(1);
            double s = Math.Min(b.W / mw, b.H / mh);
            w = mw * s; h = mh * s;
        }
        if (Math.Abs(w - b.W) < 0.01 && Math.Abs(h - b.H) < 0.01) return null;
        return new(b.X + (b.W - w) / 2, b.Y + (b.H - h) / 2, w, h);
    }

    /// <summary>Encode a 2D symbology to its module matrix; null when the
    /// symbology is linear/unknown or the content doesn't fit. ecc applies
    /// to qr (L|M|Q|H, default M); columns to pdf417 (1-30, default 6).</summary>
    public static bool[,]? TryEncodeMatrix(string? symbology, string content,
                                           string? ecc = null, int columns = 0,
                                           int minVersion = 1, bool dmRect = false,
                                           double dmAspect = 0, string? symSize = null)
    {
        // data-symsize — one attribute, read per symbology (docs/convention.md):
        //   qr          version 1-40 ("7"), or "45" modules → the version with that side
        //   datamatrix  square size "32" (pad to at least) or a rectangle "12x36" (forced)
        //   aztec       modules per side "37" (pad to at least)
        //   rmqr        exact version "11x59" (falls back to best fit if the content won't fit)
        //   pdf417      minimum rows "20" (columns come from data-columns)
        int n = int.TryParse(symSize, out int parsed) ? parsed : 0;
        try
        {
            return symbology switch
            {
                "qr" => Etiq.Core.QrCode.Encode(content,
                            string.IsNullOrEmpty(ecc) ? 'M' : char.ToUpperInvariant(ecc[0]),
                            Math.Max(minVersion, n <= 40 ? n : (n - 17) / 4)),   // modules → version
                "datamatrix" => Etiq.Core.DataMatrix.Encode(content, dmRect, dmAspect, symSize),
                // rmqr picks its version by the box aspect too; ecc M|H
                "rmqr" => Etiq.Core.Rmqr.Encode(content, ecc == "H", dmAspect, symSize),
                "aztec" => Etiq.Core.Aztec.Encode(content, n),
                "pdf417" => Etiq.Core.Pdf417.Encode(content,
                            columns is >= 1 and <= 30 ? columns : 6, -1, n),
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
                                   int logoScale = 0, bool dmRect = false,
                                   string? hri = null, string? symSize = null,
                                   HriStyle? hriStyle = null, double moduleLock = 0)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var mods = TryEncode(symbology, content);
        if (mods is not null)
        {
            // HRI (data-hri below|above): reserve a text band inside the
            // box so the overall element footprint never changes. Band =
            // auto (25 % of the box, ≤150 mils) or the pinned font size;
            // data-hri-gap adds clear space between bars and text.
            var barBox = box;
            if (hri is "below" or "above")
            {
                var hs = hriStyle ?? HriStyle.Default;
                double band = hs.Size > 0 ? hs.Size / HriFontOfBand : Math.Min(box.H * 0.25, 150);
                double take = Math.Min(box.H, band + Math.Max(0, hs.Gap));
                barBox = hri == "below"
                    ? new RectD(box.X, box.Y, box.W, box.H - take)
                    : new RectD(box.X, box.Y + take, box.W, box.H - take);
                DrawHri(g, hri == "below"
                        ? new RectD(box.X, box.Bottom - band, box.W, band)
                        : new RectD(box.X, box.Y, box.W, band),
                    HriText(symbology, content), hs);
            }
            // exact X-dim (data-module-lock): bars at moduleLock per module,
            // centered; too wide for the box → fill as usual (the footprint
            // is the promise, the inspector says it does not fit)
            if (moduleLock > 0)
            {
                int total = 0;
                foreach (var mw in mods) total += mw;
                double w = total * moduleLock;
                if (w <= barBox.W + 0.01)
                    barBox = new RectD(barBox.X + (barBox.W - w) / 2, barBox.Y, w, barBox.H);
            }
            DrawBars(g, barBox, mods);
            return true;
        }
        bool withLogo = symbology == "qr" && !string.IsNullOrEmpty(logo);
        // a logo forces ECC H AND version ≥2: v1's 21-module grid can't
        // spare a readable-size keepout even at H
        var m = TryEncodeMatrix(symbology, content, withLogo ? "H" : ecc, columns,
                                withLogo ? 2 : 1, dmRect,
                                box.H > 0 ? box.W / box.H : 0,    // box aspect picks the rect size
                                symSize);                         // data-symsize: pinned symbol size
        if (m is null) return false;
        var mbox = box;
        if (moduleLock > 0)
        {
            // exact module: rows×cols at moduleLock, centered; fall back to
            // fill-the-box when it does not fit
            double w = m.GetLength(1) * moduleLock, h = m.GetLength(0) * moduleLock;
            if (w <= box.W + 0.01 && h <= box.H + 0.01)
                mbox = new RectD(box.X + (box.W - w) / 2, box.Y + (box.H - h) / 2, w, h);
        }
        var drawn = DrawMatrix(g, mbox, m,
            keepSquare: symbology is "qr" or "datamatrix" or "aztec" or "rmqr");
        if (withLogo) DrawQrLogo(g, drawn, m.GetLength(0), logo!, baseDir, logoScale);
        return true;
    }

    /// <summary>data-module-lock="1" + data-module-mils → the exact module
    /// size to draw at (user units); 0 = fill the box (default).</summary>
    public static double ModuleLock(EditorObject o) =>
        (string?)o.El.Attribute("data-module-lock") == "1" ? o.GetNum("data-module-mils", 0) : 0;

    /// <summary>HRI presentation (docs/convention.md): data-hri-size (font
    /// em height in user units, 0 = auto from the box), data-hri-align
    /// left|center|right, data-hri-font family, data-hri-gap (clear space
    /// between bars and text, user units).</summary>
    public sealed record HriStyle(double Size, string Align, string Font, double Gap)
    {
        public static readonly HriStyle Default = new(0, "center", "Arial", 0);
        public static HriStyle From(EditorObject o) => new(
            o.GetNum("data-hri-size", 0),
            (string?)o.El.Attribute("data-hri-align") ?? "center",
            (string?)o.El.Attribute("data-hri-font") is { Length: > 0 } f ? f : "Arial",
            o.GetNum("data-hri-gap", 0));
    }

    /// <summary>What the human-readable line SHOWS: for itf14 the digits
    /// actually encoded (check digit / padding included — the whole point
    /// of HRI); gs1-128 and the rest show the content as typed (GS1 HRI
    /// convention keeps the (AI) parentheses).</summary>
    public static string HriText(string? symbology, string content) =>
        symbology == "itf14" && Etiq.Core.Itf.CanEncode(content)
            ? Etiq.Core.Itf.Normalize(content)
            : content;

    /// <summary>Font em height as a fraction of the HRI band height.</summary>
    private const double HriFontOfBand = 0.78;

    /// <summary>Single-line HRI text in its band: font size from the band
    /// (or pinned), aligned left / center / right, squeezed horizontally
    /// when the box is narrower than the text (same squeeze rule as
    /// data-width text).</summary>
    private static void DrawHri(Graphics g, RectD band, string text, HriStyle hs)
    {
        if (text.Length == 0 || band.W <= 0 || band.H <= 0) return;
        Font font;
        try { font = new Font(hs.Font, (float)(band.H * HriFontOfBand), GraphicsUnit.Pixel); }
        catch { font = new Font("Arial", (float)(band.H * HriFontOfBand), GraphicsUnit.Pixel); }
        using (font)
        {
            var sz = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            if (sz.Width <= 0) return;
            float squeeze = sz.Width > band.W ? (float)(band.W / sz.Width) : 1f;
            double ax = hs.Align switch
            {
                "left" => band.X + sz.Width * squeeze / 2,
                "right" => band.Right - sz.Width * squeeze / 2,
                _ => band.X + band.W / 2,
            };
            var st = g.Save();
            g.TranslateTransform((float)ax, (float)(band.Y + band.H / 2));
            g.ScaleTransform(squeeze, 1f);
            g.DrawString(text, font, Brushes.Black,
                -sz.Width / 2, -sz.Height / 2, StringFormat.GenericTypographic);
            g.Restore(st);
        }
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
    // ---- <image> objects (docs/convention.md "Images") ----

    private static readonly Dictionary<string, Image?> ImageCache = new();

    /// <summary>The image source: SVG 2 `href`, else Inkscape/SVG 1.1
    /// `xlink:href`. Same source forms as data-logo (path relative to the
    /// template, absolute path, http(s), data: URI).</summary>
    public static string? ImageHref(EditorObject o) =>
        (string?)o.El.Attribute("href")
        ?? (string?)o.El.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href");

    /// <summary>Load an image source untrimmed, cached like logos (file
    /// sources key on mtime; failures cache as null).</summary>
    public static Image? LoadImage(string spec, string? baseDir)
    {
        string key = spec + "|" + baseDir;
        if (!spec.StartsWith("data:") && !spec.StartsWith("http"))
        {
            try
            {
                string p = ResolveLogoPath(spec, baseDir);
                if (File.Exists(p)) key += "|" + File.GetLastWriteTimeUtc(p).Ticks;
            }
            catch { /* key without mtime */ }
        }
        if (ImageCache.TryGetValue(key, out var hit)) return hit;
        Image? img = null;
        try
        {
            var bytes = spec == "etiq" ? null : FetchLogoBytes(spec, baseDir);
            if (bytes is not null) img = Image.FromStream(new MemoryStream(bytes));
        }
        catch { img = null; }
        ImageCache[key] = img;
        return img;
    }

    /// <summary>Draw an &lt;image&gt; into its box: aspect-fit centered
    /// (SVG's default preserveAspectRatio), or stretched when
    /// preserveAspectRatio="none". data-threshold="N" (1-99, % luminance)
    /// thresholds to pure black/white — a logo on a monochrome thermal
    /// printer without the driver's dither noise; absent = the picture as
    /// is (colour printers print colour, mono drivers dither). False when the source can't be read — the caller
    /// decides what a missing image looks like (canvas: placeholder;
    /// print: nothing).</summary>
    public static bool DrawImage(Graphics g, EditorObject o, string? baseDir)
    {
        string? spec = ImageHref(o);
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var img = LoadImage(spec, baseDir);
        if (img is null) return false;
        var b = o.Bounds();
        if (b.W <= 0 || b.H <= 0) return false;
        RectangleF dest;
        if ((string?)o.El.Attribute("preserveAspectRatio") == "none")
            dest = new RectangleF((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
        else
        {
            double s = Math.Min(b.W / img.Width, b.H / img.Height);
            double w = img.Width * s, h = img.Height * s;
            dest = new RectangleF((float)(b.X + (b.W - w) / 2), (float)(b.Y + (b.H - h) / 2), (float)w, (float)h);
        }
        var prevInterp = g.InterpolationMode;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        try
        {
            double thr = o.GetNum("data-threshold", 0);
            if (thr is > 0 and < 100)
            {
                // luminance → threshold: every pixel ends black or white
                using var attrs = new System.Drawing.Imaging.ImageAttributes();
                attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                    new[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                    new[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                    new[] { 0f, 0, 0, 1, 0 },
                    new[] { 0f, 0, 0, 0, 1 },
                }));
                attrs.SetThreshold((float)(thr / 100));
                g.DrawImage(img, Rectangle.Round(dest), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
            }
            else
                g.DrawImage(img, dest);
        }
        finally { g.InterpolationMode = prevInterp; }
        return true;
    }

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
                "gs1-128" when Etiq.Core.Gs1128.CanEncode(content)
                    => Etiq.Core.Gs1128.Modules(content),
                "itf14" when Etiq.Core.Itf.CanEncode(content)
                    => Etiq.Core.Itf.Modules(content),
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
