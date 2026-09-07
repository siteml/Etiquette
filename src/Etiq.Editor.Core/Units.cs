using System.Globalization;

namespace Etiq.Editor.Core;

/// <summary>What the operator sees. The document never changes: every
/// coordinate is mils (1/1000 in) as a double. These are display
/// conversions only — Inkscape's document-units model.</summary>
public enum DisplayUnit { In, Mm, Mils, Dots }

/// <summary>Mils ⇄ display-unit conversion, formatting and parsing.
/// `dotsPerMm` is needed only for <see cref="DisplayUnit.Dots"/>; with
/// none (≤ 0) dots fall back to mils so nothing ever shows garbage.</summary>
public static class Units
{
    public const double MilsPerInch = 1000.0;
    public const double MilsPerMm = 1000.0 / 25.4;      // 39.370078740157...
    public const double MilsPerPt = 1000.0 / 72;        // 13.888...

    /// <summary>Font sizes are shown in points whatever the display unit
    /// (that is how people think about type). SVG font-size is user units
    /// = mils, so 12 pt = 166.667 mils.</summary>
    public static string FormatPoints(double mils)
    {
        string s = Math.Round(mils / MilsPerPt, 1).ToString("0.#", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>Parse a font size: a bare number is POINTS; any explicit
    /// suffix (pt, mils, mm, in…) is honoured via TryParseLength.</summary>
    public static bool TryParsePoints(string? text, out double mils)
    {
        mils = 0;
        string t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        bool hasSuffix = char.IsLetter(t[^1]) || t[^1] == '"';
        if (!hasSuffix)
        {
            if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
                return false;
            mils = pt * MilsPerPt; return true;
        }
        return TryParseLength(t, DisplayUnit.Mils, out mils);
    }

    /// <summary>Mils per dot for a head density in dots/mm (8 → 4.921...).</summary>
    public static double DotPitchMils(double dotsPerMm) =>
        dotsPerMm > 0 ? 1000.0 / (25.4 * dotsPerMm) : 0;

    public static double FromMils(double mils, DisplayUnit u, double dotsPerMm = 0) => u switch
    {
        DisplayUnit.In => mils / MilsPerInch,
        DisplayUnit.Mm => mils / MilsPerMm,
        DisplayUnit.Dots when dotsPerMm > 0 => mils / DotPitchMils(dotsPerMm),
        _ => mils,
    };

    public static double ToMils(double v, DisplayUnit u, double dotsPerMm = 0) => u switch
    {
        DisplayUnit.In => v * MilsPerInch,
        DisplayUnit.Mm => v * MilsPerMm,
        DisplayUnit.Dots when dotsPerMm > 0 => v * DotPitchMils(dotsPerMm),
        _ => v,
    };

    /// <summary>Short label: in, mm, mils, dots.</summary>
    public static string Suffix(DisplayUnit u) => u switch
    {
        DisplayUnit.In => "in", DisplayUnit.Mm => "mm",
        DisplayUnit.Dots => "dots", _ => "mils",
    };

    /// <summary>Settings/attribute spelling ⇄ enum. Unknown → In.</summary>
    public static DisplayUnit Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "mm" => DisplayUnit.Mm, "mil" or "mils" => DisplayUnit.Mils,
        "dot" or "dots" => DisplayUnit.Dots, _ => DisplayUnit.In,
    };
    public static string Name(DisplayUnit u) => Suffix(u);

    /// <summary>Digits shown per unit: in 3 (1 mil), mm 2 (10 µm), mils 0,
    /// dots 0. Trailing zeros trimmed so integers stay integers.</summary>
    public static int Decimals(DisplayUnit u) => u switch
    {
        DisplayUnit.In => 3, DisplayUnit.Mm => 2, _ => 0,
    };

    /// <summary>Inspector nudge step (↑/↓) in MILS for a display unit:
    /// 0.01 in, 0.1 mm, 1 mil, 1 dot.</summary>
    public static double NudgeMils(DisplayUnit u, double dotsPerMm = 0) => u switch
    {
        DisplayUnit.In => 10, DisplayUnit.Mm => MilsPerMm / 10,
        DisplayUnit.Dots when dotsPerMm > 0 => DotPitchMils(dotsPerMm), _ => 1,
    };

    /// <summary>Format a mils value in the display unit, no suffix.
    /// Values that are not whole in a 0-decimal unit (fractional dots or
    /// mils) show up to 2 decimals rather than lying.</summary>
    public static string Format(double mils, DisplayUnit u, double dotsPerMm = 0)
    {
        double v = FromMils(mils, u, dotsPerMm);
        int d = Decimals(u);
        string fmt = d == 0
            ? (Math.Abs(v - Math.Round(v)) < 0.005 ? "0" : "0.##")
            : "0." + new string('#', d);
        // guard against "-0"
        string s = Math.Round(v, Math.Max(d, 2)).ToString(fmt, CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>Format with suffix: "1.25 in", "984.25 mils".</summary>
    public static string FormatWithSuffix(double mils, DisplayUnit u, double dotsPerMm = 0) =>
        $"{Format(mils, u, dotsPerMm)} {Suffix(u)}";

    /// <summary>Parse a typed length into mils. A bare number is in the
    /// default unit; an explicit suffix wins wherever it is typed: in, ",
    /// mm, cm, mil, mils, dot, dots, pt (1/72 in). Invariant culture; a
    /// comma decimal is accepted too.</summary>
    public static bool TryParseLength(string? text, DisplayUnit def, out double mils, double dotsPerMm = 0)
    {
        mils = 0;
        if (text is null) return false;
        string t = text.Trim().Replace(',', '.');
        if (t.Length == 0) return false;

        int i = t.Length;
        while (i > 0 && (char.IsLetter(t[i - 1]) || t[i - 1] == '"')) i--;
        string num = t[..i].Trim();
        string suf = t[i..].Trim().ToLowerInvariant();

        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return false;

        switch (suf)
        {
            case "": mils = ToMils(v, def, dotsPerMm); return true;
            case "in": case "\"": case "inch": mils = v * MilsPerInch; return true;
            case "mm": mils = v * MilsPerMm; return true;
            case "cm": mils = v * MilsPerMm * 10; return true;
            case "mil": case "mils": mils = v; return true;
            case "pt": mils = v * MilsPerPt; return true;
            case "dot": case "dots":
                if (dotsPerMm <= 0) return false;
                mils = v * DotPitchMils(dotsPerMm); return true;
            default: return false;
        }
    }
}
