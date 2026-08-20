using System.Xml.Linq;

namespace Etiq.Core;

/// <summary>A reusable metadata fragment: fields/maps/lists that took real
/// work to build once (an address block, a serial scheme) packaged for
/// insertion into any template. File = *.snippet.xml with an etiq:snippet
/// root; children are ordinary convention elements.</summary>
public sealed record Snippet(string Name, string? Description, XElement El);

public static class SnippetLibrary
{
    /// <summary>Load snippets from the given directories (missing ones are
    /// skipped): typically the app's own "snippets" folder (shipped) plus
    /// %APPDATA%\Etiquette\snippets (user-saved).</summary>
    public static List<Snippet> Load(params string[] dirs)
    {
        var outp = new List<Snippet>();
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var f in Directory.GetFiles(dir, "*.snippet.xml").OrderBy(x => x))
            {
                try
                {
                    var el = XElement.Load(f);
                    if (el.Name != EtiqTemplate.Ns + "snippet") continue;
                    outp.Add(new Snippet(
                        (string?)el.Attribute("name") ?? System.IO.Path.GetFileNameWithoutExtension(f),
                        (string?)el.Attribute("description"), el));
                }
                catch { /* unreadable snippet file: skip, never crash the editor */ }
            }
        return outp;
    }

    /// <summary>Deep-clone the snippet's elements for insertion. Names that
    /// collide with existing declarations (per nameTaken) are suffixed
    /// (Name2, Name3, …) and every INTERNAL reference — seg ref=/map=,
    /// field list=/switch-on= — is rewritten to match, so the inserted
    /// bundle stays self-consistent. etiq:row attributes are data, never
    /// rewritten.</summary>
    public static List<XElement> Materialize(Snippet s, Func<string, bool> nameTaken)
    {
        var clones = s.El.Elements().Select(e => new XElement(e)).ToList();
        var rename = new Dictionary<string, string>();
        foreach (var e in clones)
        {
            if ((string?)e.Attribute("name") is not { } nm) continue;
            if (!nameTaken(nm) && !rename.ContainsValue(nm)) continue;
            string cand = nm;
            int n = 2;
            while (nameTaken(cand) || rename.ContainsValue(cand)) cand = nm + n++;
            rename[nm] = cand;
        }
        if (rename.Count == 0) return clones;
        string[] refAttrs = { "name", "ref", "map", "list", "switch-on" };
        foreach (var e in clones)
            foreach (var el in e.DescendantsAndSelf())
            {
                if (el.Name == EtiqTemplate.Ns + "row") continue; // row attrs = data
                foreach (var a in refAttrs)
                    if ((string?)el.Attribute(a) is { } v && rename.TryGetValue(v, out var nv))
                        el.SetAttributeValue(a, nv);
            }
        return clones;
    }

    /// <summary>Package a field WITH everything it transitively references
    /// (ref'd fields, maps, lists — including through variants) as a
    /// snippet element ready to save.</summary>
    public static XElement Package(string snippetName, XElement fieldEl, XElement etiqLabel)
    {
        var ns = EtiqTemplate.Ns;
        var include = new List<XElement>();
        var seenNames = new HashSet<string>();
        void AddField(XElement f)
        {
            string? nm = (string?)f.Attribute("name");
            if (nm is null || !seenNames.Add("f:" + nm)) return;
            include.Add(f);
            // referenced fields via segs (direct + variants) and switch-on
            var segs = f.Elements(ns + "seg")
                .Concat(f.Elements(ns + "variant").SelectMany(v => v.Elements(ns + "seg")));
            foreach (var s in segs)
            {
                if ((string?)s.Attribute("ref") is { } r) AddFieldByName(r);
                if ((string?)s.Attribute("map") is { } m) AddByName("map", m);
            }
            if ((string?)f.Attribute("switch-on") is { } sw) AddFieldByName(sw);
            if ((string?)f.Attribute("list") is { } l) AddByName("list", l);
        }
        void AddFieldByName(string name)
        {
            var f = etiqLabel.Elements(ns + "field")
                .FirstOrDefault(e => (string?)e.Attribute("name") == name);
            if (f is not null) AddField(f);
        }
        void AddByName(string kind, string name)
        {
            if (!seenNames.Add(kind[0] + ":" + name)) return;
            var el = etiqLabel.Elements(ns + kind)
                .FirstOrDefault(e => (string?)e.Attribute("name") == name);
            if (el is not null) include.Add(el);
        }
        AddField(fieldEl);
        var snip = new XElement(ns + "snippet",
            new XAttribute(XNamespace.Xmlns + "etiq", ns.NamespaceName),
            new XAttribute("name", snippetName));
        foreach (var e in include) snip.Add(new XElement(e));
        return snip;
    }
}
