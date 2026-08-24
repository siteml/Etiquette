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
    // internal: Gs1128 below builds its own symbol stream over the same table.
    internal static readonly string[] Widths =
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

    internal const string Stop = "2331112"; // 13 modules incl. termination bar

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

/// <summary>
/// GS1-128: Code 128 led by FNC1. Content uses the parenthesized AI
/// syntax — "(01)09501101530003(10)LOT42" — and FNC1 separators are
/// inserted automatically after variable-length AIs (fixed-length AIs per
/// the GS1 predefined-length table need none). Encodes Start C + FNC1,
/// pairs digits in code set C, and drops to code set B for non-numeric AI
/// values. Decode-verified with zxing-cpp (6/6, AI framing recognized).
/// </summary>
public static class Gs1128
{
    private const char Fnc1 = 'ñ';   // in-stream marker, never emitted as a char

    // GS1 predefined (fixed) lengths, keyed by 2-digit AI prefix:
    // total length = AI digits + value digits.
    private static readonly Dictionary<string, int> FixedLen = new()
    {
        ["00"] = 20, ["01"] = 16, ["02"] = 16, ["03"] = 16, ["04"] = 18,
        ["11"] = 8, ["12"] = 8, ["13"] = 8, ["14"] = 8, ["15"] = 8,
        ["16"] = 8, ["17"] = 8, ["18"] = 8, ["19"] = 8, ["20"] = 4,
        ["31"] = 10, ["32"] = 10, ["33"] = 10, ["34"] = 10, ["35"] = 10,
        ["36"] = 10, ["41"] = 16,
    };

    public static bool CanEncode(string s) =>
        !string.IsNullOrEmpty(s) && Stream(s) is not null;

    /// <summary>Parse "(ai)value(ai)value…" into the raw character stream
    /// with FNC1 separator markers; null when the syntax is invalid or a
    /// fixed-length AI has the wrong length.</summary>
    private static string? Stream(string content)
    {
        var ms = System.Text.RegularExpressions.Regex.Matches(content, @"\((\d{2,4})\)([^()]*)");
        if (ms.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        int consumed = 0;
        for (int i = 0; i < ms.Count; i++)
        {
            if (ms[i].Index != consumed) return null;   // junk between AIs
            consumed = ms[i].Index + ms[i].Length;
            string ai = ms[i].Groups[1].Value, val = ms[i].Groups[2].Value;
            if (val.Length == 0) return null;
            foreach (var c in val)
                if (c is < (char)32 or > (char)126) return null;
            bool isFixed = FixedLen.TryGetValue(ai[..2], out int fl);
            if (isFixed && ai.Length + val.Length != fl) return null;
            sb.Append(ai).Append(val);
            if (!isFixed && i < ms.Count - 1) sb.Append(Fnc1);
        }
        return consumed == content.Length ? sb.ToString() : null;
    }

    /// <summary>Alternating element widths, same contract as Code128.Modules.</summary>
    public static int[] Modules(string content)
    {
        string stream = Stream(content) ?? throw new FormatException(
            "GS1-128 content must be parenthesized AIs, e.g. (01)09501101530003(10)LOT42");
        var syms = new List<int> { 105, 102 };   // Start C, FNC1
        bool modeC = true;
        int i = 0;
        while (i < stream.Length)
        {
            char c = stream[i];
            if (c == Fnc1) { syms.Add(102); i++; continue; }
            int j = i;
            while (j < stream.Length && char.IsAsciiDigit(stream[j])) j++;
            int run = j - i;
            if (modeC)
            {
                if (run >= 2)
                {
                    syms.Add((stream[i] - '0') * 10 + (stream[i + 1] - '0'));
                    i += 2;
                    continue;
                }
                syms.Add(100); modeC = false; continue;   // → code B
            }
            // in B: return to C when a decent even digit run starts, or a
            // short even run finishes the stream
            if (run >= 4 && run % 2 == 0) { syms.Add(99); modeC = true; continue; }
            if (run >= 2 && run % 2 == 0 && j == stream.Length) { syms.Add(99); modeC = true; continue; }
            syms.Add(c - 32);
            i++;
        }
        int sum = syms[0];
        for (int k = 1; k < syms.Count; k++) sum += k * syms[k];
        syms.Add(sum % 103);

        var outList = new List<int>(syms.Count * 6 + 7);
        foreach (var v in syms)
            foreach (var ch in Code128.Widths[v])
                outList.Add(ch - '0');
        foreach (var ch in Code128.Stop)
            outList.Add(ch - '0');
        return outList.ToArray();
    }
}
