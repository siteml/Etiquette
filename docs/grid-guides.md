# Grid, guides, units and dot snapping — design (DRAFT 2)

Status: **implemented** (all four passes, 0.10.0 cycle). This document
is the design record; `docs/convention.md` § "Editor view state" is the
normative format. Deviations from the draft are noted inline as
*Implemented:*.

## Goal

Give `etiqedit` the layout aids a designer expects from BarTender,
Inkscape or Figma — a visible grid, user-placed guides, snapping to
either — plus two things label work needs that general tools don't have:

- **display units** the operator chooses (inches, millimetres, mils,
  printer dots) over an internal unit that never changes;
- **snap-to-dots**: a grid whose pitch *is* the target printer's dot
  pitch, so positions, sizes and stroke widths land on whole dots.

Everything here is **view state**. None of it changes what prints, none of
it is content, and engines/validators ignore all of it.

## What exists today

- `EditorDoc.GridMils` — hard rounding via `Geometry.Snap` for moves,
  resizes and line endpoints. Default `0` (off). Not persisted, no grid
  drawn, no UI to set it.
- `SnapEngine` — magnetic edge/centre snapping to other visible objects
  and the label rect, within a screen-space tolerance (6 px / zoom).
  Draws transient magenta alignment lines ("smart guides"). Skipped for
  rotated objects on resize.
- `Alt` while dragging suppresses both.
- Printer registry (`config/printers.json`) carries `dpi` per printer;
  media/printer feasibility already computes nearest-dot deviation for
  barcode modules.
- Convention `docs/convention.md` shows a `<g data-layer="Guides"
  data-print="false">` example. That is *drawn* helper art (margin boxes,
  notes). It stays valid and is unrelated to the ruler guides below.

## Vocabulary

- **user unit** — the SVG coordinate unit. Editor-authored templates use
  1 user unit = 1 mil (`width="6in" viewBox="0 0 6000 4000"`). Internal
  only; never shown unless the operator picks `mils` as display unit.
- **display unit** — what rulers, status bar, inspector, dialogs and grid
  settings show and accept: `in | mm | mils | dots`.
- **grid** — a regular lattice the cursor/edges round onto. One pitch,
  square. Source is either a typed value or the target printer's dots.
- **guide** — an infinite horizontal or vertical line at one coordinate,
  placed by the operator. Magnetic (like object snapping), never printed,
  never an object.
- **smart guide** — the existing transient alignment line from
  `SnapEngine`. Not stored.
- **target printer** — the printer whose dpi defines `dots`. Resolution
  order below.

## Why mils stay the internal unit

- Integer coordinates in the SVG for inch-based stock; clean diffs.
- Finer than any label printer dot (203 dpi ≈ 4.9 mil, 300 ≈ 3.3,
  600 ≈ 1.7): no precision loss.
- The barcode world speaks mils (`data-module-mils`, spec sheets, ZPL
  docs); encoders, validator and test vectors are already in mils.
- 1 mil = 0.0254 mm exactly, so metric is a lossless *display*
  conversion. A 1 mm grid is a 39.37-mil grid; all geometry is `double`,
  so nothing breaks — coordinates on a metric grid simply save as
  `78.740157480` rather than `80`.
- Precision: coordinates are doubles end to end and serialize through
  `Num.F` (`0.#########`, invariant culture). 1 mm = 5000/127 mils is a
  non-terminating decimal, so no fixed digit count is *mathematically*
  exact, but nine decimals (0.025 nm) round-trip any value typed at
  display precision (µm, thousandths of an inch, whole dots) and still
  trim float noise from grid arithmetic. The editor never rounds mils to
  integers, including the viewBox extent for mm-sized labels.
- Switching to µm (exact for both systems) would break every existing
  template, attribute and test for a cosmetic gain. Rejected.

This matches Inkscape's model exactly: `inkscape:document-units` is a
display choice layered over whatever the user unit is.

## Guides are not a layer

A layer is a z-ordered, printable, groupable `<g data-layer>`. A guide is
none of those: infinite extent, no z-order, no group membership, no print
flag, never hit-tested as an object, never in the outline tree. Modelling
guides as objects would fight every layer rule already in place
(cannot span layers, move-to-layer, merge, `data-print`). Guides live
beside `GridMils` as document view state.

## Persistence — `etiq:view`

One optional element inside the existing `etiq:label` metadata block:

