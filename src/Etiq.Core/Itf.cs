namespace Etiq.Core;

/// <summary>
/// Interleaved 2 of 5 (ITF) encoder, narrow:wide = 1:3 — digits only,
/// encoded in pairs (first digit = bars, second = spaces). The itf14
/// symbology flavor: exactly 13 digits get the GS1 check digit appended
/// automatically; any other odd length is zero-padded on the left, per the
/// usual ITF convention. Output contract matches Code128.Modules:
/// alternating element widths, even index = bar. Decode-verified with
/// zxing-cpp (5/5 including GS1-14 check-digit cases).
/// </summary>
public static class Itf
{
    private static readonly string[] Pat =
    {
        "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw",
        "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn",
    };

    public static bool CanEncode(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>13 digits → append the GS1 check digit (ITF-14); other odd
    /// lengths are zero-padded on the left to reach an even digit count.</summary>
    public static string Normalize(string s) =>
        s.Length == 13 ? s + Gs1CheckDigit(s)
        : s.Length % 2 == 1 ? "0" + s
        : s;

    /// <summary>Standard GS1 mod-10 check digit (weights 3/1 from the right).</summary>
    public static char Gs1CheckDigit(string digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
            sum += (digits[digits.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>Alternating element widths in modules, starting AND ending
    /// with a bar (even index = bar), including start/stop patterns.</summary>
    public static int[] Modules(string s)
    {
        if (!CanEncode(s))
            throw new FormatException("ITF encodes digits only");
        s = Normalize(s);
        var mods = new List<int> { 1, 1, 1, 1 };            // start: 4 narrow
        for (int i = 0; i < s.Length; i += 2)
        {
            string a = Pat[s[i] - '0'], b = Pat[s[i + 1] - '0'];
            for (int j = 0; j < 5; j++)
            {
                mods.Add(a[j] == 'w' ? 3 : 1);              // bar
                mods.Add(b[j] == 'w' ? 3 : 1);              // space
            }
        }
        mods.Add(3); mods.Add(1); mods.Add(1);              // stop: wide bar, narrow space, narrow bar
        return mods.ToArray();
    }
}
