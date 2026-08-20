using System.Xml.Linq;

namespace Etiq.Editor.Core;

/// <summary>
/// One undoable edit, built from do/undo closures over the document XML.
///
/// Merging: a command whose non-null MergeKey matches the top of the undo
/// stack is absorbed into it, so rapid repeats (drag steps, arrow-key
/// nudges) collapse into one undo entry. This is sound because undo
/// closures restore ABSOLUTE prior values captured at creation time — the
/// first command's undo alone returns the document to the pre-gesture
/// state, while the merged Do chain replays the whole gesture for redo.
/// (Consequence: only snapshot-style commands may carry a MergeKey.)
/// </summary>
public sealed class EditCommand
{
    public string Label { get; }
    public string? MergeKey { get; }
    private Action _do;
    private readonly Action _undo;

    public EditCommand(string label, Action doIt, Action undoIt, string? mergeKey = null)
    {
        Label = label; _do = doIt; _undo = undoIt; MergeKey = mergeKey;
    }

    public void Do() => _do();
    public void Undo() => _undo();

    /// <summary>Absorb a follow-up command (see class doc).</summary>
    internal void Merge(EditCommand next)
    {
        var prev = _do;
        _do = () => { prev(); next._do(); };
    }

    /// <summary>Combine several snapshot-style commands into one undo entry
    /// (multi-object move, multi-delete). Do runs them in order, Undo in
    /// reverse. Mergeable like any other command via mergeKey.</summary>
    public static EditCommand Combine(IReadOnlyList<EditCommand> cmds,
                                      string label, string? mergeKey = null) =>
        new(label,
            doIt: () => { foreach (var c in cmds) c.Do(); },
            undoIt: () => { for (int i = cmds.Count - 1; i >= 0; i--) cmds[i].Undo(); },
            mergeKey);

    // ---- factories over XML ----

    public static EditCommand SetAttr(XElement el, string attr, string? value,
                                      string label, string? mergeKey = null)
    {
        string? old = (string?)el.Attribute(attr);
        return new(label,
            doIt: () => el.SetAttributeValue(attr, value),
            undoIt: () => el.SetAttributeValue(attr, old),
            mergeKey);
    }

    public static EditCommand SetAttrs(XElement el,
        List<(string Attr, string? Old, string? New)> changes,
        string label, string? mergeKey = null) =>
        new(label,
            doIt: () => { foreach (var c in changes) el.SetAttributeValue(c.Attr, c.New); },
            undoIt: () => { foreach (var c in changes) el.SetAttributeValue(c.Attr, c.Old); },
            mergeKey);

    public static EditCommand AddElement(XElement parent, XElement child, string label) =>
        new(label,
            doIt: () => parent.Add(child),
            undoIt: () => child.Remove());

    public static EditCommand RemoveElement(XElement el, string label)
    {
        var parent = el.Parent ?? throw new InvalidOperationException("element has no parent");
        var next = el.NextNode;
        return new(label,
            doIt: () => el.Remove(),
            undoIt: () => { if (next?.Parent == parent) next.AddBeforeSelf(el); else parent.Add(el); });
    }
}

/// <summary>Classic undo/redo stack. Push() executes the command; matching
/// MergeKey merges into the top entry; any push clears redo.</summary>
public sealed class UndoStack
{
    private readonly List<EditCommand> _undo = new();
    private readonly List<EditCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => CanUndo ? _undo[^1].Label : null;
    public string? RedoLabel => CanRedo ? _redo[^1].Label : null;
    public int Count => _undo.Count;

    /// <summary>Raised after any Push/Undo/Redo — the shell repaints on it.</summary>
    public event Action? Changed;

    public void Push(EditCommand cmd)
    {
        cmd.Do();
        if (cmd.MergeKey is not null && _undo.Count > 0 &&
            _undo[^1].MergeKey == cmd.MergeKey)
            _undo[^1].Merge(cmd);
        else
            _undo.Add(cmd);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        cmd.Undo();
        _redo.Add(cmd);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        cmd.Do();
        _undo.Add(cmd);
        Changed?.Invoke();
    }
}
