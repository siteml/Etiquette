namespace Etiq.Core;

/// <summary>
/// Keep-a-Changelog slicing for the update dialog. A user two releases
/// behind must see EVERY release between theirs and the offered one — a
/// skipped release's notes are the only warning they get about its
/// behavior changes — and nothing else: no [Unreleased], no versions
/// newer than the one being installed (main can be ahead of the release).
/// </summary>
public static class Changelog
{
    /// <summary>The sections with current &lt; version ≤ latest, in file
    /// order, preceded by the file's preamble (everything before the first
    /// "## [" heading). Sections whose heading isn't a parseable
    /// "## [x.y.z] …" are dropped. If NO section falls in range (parse
    /// failure, foreign file) the whole text comes back unchanged — a
    /// full changelog beats an empty dialog.</summary>
    public static string Between(string md, Version current, Version latest)
    {
        // System.Version: a missing component is -1, so 0.10.2 ≠ 0.10.2.0
        // — normalize both bounds the same way ParseHeading does
        current = Norm(current);
        latest = Norm(latest);
        var lines = md.Split('\n');
        var sb = new System.Text.StringBuilder();
        bool keep = true, any = false;   // preamble is kept
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var v = ParseHeading(line);
                keep = v is not null && v > current && v <= latest;
                any |= keep;
            }
            if (keep) sb.Append(line).Append('\n');
        }
        return any ? sb.ToString().TrimEnd('\n') + "\n" : md;
    }

    /// <summary>"## [0.10.2] — 2026-09-08" → 0.10.2; "## [Unreleased]" →
    /// null. Missing components are normalized so 0.10 == 0.10.0.</summary>
    public static Version? ParseHeading(string heading)
    {
        int a = heading.IndexOf('['), b = heading.IndexOf(']');
        if (a < 0 || b <= a + 1) return null;
        string tag = heading[(a + 1)..b].Trim().TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var v)) return null;
        return Norm(v);
    }

    private static Version Norm(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
