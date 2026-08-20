namespace Etiq.Core;

/// <summary>
/// Code 39 encoder (pure, dependency-free). Each character is 9 elements
/// (5 bars, 4 spaces), 3 of them wide; characters are separated by a narrow
/// space; * start/stop guards are added automatically. Wide:narrow ratio is
/// 3:1 (the classic default — scanner-safe at any module width). code39ext
/// (full ASCII) maps unsupported characters to the standard two-character
/// shift sequences.
/// </summary>
public static class Code39
{
    // 9 chars per pattern: N = narrow, W = wide; positions alternate
    // bar,space,bar,space,... starting AND ending with a bar.
    private static readonly Dictionary<char, string> Patterns = new()
    {
        ['0'] = "NNNWWNWNN", ['1'] = "WNNWNNNNW", ['2'] = "NNWWNNNNW",
        ['3'] = "WNWWNNNNN", ['4'] = "NNNWWNNNW", ['5'] = "WNNWWNNNN",
        ['6'] = "NNWWWNNNN", ['7'] = "NNNWNNWNW", ['8'] = "WNNWNNWNN",
        ['9'] = "NNWWNNWNN", ['A'] = "WNNNNWNNW", ['B'] = "NNWNNWNNW",
        ['C'] = "WNWNNWNNN", ['D'] = "NNNNWWNNW", ['E'] = "WNNNWWNNN",
        ['F'] = "NNWNWWNNN", ['G'] = "NNNNNWWNW", ['H'] = "WNNNNWWNN",
        ['I'] = "NNWNNWWNN", ['J'] = "NNNNWWWNN", ['K'] = "WNNNNNNWW",
        ['L'] = "NNWNNNNWW", ['M'] = "WNWNNNNWN", ['N'] = "NNNNWNNWW",
        ['O'] = "WNNNWNNWN", ['P'] = "NNWNWNNWN", ['Q'] = "NNNNNNWWW",
        ['R'] = "WNNNNNWWN", ['S'] = "NNWNNNWWN", ['T'] = "NNNNWNWWN",
        ['U'] = "WWNNNNNNW", ['V'] = "NWWNNNNNW", ['W'] = "WWWNNNNNN",
        ['X'] = "NWNNWNNNW", ['Y'] = "WWNNWNNNN", ['Z'] = "NWWNWNNNN",
        ['-'] = "NWNNNNWNW", ['.'] = "WWNNNNWNN", [' '] = "NWWNNNWNN",
        ['*'] = "NWNNWNWNN", ['$'] = "NWNWNWNNN", ['/'] = "NWNWNNNWN",
        ['+'] = "NWNNNWNWN", ['%'] = "NNNWNWNWN",
    };

    private const int Wide = 3; // wide:narrow ratio 3:1

    /// <summary>Extended (full-ASCII) shift sequences for characters not in
    /// the base set. Returns null when the char is unencodable.</summary>
    private static string? Extend(char c) => c switch
    {
        >= 'a' and <= 'z' => "+" + (char)(c - 32),
        (char)0 => "%U",
        >= (char)1 and <= (char)26 => "$" + (char)('A' + c - 1),
        >= (char)27 and <= (char)31 => "%" + (char)('A' + c - 27),
        '!' => "/A", '"' => "/B", '#' => "/C", '&' => "/F", '\'' => "/G",
        '(' => "/H", ')' => "/I", '*' => "/J", ',' => "/L", ':' => "/Z",
        ';' => "%F", '<' => "%G", '=' => "%H", '>' => "%I", '?' => "%J",
        '@' => "%V", '[' => "%K", '\\' => "%L", ']' => "%M", '^' => "%N",
        '_' => "%O", '`' => "%W", '{' => "%P", '|' => "%Q", '}' => "%R",
        '~' => "%S", (char)127 => "%T",
        _ => null,
    };

    /// <summary>Alternating element widths, starting and ending with a bar
    /// (even index = bar), * guards included. extended=true maps full ASCII
    /// via shift pairs; otherwise unsupported characters throw.</summary>
    public static int[] Modules(string s, bool extended = false)
    {
        if (string.IsNullOrEmpty(s))
            throw new FormatException("empty barcode content");
        var chars = new List<char> { '*' };
        foreach (var raw in s)
        {
            char c = extended ? raw : char.ToUpperInvariant(raw);
            if (Patterns.ContainsKey(c) && c != '*')
            {
                chars.Add(c);
            }
            else if (extended && Extend(c) is { } pair)
            {
                chars.AddRange(pair);
            }
            else
            {
                throw new FormatException($"character '{raw}' not encodable in code39");
            }
        }
        chars.Add('*');

        var mods = new List<int>(chars.Count * 10);
        for (int i = 0; i < chars.Count; i++)
        {
            if (i > 0) mods.Add(1); // narrow inter-character space
            foreach (var e in Patterns[chars[i]])
                mods.Add(e == 'W' ? Wide : 1);
        }
        return mods.ToArray();
    }

    public static int TotalModules(string s, bool extended = false)
    {
        int t = 0;
        foreach (var w in Modules(s, extended)) t += w;
        return t;
    }

    public static bool CanEncode(string s, bool extended = false)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var raw in s)
        {
            char c = extended ? raw : char.ToUpperInvariant(raw);
            bool ok = (Patterns.ContainsKey(c) && c != '*') ||
                      (extended && Extend(c) is not null);
            if (!ok) return false;
        }
        return true;
    }
}
