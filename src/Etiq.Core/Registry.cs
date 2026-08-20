using System.Text.Json;

namespace Etiq.Core;

/// <summary>
/// Minimal media + printer registry (roadmap Phase 3): plain data files
/// describing the shop fleet and the label stocks actually run — NOT a
/// gLabels-style universal stock database. Feeds validate's
/// printer-feasibility checks (FeasibilityChecker).
/// </summary>
public sealed class PrinterDef
{
    public string Name { get; set; } = "";
    public int Dpi { get; set; }
    public int WidthMils { get; set; }              // max print width
    public string Path { get; set; } = "driver";    // driver | zpl | tpcl | bpac
    /// <summary>Dot pitch in mils (1000/dpi).</summary>
    public double DotMils => Dpi > 0 ? 1000.0 / Dpi : 0;
}

public sealed class MediaDef
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "diecut";    // diecut | continuous
    public int WidthMils { get; set; }
    public int HeightMils { get; set; }             // diecut only
    public int MinLenMils { get; set; }             // continuous only
    public int MaxLenMils { get; set; }             // continuous only
}

public sealed class Registry
{
    public Dictionary<string, PrinterDef> Printers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaDef> Media { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Load printers.json / media.json (each a JSON array) from a
    /// directory; either file may be absent.</summary>
    public static Registry Load(string dir)
    {
        var r = new Registry();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        string pf = Path.Combine(dir, "printers.json");
        if (File.Exists(pf))
            foreach (var p in JsonSerializer.Deserialize<List<PrinterDef>>(File.ReadAllText(pf), opts) ?? new())
            {
                if (p.Name == "" || p.Dpi <= 0 || p.WidthMils <= 0)
                    throw new InvalidDataException($"printers.json: '{p.Name}' needs name, dpi, widthMils");
                if (!r.Printers.TryAdd(p.Name, p))
                    throw new InvalidDataException($"printers.json: duplicate '{p.Name}'");
            }
        string mf = Path.Combine(dir, "media.json");
        if (File.Exists(mf))
            foreach (var m in JsonSerializer.Deserialize<List<MediaDef>>(File.ReadAllText(mf), opts) ?? new())
            {
                if (m.Name == "" || m.WidthMils <= 0)
                    throw new InvalidDataException($"media.json: '{m.Name}' needs name, widthMils");
                if (!r.Media.TryAdd(m.Name, m))
                    throw new InvalidDataException($"media.json: duplicate '{m.Name}'");
            }
        return r;
    }
}

/// <summary>
/// Printer-feasibility checks for a template against registry entries —
/// the piece `etiq validate` was waiting on (docs/convention.md PRINTING:
/// dot-snapping rule, AIAG minimums; roadmap Phase 3 registry item).
/// </summary>
public static class FeasibilityChecker
{
    /// <summary>Check one template against one printer. Returns findings in
    /// the same shape as TemplateValidator (Error blocks, Warning advises).</summary>
    public static List<Finding> Check(EtiqTemplate t, PrinterDef p)
    {
        var findings = new List<Finding>();
        void Err(string code, string msg) => findings.Add(new(Severity.Error, code, $"[{p.Name}] {msg}"));
        void Warn(string code, string msg) => findings.Add(new(Severity.Warning, code, $"[{p.Name}] {msg}"));

        // label physical size vs printer print width — either feed orientation
        int? labelW = PhysicalMils(t.WidthAttr);
        int? labelH = PhysicalMils(t.HeightAttr);
        if (labelW is int lw)
        {
            bool asIs = lw <= p.WidthMils;
            bool rotated = labelH is int lh && lh <= p.WidthMils;
            if (!asIs && rotated)
                Warn("printer-fit", $"label {lw} mils wide exceeds print width {p.WidthMils} mils — requires rotated feed ({labelH} mils edge first)");
            else if (!asIs)
                Err("printer-fit", $"label {lw}x{labelH} mils exceeds printer print width {p.WidthMils} mils in both orientations");
        }

        // barcode module width vs dot pitch
        double dot = p.DotMils;
        foreach (var b in t.Barcodes)
        {
            if (b.ModuleMils is not double mm || dot <= 0) continue;
            if (mm < dot)
                Err("module-dots", $"barcode '{b.Symbology}': module {mm} mils < one dot ({dot:0.##} mils at {p.Dpi} dpi) — cannot be printed");
            else if (mm < 2 * dot)
                Warn("module-dots", $"barcode '{b.Symbology}': module {mm} mils is a single-dot module at {p.Dpi} dpi — fragile; prefer ≥ {2 * dot:0.##} mils");
            else
            {
                double snapped = Math.Max(1, Math.Round(mm / dot)) * dot;   // nearest dot multiple
                double dev = Math.Abs(mm - snapped) / mm;
                if (dev > 0.20)
                    Warn("module-snap", $"barcode '{b.Symbology}': module {mm} mils snaps to {snapped:0.##} mils at {p.Dpi} dpi ({dev:P0} deviation)");
            }
        }
        return findings;
    }

    /// <summary>Parse a physical dimension attr ("6in", "152mm", "15.2cm") to mils.</summary>
    public static int? PhysicalMils(string? attr)
    {
        if (attr is null) return null;
        double mul;
        string num;
        if (attr.EndsWith("in")) { mul = 1000; num = attr[..^2]; }
        else if (attr.EndsWith("mm")) { mul = 1000 / 25.4; num = attr[..^2]; }
        else if (attr.EndsWith("cm")) { mul = 10000 / 25.4; num = attr[..^2]; }
        else return null;
        return double.TryParse(num, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v * mul) : null;
    }
}
