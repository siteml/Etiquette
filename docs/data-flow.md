# Data flow — value layers, the snapshot, and what the canvas shows

Design, agreed 2026-09-10. Replaces the accreted preview logic in the
editor (debounce timer, focus gate, sticky failure cache, ad-hoc
"cleared" maps). The engine (`etiq`) and print paths are unaffected: they
already resolve once, from complete inputs.

The rule everything below serves: **what is displayed is tracked
internally and updated consistently.** One state, one producer, one
reader. Nothing on the canvas comes from anywhere but the snapshot, except
design-time content and stand-ins, which are template content.

## 1. Value layers

Every value lives in exactly one layer. Layers are never written into each
other; they are *read* in a fixed order to produce the next layer.

| layer | held per | what it is | written by |
|---|---|---|---|
| **design** | element | the element's literal text / `data-value` — the sample the designer drew | designer, Design mode |
| **default** | field | `default=` on a prompt; `default=` row on a list | template |
| **entry** | field | the operator's live box text / picker text | every keystroke |
| **committed** | field | the entry the resolve is allowed to see | see §4 |
| **fetched** | source | raw rows from a remote source, keyed by the committed inputs that produced them | fetch |
| **resolved** | field | resolver output over committed + fetched (compose, map, case, `if-empty`, `required`) | the snapshot producer |
| **stand-in** | field / element | redaction value (`stand-in=`, `data-sensitive`) | template |

Two things that used to be layers and no longer are:

- *"pulled data kept across modes"* — there is no separate memory; the
  snapshot is the memory, and Clear empties it. The Options switch goes.
- *"cleared values"* — not a special map; Clear is an ordinary
  recompute of the snapshot from reset inputs (§6).

## 2. The snapshot

```
PreviewSnapshot
  values     : field → string        resolved, one entry per declared field that resolved
  errors     : field → string        one entry per declared field that did NOT resolve
  redacted   : field → string        the same resolve with stand-ins fed in (only while Redact is on)
  generation : int                   bumped by commit / Clear / Refresh / dataset change
```

- Produced by **one function** from (committed inputs, fetched cache,
  stand-ins). Nothing else builds field→value maps for the canvas.
- Replaced **atomically**: the canvas holds one reference and repaints.
  A recompute that is still running when another is requested is
  re-run once at the end (the existing busy/again latch).
- **Per-field**, not all-or-nothing. Each field either has a value or an
  error. A `required` field left empty is an error for *that field*; every
  other field still shows. This is what ends "one failure leaves the old
  data on the canvas".
- Fetch failures are errors on the fields that read the source, plus one
  status line naming the source and the message.

## 3. What the canvas shows

Precedence per element, top wins:

| mode | bound element (`data-field`) | unbound element |
|---|---|---|
| Design, *Design values* | design text | design text |
| Design, *Data values* | snapshot value; **empty** if error / absent | design text |
| Data | snapshot value; **empty** if error / absent | design text |

- Redact Sensitive on: a bound element reads `redacted` instead of
  `values`; an unbound `data-sensitive` element shows its stand-in / block
  mask. Print never reads either.
- The Design-mode toggle is greyed until a snapshot exists (i.e. until
  the document has been in Data mode once). It is a *view* of the last
  snapshot. Editing values from Design mode is deliberately not offered —
  the possibility is left open, nothing precludes it, but it is not a need
  today.
- Data mode never shows design text on a bound element. Ever.
- `data-clear="blank"` on a bound element: shows empty while the panel is
  in its cleared state (§6), regardless of the snapshot value.

## 4. Inputs → committed

Two kinds of prompt, decided per template by
`EtiqTemplate.FieldsFeedingRemote()` (fields whose value can reach a
source parameter/filter/query or a query-fed list's filter, directly or
through a compose):

| prompt kind | committed = | when |
|---|---|---|
| feeds a fetch | entry **as of commit** | Tab / focus loss / Enter / Clear |
| everything else | entry | every keystroke |

Consequences, all intended:

