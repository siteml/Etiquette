namespace Etiq.Core;

/// <summary>
/// Code 128 encoder (pure, dependency-free). C# port of the proven Go
/// encoder in reference/labelprint/code128.go — keep the two in lockstep.
/// Code set C is used for all-digit even-length content of 4+ digits,
/// code set B otherwise (printable ASCII 32..126). Output is the module
/// pattern; callers scale modules to fill the design box (fill-the-box
/// semantics: the barcode rect is a TARGET, data-module-mils a MINIMUM).
/// </summary>
public static class Code128
{
    // 3 bars + 3 spaces per symbol, widths 1-4, 11 modules each; values 0-106.
    private static readonly string[] Widths =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213",
        "122312", "132212", "221213", "221312", "231212", "112232", "122132",
        "122231", "113222", "123122", "123221", "223211", "221132", "221231",
        "213212", "223112", "312131", "311222", "321122", "321221", "312212",
        "322112", "322211", "212123", "212321", "232121", "111323", "131123",
        "131321", "112313", "132113", "132311", "211313", "231113", "231311",
        "112133", "112331", "132131", "113123", "113321", "133121", "313121",
        "211331", "231131", "213113", "213311", "213131", "311123", "311321",
        "331121", "312113", "312311", "332111", "314111", "221411", "431111",
        "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114",
        "413111", "241112", "134111", "111242", "121142", "121241", "114212",
        "124112", "124211", "411212", "421112", "421211", "212141", "214121",
        "412121", "111143", "111341", "131141", "114113", "114311", "411113",
        "411311", "113141", "114131", "311141", "411131", "211412", "211214",
        "211232",
    };

    private const string Stop = "2331112"; // 13 modules incl. termination bar

    private static bool AllDigits(string s)
    {
        foreach (var c in s)
            if (c is < '0' or > '9') return false;
        return s.Length > 0;
    }

    /// <summary>Symbol values including start code and checksum.</summary>
    private static List<int> Symbols(string s)
    {
        if (string.IsNullOrEmpty(s))
            throw new FormatException("empty barcode content");
        var syms = new List<int>();
        if (AllDigits(s) && s.Length % 2 == 0 && s.Length >= 4)
        {
            syms.Add(105); // Start C
            for (int i = 0; i < s.Length; i += 2)
                syms.Add((s[i] - '0') * 10 + (s[i + 1] - '0'));
        }
        else
        {
            syms.Add(104); // Start B
            foreach (var c in s)
            {
                if (c is < (char)32 or > (char)126)
                    throw new FormatException($"barcode content has non-ASCII character '{c}'");
                syms.Add(c - 32);
            }
        }
        int sum = syms[0];
        for (int i = 1; i < syms.Count; i++) sum += i * syms[i];
        syms.Add(sum % 103);
        return syms;
    }

    /// <summary>Alternating element widths in modules, starting AND ending
    /// with a bar (even index = bar), including the stop pattern.</summary>
    public static int[] Modules(string s)
    {
        var syms = Symbols(s);
        var outList = new List<int>(syms.Count * 6 + 7);
        foreach (var v in syms)
            foreach (var ch in Widths[v])
                outList.Add(ch - '0');
        foreach (var ch in Stop)
            outList.Add(ch - '0');
        return outList.ToArray();
    }

    /// <summary>Total symbol width in modules (11/symbol + 13 stop).</summary>
    public static int TotalModules(string s)
    {
        int t = 0;
        foreach (var w in Modules(s)) t += w;
        return t;
    }

    /// <summary>True when the content can be encoded (used by previews to
    /// fall back to a placeholder instead of throwing mid-paint).</summary>
    public static bool CanEncode(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (c is < (char)32 or > (char)126) return false;
        return true;
    }
}