```xml
<etiq:view units="in" grid="dots" dots-per-mm="8" target="ZT230"
           snap="grid,guides,objects" guides-locked="false">
  <etiq:guide axis="x" pos="250"/>
  <etiq:guide axis="x" pos="5750"/>
  <etiq:guide axis="y" pos="2000"/>
</etiq:view>
```

- `units` — `in | mm | mils | dots`. Display only. Absent = app default.
- `grid` — `off` | a length | `dots`. A length is a number with an
  optional unit suffix (`50`, `0.05in`, `1mm`); bare number = mils.
  `dots` uses `dots-per-mm` as the pitch.
- `dots-per-mm` — the target head's dot density, **authoritative**.
  Pitch in mils = `1000 / (25.4 × dots-per-mm)`. Required when
  `grid="dots"` or `units="dots"`; the template is self-describing on any
  machine, no registry lookup needed.
- `target` — optional human label ("ZT230"), written when the value came
  from a registry pick. Display only; never used to compute anything and
  never resolved by name (queue names and registry names drift per
  machine — "ZDesigner ZT230-200dpi ZPL (Copy 1)").
- `snap` — comma list from `grid`, `guides`, `objects`. Absent = all on.
- `guides-locked` — guides cannot be dragged/deleted until unlocked.
- `etiq:guide` — `axis="x"` (vertical line at that x) or `axis="y"`;
  `pos` always in **mils**, regardless of `units`. Optional `name`.

Rules:

- Engines, `etiq validate`, `etiq resolve`, print paths: ignore the
  element entirely. Validator MAY warn on malformed `etiq:view` but never
  fails a template for it.
- App settings (Help → Options) hold the defaults for new templates:
  `units` (default **in**), `grid` (default **off**), `snap` (default
  all on). A template's `etiq:view`, once written, overrides them.
- Absent `etiq:view` ⇒ today's behaviour (grid off, object snapping on).
- Editor writes `etiq:view` only after the operator changes something;
  opening and saving an untouched template never adds it.

### Inkscape interop

Inkscape stores guides in `sodipodi:namedview/sodipodi:guide`
(`position="x,y"` in user units, y-up in older files, `orientation`
vector) and grids in `inkscape:grid`. The convention already mirrors
`inkscape:groupmode="layer"` so Inkscape shows our layers natively; the
same courtesy is possible here:

- **read**: if `etiq:view` is absent and `sodipodi:namedview` has guides,
  import them (axis-aligned only; skip angled guides).
- **write**: if a `namedview` already exists in the file, mirror guides
  into it on save; never create one.

Cost: y-axis flip and unit handling per Inkscape version. Decision:
**read-only import**; `etiqedit` never writes `sodipodi:namedview`.

## Snapping model

Three independent toggles (View menu, toolbar buttons, status-bar
indicators; `Alt` still suppresses all for one gesture):

| toggle    | mechanism                                     | strength |
|-----------|-----------------------------------------------|----------|
| grid      | `Geometry.Snap` rounding to the pitch         | hard     |
| guides    | `SnapEngine` candidates                       | magnetic |
| objects   | `SnapEngine` candidates (existing)            | magnetic |

Order per gesture: grid rounding → magnetic adjustment (guides +
objects) → **when the grid is `dots`, re-round the magnetic result to
the nearest dot**. Alignment to a neighbour is then exact to within half
a dot, and every edge stays on the lattice, so sizes stay whole dots.
For a manual grid the magnetic result stands, as today. Guides simply add
their `pos` to the candidate `xs`/`ys` in `SnapEngine.Candidates` — one
extra parameter. Magnetic snap to a guide draws that guide highlighted
rather than a separate magenta line.

Resize goes through the same path, so an object whose edges are on the
grid has grid-multiple width and height without extra logic. Rotated
objects keep grid snapping in local space and skip guide/object snapping,
exactly as they skip object snapping now.

### Stroke width

Inspector stroke-width rows gain a "snap to dots" nudge when `dots` is
resolvable: rounds to the nearest whole dot, minimum 1. A 1-dot hairline
is the most visible win of the whole feature; sub-dot strokes render
inconsistently on thermal heads. Not automatic — an explicit button/key,
because stroke widths are often inherited from imported art.

## Snap-to-dots

`grid="dots"` sets pitch = `1000 / (25.4 × dots-per-mm)` mils, from the
template's own `dots-per-mm`. Nothing is resolved at load time.

