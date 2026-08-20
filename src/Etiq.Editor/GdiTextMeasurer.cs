using Etiq.Editor.Core;

namespace Etiq.Editor;

/// <summary>Real GDI text metrics for the editor core — the WYSIWYG-parity
/// half of the WinForms decision (canvas measures like the driver prints).</summary>
public sealed class GdiTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly Bitmap _scratch = new(1, 1);
    private readonly Graphics _g;

    public GdiTextMeasurer()
    {
        _g = Graphics.FromImage(_scratch);
        _g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    }

    public double Width(string text, double fontSize, string family, bool bold)
    {
        if (text.Length == 0) return 0;
        using var font = new Font(family, (float)fontSize,
            bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        return _g.MeasureString(text, font, PointF.Empty,
            StringFormat.GenericTypographic).Width;
    }

    public void Dispose()
    {
        _g.Dispose();
        _scratch.Dispose();
    }
}
