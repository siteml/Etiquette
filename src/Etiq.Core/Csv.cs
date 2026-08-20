using System.Text;

namespace Etiq.Core;

/// <summary>Dependency-free RFC 4180 CSV reader for batch-merge record
/// sets. First row = headers; rows become case-insensitive column→value
/// dictionaries. Handles quoted fields, embedded commas/quotes/newlines,
/// and both CRLF and LF.</summary>
public static class Csv
{
    public static List<Dictionary<string, string>> ReadFile(string path) =>
        Read(File.ReadAllText(path));

    public static List<Dictionary<string, string>> Read(string text)
    {
        var rows = ParseRows(text);
        var outp = new List<Dictionary<string, string>>();
        if (rows.Count == 0) return outp;
        var headers = rows[0];
        for (int r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count == 1 && rows[r][0] == "") continue;   // trailing blank line
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Count; c++)
                d[headers[c]] = c < rows[r].Count ? rows[r][c] : "";
            outp.Add(d);
        }
        return outp;
    }

    private static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false; i++; continue;
                }
                field.Append(ch); i++; continue;
            }
            switch (ch)
            {
                case '"': quoted = true; i++; break;
                case ',': row.Add(field.ToString()); field.Clear(); i++; break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    goto case '\n';
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                    i++; break;
                default: field.Append(ch); i++; break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