Real densities (worth writing into the UI tooltip, since "203" is a lie):

| nominal dpi | true density        | mils/dot |
|-------------|---------------------|----------|
| 203         | 8 dots/mm = 203.2   | 4.921    |
| 300         | 11.81 dots/mm       | 3.333    |
| 600         | 23.62 dots/mm       | 1.667    |
| 180         | Brother PT-D410     | 5.556    |

**Registry.** `config/printers.json` entries gain an optional
`dotsPerMm`; existing entries stay valid and fall back to `dpi / 25.4`.
The registry's only role here is to **populate** the template value:

- View → Target Printer… lists registry entries (name + density) plus
  "Custom…" (type dpi or dots/mm). Picking one writes `dots-per-mm` and
  the `target` label. The dropdown is prefilled, first time only, from
  the `etiq:panel` pinned printer if it is in the registry, else the
  machine default printer if it is, else nothing.
- If `grid="dots"` or `units="dots"` is set but `dots-per-mm` is absent
  (hand-edited file), the grid falls back to off, status bar shows
  "dots: no density set", `dots` unit is greyed, and the Target Printer…
  dialog opens on first use.

Changing the density does **not** move anything. A "Snap all to dots"
command (Layout menu, undoable, one step) re-rounds every unrotated
object's position and size to the current pitch for when a template
migrates 8/mm → 11.81/mm. Rotated objects are skipped and counted in the
status message ("12 snapped, 2 rotated skipped"); rotated snapping in
general is deferred — see roadmap "rotated-resize snapping".

**Print-time mismatch.** When the chosen queue's registry entry has a
different `dotsPerMm` than the template's `dots-per-mm`, the print path
warns "designed for 8 dots/mm, printing at 11.81 dots/mm" — a numeric
comparison, never a name match. Same pattern as `data-module-mils`
feasibility: physics in the template, checked against whatever printer
is actually chosen.

**Origin.** The dot lattice is anchored at the viewBox origin. The ZPL
raster path is then exact. The driver path renders at the queue's native
DPI with 1:1 dot mapping, but printer-side offsets may shift the whole
image a fraction of a dot; the goal is integer *sizes* (crisp edges,
integer barcode modules), not absolute registration, so this is
acceptable.

**Barcodes.** Renderers already module-snap and tight-box symbols; a
dot-snapped box just means the editor box equals the printed symbol
sooner, and the validator's nearest-dot deviation warning becomes rare.

## Drawing

- **Grid**: dotted or thin lines in a light grey at the pitch. Adaptive:
  draw only when on-screen spacing ≥ ~6 px; below that draw every 2nd,
  4th, 8th … line so spacing stays in the 6–48 px band, and snapping
  still uses the true pitch. A 203-dpi dot grid becomes visible at
  roughly 250 % zoom. Major lines every 10 pitches in `in`/`mils`/`mm`,
  every 8 in `dots` (one millimetre on an 8/mm head).
- **Guides**: solid 1 px cyan (theme-aware later) across the whole
  canvas, drawn above content, below selection chrome. Design mode only;
  Data mode never draws grid or guides.
- **Smart guides**: unchanged (magenta dashed).
- **Rulers**: new; top and left, in the display unit, with a cursor
  marker. Rulers are the guide-creation surface (below), so they arrive
  in the same pass.

## Interaction

- Drag from a ruler into the canvas → new guide, following the cursor,
  snapping to grid/objects if enabled. Release over the ruler → cancel.
- Drag an existing guide to move it; drag it back onto its ruler to
  delete it. Cursor changes on hover; hit tolerance 4 px.
- Double-click a guide → small dialog: position (display unit), name.
- View → Add Guide… (same dialog) creates one without rulers — keyboard
  route and exact placement; rulers are the fast route. Both ship.
- Right-click guide → Delete, Lock guides, Delete all guides.
- View menu: Show Grid, Show Guides, Show Rulers, Lock Guides, Snap to
  Grid / Guides / Objects, Grid… (pitch + source), Units ▸ in / mm /
  mils / dots.
- Status bar: `x: 1.250 in  y: 0.500 in` in the display unit; a
  `grid: 1 mm` / `grid: dots @ 203` segment that toggles on click.
- All guide edits go through `UndoStack` as metadata edits, merge-keyed
  per drag, so a guide drag is one undo like an object drag.

## Display units

