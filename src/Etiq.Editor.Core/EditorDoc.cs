using System.Xml.Linq;
using Etiq.Core;

namespace Etiq.Editor.Core;

/// <summary>
/// The editor's document: a live XDocument of an Etiquette SVG template
/// (convention 0.2). THE XML IS THE MODEL — typed wrappers (EditorObject,
/// EditorLayer) are views over XElements and every edit mutates the XML in
/// place, so Save() round-trips attributes, elements, comments and
/// namespaces the editor doesn't understand (Inkscape interop depends on
/// this). Undo/redo works through UndoStack over the same XML.
/// </summary>
public sealed class EditorDoc
{
    public XDocument Xml { get; }
    public string? Path { get; private set; }
    public UndoStack Undo { get; } = new();
    public double GridMils { get; set; } = 0;   // 0 = snapping off

    private EditorDoc(XDocument xml, string? path) { Xml = xml; Path = path; }

    public static EditorDoc Load(string path) => new(XDocument.Load(path), path);
    public static EditorDoc Parse(string xml) => new(XDocument.Parse(xml), null);

    public void Save(string? path = null)
    {
        Path = path ?? Path ?? throw new InvalidOperationException("no path");
        Xml.Save(Path);
    }

    public XElement Root => Xml.Root!;

    /// <summary>Label size in user units from the viewBox.</summary>
    public RectD ViewBox
    {
        get
        {
            var parts = ((string?)Root.Attribute("viewBox") ?? "0 0 0 0")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4 &&
                   double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y) &&
                   double.TryParse(parts[2], out var w) && double.TryParse(parts[3], out var h)
                ? new RectD(x, y, w, h) : default;
        }
    }

    // ---- metadata (etiq:label: fields, maps, lists) ----

    public static readonly XNamespace EtiqNs = "https://etiquette.dev/ns/0.1";

    /// <summary>The live etiq:label element, or null when the template has
    /// no metadata yet.</summary>
    public XElement? EtiqLabel() =>
        Root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")?
            .Element(EtiqNs + "label");

    /// <summary>A deep COPY of the etiq:label element (or a fresh empty one).
    /// Metadata editors mutate the clone freely, then install it in a single
    /// undoable step via ReplaceEtiqLabel.</summary>
    public XElement GetOrCreateEtiqLabelClone()
    {
        var live = EtiqLabel();
        return live is not null
            ? new XElement(live)
            : new XElement(EtiqNs + "label",
                new XAttribute(XNamespace.Xmlns + "etiq", EtiqNs.NamespaceName));
    }

    /// <summary>Install an edited etiq:label as ONE undoable step (the whole
    /// metadata edit session = one undo). Creates the metadata element when
    /// the template lacks one.</summary>
    public EditCommand ReplaceEtiqLabel(XElement edited)
    {
        var old = EtiqLabel();
        var meta = Root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        bool createdMeta = false;
        return new EditCommand("edit fields/maps/lists",
            doIt: () =>
            {
                if (old is not null) { old.ReplaceWith(edited); return; }
                if (meta is null)
                {
                    meta = new XElement(Root.Name.Namespace + "metadata");
                    Root.AddFirst(meta);
                    createdMeta = true;
                }
                meta.Add(edited);
            },
            undoIt: () =>
            {
                if (old is not null) { edited.ReplaceWith(old); return; }
                edited.Remove();
                if (createdMeta) { meta!.Remove(); meta = null; createdMeta = false; }
            });
    }

    // ---- layers ----

    /// <summary>Declared layers in document (= z) order. Content outside any
    /// layer group is the anonymous bottom layer (Name == null wrapper).</summary>
    public List<EditorLayer> Layers =>
        Root.Elements().Where(e => e.Name.LocalName == "g" &&
                                   e.Attribute("data-layer") is not null)
            .Select(e => new EditorLayer(e)).ToList();

    public EditorLayer AddLayer(string name)
    {
        var g = new XElement(Root.Name.Namespace + "g",
                             new XAttribute("data-layer", name));
        Undo.Push(EditCommand.AddElement(Root, g, $"add layer '{name}'"));
        return new EditorLayer(g);
    }

    /// <summary>Promote an ordinary group to a layer: move to top level +
    /// name it (convention: "promote group to layer is just a rename").</summary>
    public EditorLayer PromoteGroupToLayer(XElement group, string name)
    {
        var oldParent = group.Parent!;
        var oldNext = group.NextNode;
        Undo.Push(new EditCommand($"promote group to layer '{name}'",
            doIt: () =>
            {
                group.Remove();
                Root.Add(group);
                group.SetAttributeValue("data-layer", name);
            },
            undoIt: () =>
            {
                group.SetAttributeValue("data-layer", null);
                group.Remove();
                if (oldNext is not null) oldNext.AddBeforeSelf(group);
                else oldParent.Add(group);
            }));
        return new EditorLayer(group);
    }

    // ---- objects ----

    /// <summary>All editable objects across layers, document order.</summary>
    public List<EditorObject> Objects =>
        Root.Descendants()
            .Where(EditorObject.IsEditable)
            .Select(EditorObject.Wrap)
            .ToList();

    /// <summary>Topmost object at a point (reverse document order = front
    /// first). Locked layers are skipped. `pad` in user units.</summary>
    public EditorObject? HitTest(PointD p, double pad = 2)
    {
        foreach (var o in Objects.AsEnumerable().Reverse())
        {
            if (o.Layer?.Locked == true) continue;
            if (o.HitTest(p, pad)) return o;
        }
        return null;
    }

    public void AddObject(EditorLayer layer, XElement el, string label) =>
        Undo.Push(EditCommand.AddElement(layer.El, el, label));

    public void RemoveObject(EditorObject o) =>
        Undo.Push(EditCommand.RemoveElement(o.El, $"delete {o.Kind}"));

    /// <summary>Move an object one step forward/backward within its parent
    /// (document order = z-order).</summary>
    public void ReorderZ(EditorObject o, bool forward)
    {
        var el = o.El;
        XElement? swap = forward
            ? el.ElementsAfterSelf().FirstOrDefault()
            : el.ElementsBeforeSelf().LastOrDefault();
        if (swap is null) return;
        Undo.Push(new EditCommand(forward ? "bring forward" : "send backward",
            doIt: () => { el.Remove(); if (forward) swap.AddAfterSelf(el); else swap.AddBeforeSelf(el); },
            undoIt: () => { el.Remove(); if (forward) swap.AddBeforeSelf(el); else swap.AddAfterSelf(el); }));
    }

    /// <summary>Validate the current XML against the convention.</summary>
    public List<Finding> Validate() =>
        TemplateValidator.Validate(EtiqTemplate.Parse(Xml.ToString()));

    // ---- multi-object operations (one undo entry each) ----

    /// <summary>Move several objects together — a single undo entry,
    /// mergeable across drag steps via mergeKey.</summary>
    public void MoveObjects(IReadOnlyList<EditorObject> objs, double dx, double dy,
                            string? mergeKey = null)
    {
        if (objs.Count == 0 || (dx == 0 && dy == 0)) return;
        Undo.Push(EditCommand.Combine(
            objs.Select(o => o.Move(dx, dy)).ToList(),
            objs.Count == 1 ? $"move {objs[0].Kind}" : $"move {objs.Count} objects",
            mergeKey));
    }

    /// <summary>Rotate several objects TOGETHER by deltaDeg about a common
    /// pivot — one undo entry, internal layout preserved exactly. Each
    /// member's own rotation composes with the group delta into a single
    /// rotate(a x y) transform (or a plain move when the angles cancel).</summary>
    public void RotateObjects(IReadOnlyList<EditorObject> objs, double deltaDeg, PointD pivot)
    {
        if (objs.Count == 0 || deltaDeg % 360 == 0) return;
        var cmds = new List<EditCommand>();
        foreach (var o in objs)
        {
            double a1 = o.RotationDeg;
            var p1 = a1 != 0 ? o.RotationPivot : pivot; // unrotated: compose trivially
            var (deg, q, t, isTranslation) = Geometry.ComposeRotation(a1, p1, deltaDeg, pivot);
            if (isTranslation)
            {
                cmds.Add(o.Move(t.X, t.Y));
                cmds.Add(EditCommand.SetAttr(o.El, "transform", null, "clear rotation"));
            }
            else
            {
                string n(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                cmds.Add(EditCommand.SetAttr(o.El, "transform",
                    $"rotate({n(deg)} {n(q.X)} {n(q.Y)})", "rotate"));
            }
        }
        Undo.Push(EditCommand.Combine(cmds,
            objs.Count == 1 ? "rotate object" : $"rotate {objs.Count} objects"));
    }

    /// <summary>Delete several objects as one undo entry.</summary>
    public void RemoveObjects(IReadOnlyList<EditorObject> objs)
    {
        if (objs.Count == 0) return;
        Undo.Push(EditCommand.Combine(
            objs.Select(o => EditCommand.RemoveElement(o.El, "delete")).ToList(),
            objs.Count == 1 ? $"delete {objs[0].Kind}" : $"delete {objs.Count} objects"));
    }

    // ---- structural grouping (plain <g> inside a layer) ----

    /// <summary>The object's enclosing plain group: the outermost non-layer
    /// &lt;g&gt; ancestor below its layer (null when ungrouped).</summary>
    public static XElement? GroupContainer(XElement el)
    {
        XElement? found = null;
        for (var p = el.Parent; p is not null; p = p.Parent)
        {
            if (p.Name.LocalName != "g") break;
            if (p.Attribute("data-layer") is not null) break;   // layers are not groups
            found = p;
        }
        return found;
    }

    /// <summary>All editable members that click-selecting `o` should pick:
    /// the whole enclosing group when there is one, else just the object.</summary>
    public List<EditorObject> GroupMembers(EditorObject o)
    {
        var g = GroupContainer(o.El);
        if (g is null) return new List<EditorObject> { o };
        return g.Descendants().Where(EditorObject.IsEditable)
                .Select(EditorObject.Wrap).ToList();
    }

    /// <summary>Wrap the given objects in a new &lt;g&gt; at the document
    /// position of the first one (single undo entry). Objects must share a
    /// parent for a meaningful z-order result; mixed parents are legal but
    /// the group lands under the first object's parent.</summary>
    public XElement GroupObjects(IReadOnlyList<EditorObject> objs)
    {
        if (objs.Count < 2)
            throw new InvalidOperationException("select at least two objects to group");
        var g = new XElement(Root.Name.Namespace + "g");
        var els = objs.Select(o => o.El).ToList();
        // capture original positions for undo (restore in reverse order)
        var spots = els.Select(e => (El: e, Parent: e.Parent!, Next: e.NextNode)).ToList();
        var anchorParent = els[0].Parent!;
        // first following node that is NOT itself being grouped (else the
        // anchor vanishes when we lift the elements out)
        System.Xml.Linq.XNode? anchorNext = els[0].NextNode;
        while (anchorNext is System.Xml.Linq.XElement ae && els.Contains(ae))
            anchorNext = anchorNext.NextNode;

        Undo.Push(new EditCommand($"group {objs.Count} objects",
            doIt: () =>
            {
                foreach (var e in els) e.Remove();
                if (anchorNext is not null && anchorNext.Parent == anchorParent)
                    anchorNext.AddBeforeSelf(g);
                else anchorParent.Add(g);
                foreach (var e in els) g.Add(e);
            },
            undoIt: () =>
            {
                foreach (var e in els) e.Remove();
                g.Remove();
                for (int i = spots.Count - 1; i >= 0; i--)
                {
                    var (el, parent, next) = spots[i];
                    if (next is not null && next.Parent == parent) next.AddBeforeSelf(el);
                    else parent.Add(el);
                }
            }));
        return g;
    }

    /// <summary>Delete a layer WITH everything on it (single undo entry).</summary>
    public void RemoveLayer(EditorLayer layer) =>
        Undo.Push(EditCommand.RemoveElement(layer.El, $"delete layer '{layer.Name}'"));

    /// <summary>Move a layer one step in the z stack — layers paint in
    /// document order, later = on top. dir +1 raises, -1 lowers. Returns
    /// false at the edge of the stack.</summary>
    public bool MoveLayer(EditorLayer layer, int dir)
    {
        var layers = Layers;
        int i = layers.FindIndex(l => l.El == layer.El);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= layers.Count) return false;
        var el = layer.El;
        var other = layers[j].El;
        var oldParent = el.Parent!;
        var oldNext = el.NextNode;
        Undo.Push(new EditCommand(dir > 0 ? $"raise layer '{layer.Name}'" : $"lower layer '{layer.Name}'",
            doIt: () =>
            {
                el.Remove();
                if (dir > 0) other.AddAfterSelf(el);
                else other.AddBeforeSelf(el);
            },
            undoIt: () =>
            {
                el.Remove();
                if (oldNext is not null && oldNext.Parent == oldParent) oldNext.AddBeforeSelf(el);
                else oldParent.Add(el);
            }));
        return true;
    }

    /// <summary>Move objects to another layer as ONE undo entry. Moves each
    /// object's top-level UNIT — selecting one member of a group moves the
    /// whole group, because a group lives in exactly one layer (XML nesting:
    /// members are the group's descendants, so a group CANNOT span layers).
    /// Moved units land at the end of the target layer (= its top).</summary>
    public void MoveToLayer(IReadOnlyList<EditorObject> objs, EditorLayer target)
    {
        var units = new List<XElement>();
        foreach (var o in objs)
        {
            var unit = o.El;
            for (var p = o.El.Parent;
                 p is not null && p.Name.LocalName == "g" && p.Attribute("data-layer") is null;
                 p = p.Parent)
                unit = p;
            if (!units.Contains(unit) && unit.Parent != target.El) units.Add(unit);
        }
        if (units.Count == 0) return;
        var spots = units.Select(e => (El: e, Parent: e.Parent!, Next: e.NextNode)).ToList();
        Undo.Push(new EditCommand($"move {units.Count} to layer '{target.Name}'",
            doIt: () =>
            {
                foreach (var e in units)
                {
                    e.Remove();
                    target.El.Add(e);
                }
            },
            undoIt: () =>
            {
                for (int i = spots.Count - 1; i >= 0; i--)
                {
                    var (el, parent, next) = spots[i];
                    el.Remove();
                    if (next is not null && next.Parent == parent) next.AddBeforeSelf(el);
                    else parent.Add(el);
                }
            }));
    }

    /// <summary>Dissolve a plain group: children move up to the group's
    /// position, the empty &lt;g&gt; is removed (single undo entry).</summary>
    public void Ungroup(XElement g)
    {
        if (g.Name.LocalName != "g" || g.Attribute("data-layer") is not null)
            throw new InvalidOperationException("not a plain group");
        var children = g.Nodes().ToList();
        Undo.Push(new EditCommand("ungroup",
            doIt: () =>
            {
                foreach (var n in children) n.Remove();
                foreach (var n in children) g.AddBeforeSelf(n);
                g.Remove();
            },
            undoIt: () =>
            {
                var last = children.LastOrDefault(n => n.Parent is not null);
                if (last is not null) last.AddAfterSelf(g);
                foreach (var n in children) { n.Remove(); g.Add(n); }
            }));
    }
}

/// <summary>Typed view over a layer group (&lt;g data-layer&gt;).</summary>
public sealed record EditorLayer(XElement El)
{
    public string Name => (string?)El.Attribute("data-layer") ?? "";
    public bool Locked => (string?)El.Attribute("data-locked") == "true";
    public bool Printed => (string?)El.Attribute("data-print") != "false";
    public bool Visible => (string?)El.Attribute("display") != "none";

    public EditCommand SetLocked(bool v) =>
        EditCommand.SetAttr(El, "data-locked", v ? "true" : null, v ? "lock layer" : "unlock layer");
    public EditCommand SetPrinted(bool v) =>
        EditCommand.SetAttr(El, "data-print", v ? null : "false", v ? "print layer" : "no-print layer");
    public EditCommand SetVisible(bool v) =>
        EditCommand.SetAttr(El, "display", v ? null : "none", v ? "show layer" : "hide layer");
    public EditCommand Rename(string name) =>
        EditCommand.SetAttr(El, "data-layer", name, $"rename layer to '{name}'");
}
