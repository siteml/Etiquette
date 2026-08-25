# The Etiquette SVG label convention — draft 0.2

Changes 0.1 → 0.2 (2026-08-02, WYSIWYG-editor decision — see roadmap
Phase 4): composed fields (`source="compose"` segment lists), lookup maps,
`if-empty`/`required` conditions, `on-fail` policy for epicor/rest fields,
generic `rest` source kind (named connection profiles; GLPI is the first
planned profile), reserved `db` + `file` + `device` source kinds (stubs),
layers as top-level groups, text fit
(`data-width`/`data-overflow` — needed for .btw width-fitted text parity).
All 0.1 templates remain valid 0.2 templates.

A label template is a **plain SVG file** with a small set of documented
attributes. Anything that renders SVG can preview it; Inkscape can edit it;
the Etiquette engine can print it.

## Ground rules

1. The SVG root carries physical units (`width="6in" height="4in"` +
   matching `viewBox`). One user unit = 1/96 in (CSS pixel) unless the
   template says otherwise.
2. Etiquette-specific data lives in `data-*` attributes (valid SVG, ignored
   by every renderer) and one optional metadata block. Editors that preserve
   unknown attributes (Inkscape does) round-trip templates safely.
3. A template with every placeholder left as-is must still render as a
   sensible "sample data" label — the placeholder text doubles as preview.

## Dynamic text

A `<text>` (or `<tspan>`) element becomes dynamic with one attribute:

```xml
<text x="34" y="120" font-family="Arial" font-weight="bold"
      font-size="16" data-field="PartNo">{PartNo}</text>
```

At print time the engine replaces the element's text content with the value
of `PartNo`. The literal content in the file is just the design-time preview.

Optional:
- `data-format="date:dd-MMM-yyyy"` / `"number:0000"` — display formatting
- `data-transform="upper|trim"` — simple transforms, comma-separated

### Text fit

Without a box, text renders at its natural width. With one:

- `data-width` (user units) — the text box width, measured from the
  element's anchor according to its justification (left-anchored grows
  right, centered grows both ways, etc.). This is where .btw text box
  width lands on conversion.