Conversion from mils: `in = mils / 1000`, `mm = mils × 0.0254`,
`dots = mils × dpi / 1000`. Formatting precision: `in` 3 dp, `mm` 2 dp,
`mils` 0 dp, `dots` 0 dp (fractional dots shown in grey when not whole).

Applies to: status bar, inspector geometry rows (x, y, w, h, stroke),
rulers, Label Size dialog (already in/mm), grid pitch entry, guide
dialog, and the selection info line. Parsing accepts an explicit unit
suffix anywhere a length is typed, whatever the display unit.

Text size stays in points. Barcode `module-mils` stays in mils in the
inspector (industry vocabulary) but shows the dots equivalent beside it
when a target resolves.

## Implementation map

| piece                              | where                          |
|------------------------------------|--------------------------------|
| `ViewSettings` read/write, guides  | `Etiq.Editor.Core/EditorDoc`   |
| unit conversion + formatting       | `Etiq.Editor.Core/Units` (new) |
| guide candidates                   | `Etiq.Editor.Core/SnapEngine`  |
| registry `dotsPerMm`, mismatch warn| `Etiq.Core/Registry`, print path |
| grid/guide/ruler drawing, drag     | `Etiq.Editor/CanvasControl`, `RulerControl` (new) |
| menus, status bar, settings        | `Etiq.Editor/MainForm`         |
| unit-aware rows, stroke snap       | `Etiq.Editor/InspectorPanel`   |
| Snap-all-to-dots                   | `Etiq.Editor.Core/Commands`    |
| convention: `etiq:view` section    | `docs/convention.md`           |

Suggested passes:

1. `Units` + display-unit setting + status bar/inspector/dialogs. No
   file format change. Immediately useful.
2. `etiq:view` persistence, grid drawing, Grid… dialog, `dots` source,
   Snap-all-to-dots.
3. Rulers, guides (create/move/delete/lock, undo), guide snapping.
4. Inkscape `namedview` read import; stroke-width dot snap; print-time
   density mismatch warning.

Tests (Editor.Core, headless): unit round-trips; `etiq:view` parse of
every `grid` spelling; guide candidates change `SnapEngine` results;
absent `etiq:view` ⇒ unchanged behaviour; untouched load/save adds no
element.

## Decisions (draft 1 → 2)

- **Target printer**: not resolved by name. The template carries
  `dots-per-mm` (authoritative) plus an optional `target` label. Registry
  only populates the value; prefill order panel-pinned → machine default.
  Rationale: queue and registry names drift per machine and carry
  extra/missing bits; physics doesn't.
- **Registry precision**: optional `dotsPerMm` per entry; `dpi / 25.4`
  fallback. 8 vs 7.992 (literal 203) is one dot per 6 in — worth being
  exact.
- **Inkscape guides**: read-only import when `etiq:view` is absent; no
  write-back.
- **Dots + object snapping**: grid → magnetic → re-round to nearest dot.
  Objects stay on the lattice; alignment error ≤ ½ dot.
- **Major lines**: every 10 pitches (in/mm/mils), every 8 (dots).
  *Implemented:* a program setting (Options), not template state — every
  N (0 = auto) with two modes: fixed (N cells = physical distance;
  minors thin by divisors of N) or adaptive (N drawn lines, follows
  zoom). Fixed is the default. On-screen minimum line spacing is a
  setting too (fine/normal/coarse/sparse).
- **Non-square dot pitch**: ignored; none in the fleet. `dots-per-mm` is a
  single number by design; extend to `x,y` only if a printer appears.
- **Out of scope, noted**: `etiq:panel printer=` is also name-keyed and
  has the same weakness; a registry `id` slug would fix both later.
- **Defaults**: display unit `in`, grid off, both settable in Options.
- **`etiq:view` written only once a view setting is touched.**
- **Guides/grid/rulers are Design-mode only.**
- **Guide creation**: rulers (drag) and View → Add Guide… (dialog) both.
  *Implemented:* while guides are locked, ruler drags create nothing;
  Add Guide… stays available.
- **Print-time density check** *Implemented* against the driver's
  reported resolution (`PrinterResolution.X`), not a registry lookup —
  no name matching at all.
- **Stroke-width dot snap is explicit**, never automatic.
- **Rotated objects**: skipped by Snap-all-to-dots, counted in the
  message; rotated snapping deferred as a whole.
- **Guide `pos` always mils**; `Alt` suppresses all snapping.
- **Pass order**: units → view/grid/dots → rulers/guides → interop.