- The fetch runs exactly once per committed key. Typing "C", "C0",
  "C08"… never reaches it.
- The bound element for a fetch-feeding prompt shows the **committed**
  value, not the keystrokes. What the label shows is what the lookup used.
- Lists, pickers and fetched row sets are committed on change (no typing
  involved).
- A commit that changes nothing is a no-op. Commit runs synchronously on
  focus loss, so a Print click that took the focus already sees the value.
- `override="true"` fields: the box is **prefilled** with the fetched
  value as real text (italic, muted — the "from source" style; redacted
  like the canvas when Redact is on) and is *not* submitted to the
  resolve. The operator's first keystroke makes the box the value —
  empty included, so a fetched value can be blanked. ↺ beside the box
  and Clear drop the edit; the next recompute refills it from the fetch.
  What is in the box is what prints; there is no "empty means fetched"
  rule to explain.

## 5. Fetch

- Keyed by (source, dataset, target, committed parameter values) — the
  existing signature.
- Success is cached across generations (same key → no re-pull).
- Failure is recorded **with the generation it happened in**. The next
  generation retries it. Commit, Clear, Refresh Preview and a dataset
  change each bump the generation. So a bad job number is never remembered
  as bad once the operator re-commits, and an invalid entry can't poison a
  later one.
- A pull needs every field-fed input non-empty; otherwise the fields that
  read the source carry `waiting for <input>` as their error.

## 6. Clear

One ordinary recompute after resetting the inputs:

1. entry ← default (`clear="blank"`: empty even with a default); lists ←
   default row / first row; embedded Copies ← 1.
2. committed ← entry for every prompt (Clear is a commit).
3. fetched cache dropped; generation++.
4. snapshot recomputed. Fields that read a source resolve against empty
   inputs → errors → shown empty. Composes keep their literal and prompt
   parts (a resolve, not a map edit). Auto/fixed fields (date) stay.
5. Panel enters its *cleared* state: `data-clear="blank"` elements draw
   empty. The next entry into any box leaves the cleared state.

Redaction needs no special ordering: stand-ins are fed into the same
resolve, so a restored default that is sensitive comes out redacted.

## 7. Errors on screen

- **Data panel inputs**: the box (or picker) whose field has an error —
  for a picker, any field reading its list — gets a thin red outline.
  Nothing textual next to it.
- **Canvas**: the bound element draws empty. No outline, nothing textual
  on the label.
- **Status line** at the bottom of the panel lists every error, one per
  line, `Field: message`, source failures first.
- Print is refused while any field bound to a printed element has an
  error (today's rule, unchanged).

## 8. Mode switches

- Data → Design: the snapshot is kept as-is (it is the memory). Nothing
  recomputes in Design mode; no input handlers run; the canvas shows
  design or data values per the toggle.
- Design → Data: the panel is rebuilt; entries come back from the
  per-session memo (as today); committed ← entry; one recompute.

## 9. Implementation map

| piece | where | notes |
|---|---|---|
| `PreviewSnapshot` + producer | `Etiq.Editor.Core/Preview.cs` | pure: (template, committed, fetch provider, stand-ins) → snapshot. Unit-tested. Per-field resolve via `FieldResolver.Resolve(name)` in try/catch; the resolver's memo keeps shared work single. |
| generation-aware fetch cache | `Etiq.Editor/SourceCache.cs` | rows + failures with generation; the only owner of `_sourceRows`/`_sourceFails`. |
| committed store | `Etiq.Editor/MainForm` data panel | `Dictionary<field,string>`; commit rules per §4. |
| canvas | `CanvasControl` | `Snapshot` property replaces `ResolvedValues`/`DisplayValues`; `ShowDataValues` toggle; red outline for errored bound elements; `Cleared` flag stays. |
| removed | — | preview timer, focus gate, `KeepPulledData`, `ClearedValues`, `RedactedDisplay`, `RefreshPreview`/`RefreshPreviewAsync` split (one async producer; print awaits it). |

Out of scope, pinned: editing values from Design mode; per-box error
decoration; anything about series (`docs/series.md`).