- `data-overflow` — what happens when the rendered value exceeds
  `data-width` (enum borrowed from JasperReports' `textAdjust`):
  - `shrink` — **default when `data-width` is present**: compress
    horizontally to fit, preserving cap height (BarTender width-fit
    behavior; DrawCapTopFitted in the GDI prior art in reference/)
  - `clip` — hard cut at the box edge
  - `wrap` — break into lines at the box width; requires `data-height`
    (user units), clips at the box bottom

`data-overflow` without `data-width` is meaningless (validator error).

**Fit modes (`data-fit`).** The box attributes give three ways for text to
meet its box, selectable explicitly with `data-fit`:

- `none` — dynamic width: the font never changes. A `data-width`/
  `data-height` present is a WINDOW — overlong text hard-clips at the
  box boundary instead of spilling over the label. In the editor,
  resize drags on a `data-fit="none"` text size the box (never the
  font).
- `width` — squeeze into the `data-width` box horizontally (per
  `data-overflow` above); height is whatever the lines need.
- `box` — fixed box: the font shrinks UNIFORMLY (size and line-height
  together) until the whole block fits `data-width` × `data-height`.

When `data-fit` is absent the mode is inferred: `width` when `data-width`
is present, else `none`. `box` is never inferred — labels that use
`data-height` purely as a vertical-alignment box keep their behavior.

### Multiline text and box alignment

A text element's resolved value (or literal content) may contain newline
characters; renderers stack the lines downward from the element's baseline.

- `data-line-height` (user units) — baseline-to-baseline distance;
  default `1.2 × font-size`.
- `data-align` — `left` (default) | `center` | `right`: per-line
  horizontal placement inside the `data-width` box (no box → no-op).
- `data-height` (user units) — the text box height, measured from the
  element's top (baseline − 0.8em).
- `data-valign` — `top` (default) | `middle` | `bottom`: placement of the
  whole line block inside the `data-height` box (no box → no-op).

Caveat: plain SVG has no automatic line breaks, so a foreign renderer
(browser, Inkscape) shows multiline content space-collapsed on one line.
Etiquette's own renderers (editor, print) honor it.

**Line stacks (plain-SVG-pure multiline).** The preferred presentation for
multiline content is a stack of ordinary single-line `<text>` elements —
foreign renderers then show the label correctly. For static text this is
just elements. For dynamic content, `data-line="N"` (0-based) on a
field-bound element makes it render only the Nth line of the resolved
value (empty when the value has fewer lines). Because the index applies
AFTER `collapse-blank-lines`, a vanished blank line reflows the stack
automatically — each element keeps its fixed position, the content moves
up. The editor's "Split into Line Stack" command converts a multiline
element into such a stack.

## Barcodes

A placeholder `<rect>` marks the barcode's position and target box:

```xml
<rect x="120" y="200" width="240" height="60"
      data-barcode="code128" data-field="Serial"
      data-hri="below"/>
```

- `data-barcode`: `code39 | code39ext | code128 | gs1-128 | itf14 |
  datamatrix | qr | rmqr | aztec | pdf417` — all implemented with
  dependency-free, decode-verified encoders. (`iqr` was dropped in 0.6:
  Denso Wave never published the spec openly and no open decoder exists —
  use `rmqr` for rectangular or `qr` for square symbols.)
  - `gs1-128`: content uses parenthesized GS1 Application Identifiers,
    e.g. `(01)09501101530003(10)LOT42`; FNC1 separators are inserted
    automatically after variable-length AIs, and fixed-length AIs are
    validated against the GS1 predefined-length table.
  - `itf14`: interleaved 2 of 5, digits only; exactly 13 digits get the
    GS1 check digit appended automatically; odd lengths are left
    zero-padded.
  - `rmqr`: rectangular QR (ISO/IEC 23941, R7x43 … R17x139); the symbol
    version is chosen automatically to best match the target box aspect.
  - `aztec`: ISO/IEC 24778, compact and full symbols; needs no quiet zone.
- `data-field`: data source for the symbol content (or `data-value` for fixed)
- `data-hri`: `none | below | above` human-readable interpretation (linear
  symbologies; rendered inside the target box — for `itf14` the HRI shows
  the digits actually encoded, check digit included)
- `data-module-mils` (optional): minimum X-dimension in mils; the engine
  refuses/warns when the target printer cannot honor it (dot-snapping rule —
  see PRINTING).
- `data-ecc` (qr and rmqr, optional): error-correction level `L | M | Q | H`
  (rmqr supports only `M | H`); default `M`.
- `data-columns` (pdf417 only, optional): data columns 1-30, default 6 —
  more columns = wider and fewer rows.
- `data-dmshape` (datamatrix only, optional): `rect | square` — `rect`
  prefers the ECC200 rectangular formats (8x18 … 16x48), picked to best
  match the target box aspect; content too long for any rectangle falls
  back to a square symbol. Default square.
- `data-tight` (2D symbologies, optional): `1` keeps the target box
  snapped to the symbol's exact drawn extent after editor resizes
  (editor behavior; renderers ignore it).
- `data-logo` (qr only, optional): center logo overlay — `etiq` (the
  built-in Etiquette icon), an image file path (absolute, or relative to
  the label file — one shared file updates many templates), an http(s)
  URL (fetched once per session), or a `data:image/…;base64,…` URI (the
  self-contained form; the editor's Embed/Extract buttons convert
  between file and embedded representations). A logo
  FORCES ECC level `H` and symbol version ≥2; the overlay is sized to
  25% of the symbol side (snapped to whole modules, white 1-module
  keepout) — the measured ceiling at which every symbol still scans in
  two independent decoders (28%+ starts failing small symbols). The
  image is auto-trimmed of transparent/near-white margins so the mark
  fills its box. A missing image renders the code without the logo,
  never broken.
- `data-logo-scale` (with `data-logo`, optional): absent = FILL — the
  image auto-scales to the keepout limit. A number (25-130) sets a
  manual % of the reserved box instead. Either way the image is clamped
  inside the keepout, so the code's modules are never touched and
  scannability is unaffected.

Linear symbols and pdf417 FILL the rect (fill-the-box rule); qr and
datamatrix keep square modules — the largest square rendering centered in
the rect. Quiet zones are the designer's whitespace: keep ≥10 modules
clear around linear codes, ≥4 around qr, ≥2 around datamatrix.

The rect itself is not rendered at print time; generated barcode vectors
replace it, module widths snapped to integer printer dots, total size fitted
within the rect minus snapping remainder.

## Data sources

Field names are declared once in the metadata block so the engine and tools
can validate and prompt:

```xml
<metadata>
  <etiq:label xmlns:etiq="https://etiquette.dev/ns/0.1">
    <etiq:field name="PartNo"  source="epicor"  column="JobAsmbl_PartNum"/>
    <etiq:field name="Qty"     source="prompt"  caption="Quantity:" mask="9999"/>
    <etiq:field name="Serial"  source="serial"  counter="NORTHWIND"
                format="000000" increment="1" alphabet="0-9"/>
    <etiq:field name="Date"    source="auto"    value="date:dd-MMM-yyyy"/>
  </etiq:label>
</metadata>
```

Source kinds:
- `epicor` — engine fills from a REST/BAQ row (`column` = BAQ column name
  — NOTE these are alias-prefixed display-field ids, e.g.
  `JobHead_JobNum`, not bare column names). With `from=` the field reads
  a **declared source** (see "Declared sources" below); without it, the
  engine's single implicit BAQ (legacy labelprint-style configuration).
  `override="true"` lets the operator type over the pulled value at
  print time: a non-empty entry wins, an empty one falls back to the
  fetch.
- `rest`   — generic REST source: `connection` names a profile (base URL,
  auth style, headers) defined in an engine-side data file — connections
  are configuration, never template content; `query` is the
  endpoint/parameters; `pick` selects one value from the JSON response
  as a **dotted path with optional index** (`assets[0].name`) — a closed
  selector, deliberately not JSONPath-the-language. First planned
  profile: GLPI (App-Token + Session-Token). `epicor` predates this and
  stays a first-class kind; it may become a built-in `rest` profile
  internally. `on-fail` applies to `rest` exactly as to `epicor`.
- `prompt` — operator input at print time (caption, mask, default)

Any non-compose field may additionally carry
`case="normal|upper|lower|title"`: the resolved value is normalized before
`if-empty`/`required` run (`normal` is an explicit no-op, same as absent). Casing is
always opt-in per field — never assumed. `title` is ENGLISH title case,
deterministic: lowercase everything, capitalize each word except the
standard small words (a, an, the, and, but, or, nor, for, so, yet, as, at,
by, in, of, off, on, per, to, up, via, vs, v) — which still capitalize as
the first or last word or after : ; . ! ? — and capitalize each part of
hyphen/slash compounds. Normalization is total, so acronyms are lost
("GLPI" → "Glpi"); don't use `title` on fields that carry acronyms. Prompt UIs should mirror the
declared casing while the operator types, but the resolver is the
enforcement point. (Compose fields normalize per segment via the seg
`case=` transform instead.)
- `list`   — one column of a row picked from an EMBEDDED PICK LIST
  (`list=` names it, `column=` picks the column; see "Embedded pick
  lists" below)
- `serial` — issued by a counter service (see SERIALIZATION)
- `auto`   — engine-computed (date, time, station, user; reserved
  additions: `labelindex`, `labelcount`, `copyindex` for "label N of M"
  in batch/merge jobs)
- `fixed`  — constant

Reserved source kinds (specified now so templates and validators know
them; engine implementation stubbed until needed):

- `db` — row lookup against a database connection profile. Same profile
  discipline as `rest`: connection details AND the SQL live engine-side
  in the profile file as **named queries**; the template supplies only
  `connection`, `query` (the query's name) and `param-*` values, plus
  `column` to pick from the result row. Raw SQL never appears in a
  template — that keeps injection out and keeps the template vocabulary
  closed. (`on-fail` applies as for `rest`.)
- `file` — single-value lookup from a tabular file: `path` (local/UNC),
  `column`, `match-column`/`match-value` for row selection, `sheet` for
  workbooks. CSV first; XLSX is feasible dependency-free (zip + XML,
  both in the BCL). NOTE the job-level distinction: iterating a file's
  rows to print one label each is the **batch merge** feature (a job
  concern, see roadmap), not a field source. `source="file"` is for one
  looked-up value on one label.
- `device` — a reading taken from station-attached hardware at print
  time (weigh scale, caliper, temp probe — the BarTender "device data"
  concept). Same profile discipline again: `connection` names an
  engine-side device profile (port, protocol, parse rule); the template
  says only *what* it wants (`connection`, optional `unit`), never how
  to talk to hardware. `on-fail` applies (`block` default — a label
  printed with a missing weight is worse than no label).

Validator: unknown `source` values are errors; reserved-but-unimplemented
kinds validate structurally but fail at print time with "not yet
implemented".

## Declared sources (multi-BAQ fetches)

A template may declare any number of **sources** — one remote fetch each,
yielding one row whose columns any number of fields consume:

```xml
<etiq:source name="LotInfo" connection="Epicor" baq="MFG-LotLabel"
             param-LotNum="{LotNum}" filter-Plant="MfgSys"/>
<etiq:field name="PartNo" source="epicor" from="LotInfo" column="JobHead_PartNum"/>
<etiq:field name="PartDesc" source="epicor" from="LotInfo" column="JobHead_PartDescription"/>
```

- `connection` NAMES a machine-side connection (the designer's
  File → Connections… store) — credentials and URLs never appear in the
  template. A connection may define several **datasets** (Epicor calls
  them environments; for a database it would be the database name):
  which one is live is a machine or per-session choice, never a template
  edit — unless `dataset=` here pins one explicitly.
- `baq` is the BAQ id. `param-Xxx` values feed BAQ **parameters** (the
  name must match a parameter defined in the BAQ); `filter-Xxx` become
  OData `$filter` equality terms (the name must be a result column —
  alias-prefixed). A value of `{FieldName}` resolves that field first
  (prompts, lists, even another source); anything else is a literal.
- One HTTP call per source per label, first row wins. A source whose
  field-fed values are still empty (or mid-entry) does not fetch.
- Validation: sources need `name`/`connection`/`baq`; `from=` must name a
  declared source; `{Field}` references must exist and must not create a
  cycle; a source with no params/filters warns (it would pull the whole
  BAQ).

## Data-panel presentation (etiq:panel)

Optional, one per template — configures the print-station / Data-mode
form without touching the fields themselves:

```xml
<etiq:panel print="direct" printer="embedded" copies="embedded"
            collate="grouped" buttons="print,clear" buttons-at="top"/>
```

- `print` — `dialog` (system print dialog; default) | `direct`
  (straight to the printer, labelprint-style).
- `printer` — direct printing only: absent = machine default; a name =
  pinned; `embedded` = an on-form picker with a "Default printer"
  checkmark.
- `copies` — `ask` (dialog on batch; default) | `embedded` (count
  control on the form; collation control grays out at 1 copy) |
  `fixed:N`.
- `collate` — `choose` (selector on the form; enabled only when a run
  can actually yield more than one page at 1 copy AND copies > 1) |
  `grouped` (1-1-2-2) | `sequenced` (1-2-1-2) | `ask` (no selector — a
  popup asks only when it matters).
- `order` — explicit input order on the panel: comma list of
  `field:Name` / `list:Name` tokens; unlisted inputs follow in
  declaration order. (Edited with Move Up/Down on F4's Panel tab.)
- `buttons` — comma list of `preview`, `print`, `printall`, `clear`
  (default: all), in display order; `buttons-at` = `bottom` (default) |
  `top`.
- Individual inputs opt out with `panel="hide"` on the field or list —
  the field still resolves; it just takes no operator input.

Absent element/attributes reproduce the historical behavior exactly.

## Composed fields (segment lists)

A field may be **composed** of ordered segments instead of having a single
source. Composition is data, not a language: there is no expression syntax,
no scripting, and deliberately never will be — the vocabulary below is
closed, and extending it means extending this spec.

```xml
<etiq:field name="BoxSerial" source="compose">
  <etiq:seg value="DB-"/>
  <etiq:seg ref="PartNo" start="0" len="4" case="upper"/>
  <etiq:seg ref="Date" format="date:yyMM"/>
  <etiq:seg ref="Serial"/>
</etiq:field>
```

Rules:

1. Each `<etiq:seg>` carries exactly one of `value` (literal) or `ref`
   (the name of another declared field) — or is a pure line break,
   `<etiq:seg newline="true"/>`, which contributes `\n` and must carry no
   other attribute.
2. A `ref` must point at a **non-compose** field. Composition is one level
   deep by design — no nesting means no cycles, no evaluator, no surprises.
3. Segments resolve independently, then concatenate left to right. The
   renderer and print paths only ever see the final string; nothing
   downstream of resolution knows composition exists.
4. Per-label memoization: if several segments (or several fields) reference
   the same `serial` field, the counter is consumed **once per printed
   label** and every reference sees the same value.

Per-segment transforms, applied in this fixed order:

| attribute | meaning |
|---|---|
| `start`, `len` | substring (0-based; clamped, never errors) |
| `format` | same formats as `data-format` (`date:…`, `number:…`) |
| `case` | `normal` \| `upper` \| `lower` \| `title` (`normal` = no-op) |
| `pad` | `side:char:width`, e.g. `pad="left:0:6"` |
| `map`, `default` | lookup table (below); `default` when no row matches |
| `sep` | smart separator: emitted **before** this segment's content, but only when the segment resolved non-empty **and** the current line already has content — so a blank State never leaves `City, ` dangling |

### Variant composition (conditional segment lists)

When the segment ORDER itself depends on data — international address
formats being the canonical case — a compose field may carry
`switch-on="FieldName"` and `<etiq:variant>` blocks instead of direct
segments. The switch field's resolved value picks one variant's segment
list; matching mirrors lookup maps (exact `when` beats `prefix`, first in
document order; a variant with neither is the default; no match and no
default blocks the print). An exact `when` may list several values
separated by `|` (`when="DE|AT|CH"`), so one variant covers a whole
format group. Still data, not a language: one switch value, a closed
list of alternatives.

```xml
<etiq:field name="AddressBlock" source="compose" switch-on="Country"
            collapse-blank-lines="true">
  <etiq:variant when="DE">   <!-- zip before city -->
    <etiq:seg ref="Street"/><etiq:seg newline="true"/>
    <etiq:seg ref="Zip"/><etiq:seg ref="City" sep=" "/>
  </etiq:variant>
  <etiq:variant>             <!-- default: US ordering -->
    <etiq:seg ref="Street"/><etiq:seg newline="true"/>
    <etiq:seg ref="City"/><etiq:seg ref="State" sep=", "/><etiq:seg ref="Zip" sep=" "/>
  </etiq:variant>
</etiq:field>
```

Direct segments and variants cannot mix on one field. `switch-on` must
name a declared field that does not itself switch (no chained
switching). It MAY name a plain compose field — the normalization-helper
pattern: a one-seg compose runs the raw value through a map first
(`"Germany"`/`"DEUTSCHLAND"`/`"de"` → `DE`), then the block switches on
the normalized code. Combined with multi-value `when`, dozens of
countries collapse into a handful of variants plus one map — grids of
map rows scale; long lists of variants don't. Field references inside a
segment (`ref=`) still cannot point at compose fields (one level only),
and any circular field reference blocks the print.

### Snippets (reusable metadata templates)

Compositions and lists take real work to build; a snippet packages
fields/maps/lists for reuse: a `*.snippet.xml` file with an
`<etiq:snippet name="…" description="…">` root whose children are ordinary
convention elements. Etiquette ships a few (US and international address
blocks); the editor inserts them (renaming on collision, rewriting the
bundle's internal references) and can save any field with everything it
references as a new snippet. Snippets are tooling files, not part of a
label template.

Blank-line suppression: `collapse-blank-lines="true"` on the compose field
drops lines that end up empty or whitespace-only after composition (the
classic address-block behavior — a missing Address2 line closes up).

```xml
<etiq:field name="AddressBlock" source="compose" collapse-blank-lines="true">
  <etiq:seg ref="Name"/>
  <etiq:seg newline="true"/>
  <etiq:seg ref="Address2"/>          <!-- blank → whole line vanishes -->
  <etiq:seg newline="true"/>
  <etiq:seg ref="City"/>
  <etiq:seg ref="State" sep=", "/>    <!-- comma only if both sides exist -->
  <etiq:seg ref="Zip" sep=" "/>
</etiq:field>
```

## Lookup maps

A named, closed substitution table — the only "conditional" beyond
`if-empty`. Declared once in the metadata block, referenced from segments:

```xml
<etiq:map name="Plants" default="XX">
  <etiq:when from="SPRINGFIELD" to="SP"/>
  <etiq:when from="MONTERREY"   to="MO"/>
  <etiq:when prefix="22"     to="GREEN"/>
</etiq:map>
```

- `from` = exact match (after the segment's other transforms), `prefix` =
  starts-with; exact rows win over prefix rows, then document order.
- `default` on the map (or `default` on the referencing segment, which
  wins) applies when nothing matches; if neither is present, a non-match
  is a **print-time validation error** (blocks the label, names the field).

## Embedded pick lists

A template can carry its own small data sets — common ship-to addresses,
department blocks, and the like — so they travel WITH the file:

```xml
<etiq:list name="ShipTo" key="Name" default="Springfield Plant">
  <etiq:row Name="Springfield Plant"  Addr="100 Example Ave" CityLine="Springfield, IL 62701"/>
  <etiq:row Name="Monterrey Plant" Addr="Av. Ejemplo 100"  CityLine="Monterrey, N.L."/>
</etiq:list>

<etiq:field name="ShipName" source="list" list="ShipTo" column="Name"/>
<etiq:field name="ShipAddr" source="list" list="ShipTo" column="Addr"/>
<etiq:field name="ShipCity" source="list" list="ShipTo" column="CityLine"/>
```

- Row columns are the `etiq:row` attributes; `key=` names the column the
  operator sees and selects by; `default=` preselects a row.
- Selection is ONE ROW PER LIST per job: every field bound to the same
  list follows the same selection — that is how a pick fills a whole
  address block as a set. Want two independently selectable values?
  Declare two lists (even single-column ones). That is the entire
  separately-vs-set configuration — no extra flag.
- No selection and no default ⇒ blocks at print time naming the field.
  A column missing from the selected row resolves empty (if-empty
  applies as usual).
- Print UIs render one dropdown per list; `etiq resolve` takes
  `--choose List=Key`.

**Picker presentation and filtering.** Optional attributes on `etiq:list`
shape the operator's data-entry UI (resolution semantics are unchanged):

- `caption="Customer:"` — the label shown next to the picker; default is
  the list name. Entry ORDER on the panel follows field declaration
  order: a prompt appears where it is declared, a list where its first
  bound field is declared.
- `display="FieldName"` — a declared field resolved PER ROW (with that
  row selected) supplies the picker text; a compose over several of the
  list's columns works, so one picker line can read
  "Adient — Warren, MI". Absent → "key — first column" heuristic.
- `filter-column="Region" filter-ref="RegionPrompt"` — the picker offers
  only rows whose column equals the resolved value of the referenced
  field (typically a prompt); an empty filter value offers all rows.
  Both attributes or neither (validator enforces; a `filter-ref` that
  reads the list it filters is a circularity error).

## Conditions

The complete condition vocabulary — an enum of behaviors, not syntax:

- `if-empty="TEXT"` on any field or segment: substitute the literal when
  the resolved value is empty/whitespace.
- `required="true"` on any field: empty after `if-empty` ⇒ block printing
  with a message naming the field. (`prompt` fields with `required` won't
  accept an empty entry in the first place.)
- `map`/`default` on segments, as above.

That's all of them. A condition that doesn't fit these shapes is a sign
the logic belongs upstream (in the BAQ, the Kinetic Function, or the
operator's head) — not in the template.

## Print-time failure policy (epicor/rest fields)

Fetch is easy; offline isn't. Every `epicor` or `rest` field carries an
explicit policy for REST failure (timeout, non-2xx, missing column/path),
chosen at design time so nobody decides during a line-down moment:

```xml
<etiq:field name="PartNo" source="epicor" column="JobAsmbl_PartNum"
            on-fail="block"/>
```

- `on-fail="block"` — **default**: refuse to print, show the error.
- `on-fail="cached"` — use the last successfully fetched value for the
  same query key, if the engine has one (engine keeps a small local
  cache); no cache entry ⇒ behaves like `block`. Printed output using a
  cached value is flagged in the job log.
- `on-fail="use:TEXT"` — substitute a fixed literal (for cosmetic fields
  only; validators warn when a barcode consumes an `on-fail="use:"` field).

## Layers

A layer is nothing more than a **direct child `<g>` of the SVG root**
carrying `data-layer="Name"`. Document order = z-order (first = bottom).
This is exactly how Inkscape models layers (a group with an attribute),
so interop is inherent, not emulated.

```xml
<g data-layer="Frame" data-locked="true"> …static art… </g>
<g data-layer="Fields"> …dynamic text and barcodes… </g>
<g data-layer="Guides" data-print="false" display="none"> …notes… </g>
```

- `data-layer` — layer name, unique within the template.
- `data-locked="true"` — **editor-only**: objects not selectable/movable.
  Engines ignore it.
- `data-print="false"` — excluded from every render path (driver, ZPL,
  PDF) regardless of `display`; for guides, margins, designer notes.
- Layer groups appear only at the top level; groups nested deeper are
  ordinary groups. "Promote group to layer" is: move to top level, add
  `data-layer`.
- Top-level content outside any layer group stays legal (0.1 compat) and
  is treated as an anonymous bottom layer.
- Editors MAY additionally mirror `inkscape:groupmode="layer"` +
  `inkscape:label` (namespaces declared on the root) so Inkscape shows
  the same layers natively; engines ignore those attributes entirely.

## Serialization

`source="serial"` fields reference a named **counter**, not local state.
Counters are backed by a pluggable provider; the reference implementation is
an Epicor Kinetic Function doing an atomic read-increment on a UD table
(server-side transaction ⇒ duplicates impossible across stations). `format`
is a padding/format mask; `alphabet` supports base-36 serial schemes.
Templates never store the current value.

## Printing

Engine pipeline: resolve fields → substitute text → generate barcode vectors
(Zint) → render:

- **driver path** (any Windows printer): render at the queue's native DPI,
  1:1 dot mapping, explicit DEVMODE resolution; barcodes drawn module-snapped
  and un-antialiased
- **raw thermal path**: 203/300 dpi mono raster wrapped in ZPL (`^GF`), or
  native commands where fidelity allows
- **PDF**: same render to file, for proofs/archive

## Validation (`etiq validate`)

- every `data-field` has a metadata declaration and vice versa
- barcode rects: known symbology, non-degenerate size, module-mils feasible
  on declared target printers
- serial fields reference a declared counter
- no text outside the label bounds

Added in 0.2:

- compose fields: every `<etiq:seg>` has exactly one of `value`/`ref`;
  every `ref` resolves to a declared non-compose field; `pad` parses as
  `side:char:width`
- every segment `map` resolves to a declared `<etiq:map>`; maps with no
  `default` anywhere get a warning ("non-match will block at print time")
- `on-fail="use:"` feeding a barcode ⇒ warning
- layer names unique; `data-layer`/`data-locked`/`data-print` only on
  direct children of the root; `data-print="false"` layers containing
  `data-field` elements ⇒ warning (bound but never printed)
- `data-overflow` requires `data-width`; `data-overflow="wrap"` requires
  `data-height`; `data-width` box (per justification) must fit within
  label bounds
- `rest` fields: `connection` resolves to a defined profile; `pick`
  parses as dotted-path-with-index (nothing else)
- lists: unique non-empty names; `key=` required, present and unique on
  every row; at least one row; `default=` matches a row. `list` fields:
  `list=` resolves, `column=` present on at least one row (warning when
  only on some)
