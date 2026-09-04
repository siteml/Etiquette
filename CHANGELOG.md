# Changelog

All notable changes to Etiquette are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/); versions follow semver
(pre-1.0: minor bumps may change behavior).

## [0.9.0] — 2026-09-04

### Added
- **Prompt defaults** — `default=` on a `source="prompt"` field prefills
  the data-panel input, and **Clear** restores it instead of blanking.
- **Field editor pane** — the F4 Fields tab replaces the property grid
  with a purpose-built pane: only the attributes that apply to the
  selected source, in a sensible order, with hints under each input and
  live dropdowns (declared queries, lists, the chosen list's columns,
  the machine's connections, case / on-fail value sets). Driven by a
  single spec table, so schema changes stay one-line edits.
- **Panel tab: Log button** — the print-log panel button is now a
  checkbox next to Preview/Print/Print All/Clear (opt-in; the default
  button set is unchanged).
- **Template version stamp** — saving writes
  `generator="<version>"` on `etiq:label`; opening a template saved by a
  NEWER Etiquette warns that it may use features this build lacks
  (older templates open silently, as before).

### Changed
- **Clear vs pick lists** — Clear now RESETS list-sourced dropdowns to
  their `default=` row (or the first row) rather than emptying a picker
  that has a defined set of entries; prompt boxes still clear (to their
  `default=`, if any).

### Fixed
- **Update flavor switch** — version comparison normalized to three
  components (assembly `0.8.0.0` vs tag `0.8.0`), so Check for Updates
  correctly offers the framework-dependent/self-contained switch again.
- **F4 Queries tab** — the params/filters grid no longer clips under the
  hint text at any window width or display scale (the hint is measured,
  the grid placed below it).
- **Compose dialog** — details-pane rows align vertically; map default
  "blank" checkbox semantics (`default=""`) with `\` escape for a
  literal leading backslash.

## [0.8.0] — 2026-09-04

### Added
- **GLPI connections** — a new connection type (`glpi`: API endpoint,
  App-Token, user token; both tokens DPAPI-protected, Test opens and
  closes a real session). An `etiq:query` on a GLPI connection names the
  item type (`query="Computer"`, `Monitor`, `NetworkEquipment`, `Printer`,
  …) and picks one item by `param-id` or by `filter-<column>` (serial,
  otherserial, name…; GLPI's substring search is narrowed to an exact,
  case-insensitive match). Dropdown foreign keys come back expanded, so
  `locations_id` is the location's name. Fields consume a GLPI query with
  `source="rest" from="…" column="…"` — `override="true"` works exactly as
  for Epicor pulls. First non-Epicor consumer of the connections platform;
  the concrete use case is equipment ID tags on a Brother P-touch (any
  Windows-driver tape printer prints through the normal driver path).
  `examples/glpi-asset-tag.svg` is an 18 mm-tape starter. `query=` may
  itself be `{Field}` (a pick list of asset classes), and GLPI rows carry
  virtual `model` / `type` / `manufacturer` / `location` columns so one
  template spans every class.
- **Query-fed pick lists** — `etiq:list from="QueryName"` fills a picker
  from ALL rows of a declared query (live inventory, paged, background
  fetch in the editor) instead of embedded rows; the chosen row feeds
  `source="list"` fields as usual. First use: pick the asset by inventory
  number from GLPI. F4 Lists tab: "Rows from query".
- **Help > Options > Print offset** — per-printer x/y nudge in mils for
  drivers whose reported hard margin doesn't match where the image
  lands (tape printers typically print a few mils low). Stored by
  printer name; applied to whichever printer a job goes to.
- **Compose dialog, decluttered** — the segment grid shows only newline /
  value / ref / sep plus a one-line "transforms" summary; selecting a row
  edits its transforms in a grouped pane (piece: split/part/start/len ·
  format/case/pad/if-empty · map/default). A live **Preview** line shows
  the composed result with sample values as you edit.
- **Segment `split` / `part`** — keep one delimited piece of a value
  (`split="&gt;" part="-2"` on "Site > Bldg > Room" → "Bldg"; negative
  counts from the end). Runs before start/len.
- **Connections export picks connections** — Export… now asks which
  connections go into the bundle (current one pre-ticked), so a GLPI
  print station never receives ERP credentials.
- **Print log** — every print appends JSON-lines records (monthly files,
  default %APPDATA%\Etiquette\logs; settings `printLog=off` /
  `printLogDir` for a shared location): timestamp, template, printer,
  station/user, and the resolved values exactly as printed. A spooler
  watcher then records the job's real fate — completed, error (offline /
  out of tape), or stuck in the queue. File > Print Log… views it with a
  configurable look-back (default 1 month, not capped to it) and can
  REPRINT any record verbatim; print-station mode reaches the same viewer
  via `buttons="…,log"` on etiq:panel or the deliberate Ctrl+Shift+L
  chord. The log is the future series manifest, v0.
- **View > Fit to Window** — Ctrl+0, or double middle-click on the canvas.
- **File > Label Size…** — shows the label's physical size and resizes
  it (width/height/unit; undoable). Content keeps its position; the
  print page follows the new size.

### Fixed
- **Print page size** — the native print path now declares the label's
  own size as a custom paper form (portrait form + landscape rotation,
  zero margins) instead of inheriting the driver's default form. A
  Brother tape driver previously cut every label at its 100 mm default;
  an office printer placed the label on a Letter page.
- Templates whose queries were authored as `etiq:query` (the F4 spelling
  since the rename) never fetched in the editor — the preview only looked
  for the old `etiq:source` element.

### Changed
- `etiq:source` (the declared read-only remote fetch) is renamed
  **`etiq:query`** — "source" already means a field's value-kind, and
  queries are strictly read-only (unlike the planned `etiq:series`
  transactions, see docs/series.md). The old spelling parses forever;
  F4 writes the new name.

## [0.7.0] — 2026-08-25

### Added
- **Named connections with datasets** — templates can now declare remote
  data sources (`etiq:source`, e.g. an Epicor BAQ with parameters/filters
  fed by prompt fields) that reference a machine-side connection by NAME
  only; credentials never enter the template. Each connection can define
  multiple datasets (Epicor environments, database names, …) and which one
  is live is a machine or session choice — a toolbar picker (amber when
  overriding) switches everything for testing without touching templates.
  Secrets are DPAPI-protected per machine; File > Connections… manages the
  store, and a password-protected `.etiqcreds` bundle (Export/Import or
  `etiqedit --import-connections file`) provisions new machines — a
  bundle found next to the exe is offered for import (and cleanup) on
  startup, and a Test button verifies a connection's reachability and
  authentication in plain language.
- **Configurable data panel** (`etiq:panel`, edited on F4's new Panel
  tab): choose which action buttons exist (Refresh Preview / Print /
  Print All / Clear) and whether they sit above or below the fields; put
  the copy count, collation, and a printer picker (with a "Default
  printer" checkmark) directly on the form; hide individual inputs
  (`panel="hide"` — the field still resolves) and reorder them with
  Move Up/Down; and set `print="direct"` for labelprint-style printing
  that goes straight to the chosen printer with no system dialog. The
  collation selector enables only when a run can actually interleave
  (a multi-label batch at 2+ copies), and `collate="ask"` hides it
  entirely, asking in a popup only when it matters.
- **Overrideable pulled fields** — `override="true"` on an epicor field
  shows an input whose ghost text is the fetched value: leave it empty to
  use the pull, type to override. F4 gained a **Sources** tab for
  declaring BAQ fetches (connection, parameters/filters fed by fields)
  without touching XML.
- Data mode now mirrors the classic labelprint layout: data entry lives in
  the LEFT pane (where the outline sits in Design mode) and the inspector
  side collapses, giving the label preview the full remaining width.
- A Clear button in the data pane blanks every prompt and picker after a
  print job, ready for the next one.
- File > Close (Ctrl+W) and File > Open Recent — the most recently used
  files are remembered (how many is configurable in Help > Options;
  default 10). Menu actions that can't run right now (Undo with nothing
  to undo, Group without a multi-selection, Save with no document, …)
  are grayed out.
- Closing the window, opening a file, or File > New with unsaved changes
  now asks to save first (Yes / No / Cancel) instead of silently
  discarding work.
- Changing the update-download preference (standalone ↔
  framework-dependent) in Help > Options now takes effect even with no
  new release: the version check offers switching to the alternate
  package of the SAME version, verifying first that the .NET 8 Desktop
  Runtime is present when switching to the framework-dependent build.
- Print-station mode never runs the startup update check — stations
  value stability; update by exiting station mode, updating manually,
  and re-entering.
- **Print-station mode** — a stripped-down presentation for dedicated
  print stations: no menu, toolbar, outline or inspector, just the label
  preview (auto-fitting the window) and the data-entry/print panel.
  View → Enter Print-Station Mode persists it, so etiqedit opens straight
  into the station view on every start until it is explicitly turned off
  (Ctrl+Shift+F12, then typing UNLOCK — no accidental exit).
  `etiqedit --station <file>` runs it for a single session.

### Fixed
- A print station whose template file went missing no longer falls open
  into the full editor: it stays locked, explains which file is missing,
  and still exits only via Ctrl+Shift+F12 + UNLOCK.
- Opening a file while in Data mode no longer leaves the data pane blank
  (a hidden inspector window could end up covering it).
- The dropdown text in a freshly built inspector pane is no longer left
  selected/highlighted on first visit.
- The inline text editor now grows and shrinks with its content while
  typing.
- The editor now scales its layout correctly on machines with 125/150%
  display scaling or an enlarged system font — controls, dialogs,
  splitters and the inspector column grow with the text instead of
  clipping it.
- In Data mode, clicking the outline tree no longer re-activates the
  design selection machinery (the tree is disabled while data mode locks
  layout interaction).

## [0.6.0] — 2026-08-24

### Added
- **Four new symbologies**, all dependency-free and decode-verified against
  an independent reader:
  - `gs1-128` — Code 128 with FNC1: parenthesized GS1 Application
    Identifier syntax (`(01)09501101530003(10)LOT42`), automatic FNC1
    separators after variable-length AIs, fixed-length AI validation.
  - `itf14` — Interleaved 2 of 5; exactly 13 digits get the GS1 check
    digit appended automatically.
  - `rmqr` — rectangular QR (ISO/IEC 23941), all 32 versions R7x43 …
    R17x139, ECC M/H; the symbol version is chosen to best match the
    target box aspect.
  - `aztec` — ISO/IEC 24778, compact and full symbols; needs no quiet zone.
- **Rectangular DataMatrix** — the six ECC200 rectangle formats
  (8x18 … 16x48) via `data-dmshape="rect"`, selected by target box aspect.
- **HRI rendering** — `data-hri="below|above"` now draws the
  human-readable line inside the target box for all linear symbologies;
  for `itf14` it shows the digits actually encoded, check digit included.
- **Tight bounding boxes** (editor) — 2D symbols can keep their box
  snapped to the exact drawn symbol through resizes (`data-tight="1"`).
- Inspector: live encode-status rows for `itf14` (shows the appended
  check digit) and `gs1-128` (AI syntax validation), an ECC selector for
  `rmqr`, and a Rectangular toggle for DataMatrix.
- **Update experience** — the update prompt now shows this changelog,
  rendered, with *Install now*, *Skip this version*, and *Remind me
  later*; a new Help → Options dialog controls the startup update check,
  the download flavor, and skipped releases.
- A `CHANGELOG.md` (this file).

### Changed
- Editor performance overhaul: the inspector caches built control sets per
  element type, so switching between selected elements is instant; several
  native-handle leaks fixed (snappy exit); redundant refresh passes removed.
- Editor feel: a small click dead-zone prevents accidental nudges when
  selecting; the current selection wins over z-order when clicking
  overlapping elements (select a buried element in the outline, then drag
  it on the canvas); fixed-height (`data-fit="none"`) text boxes resize by
  their clip edges; inline multiline text editing renders correctly and
  only closes when clicking outside it.

### Removed
- The `iqr` symbology. Denso Wave never published the iQR specification
  openly and no open-source decoder exists to verify against, so it could
  never meet this project's decode-verified standard. Use `rmqr` for
  rectangular symbols or `qr` for square ones. Templates naming `iqr` now
  fail validation with an unknown-symbology error.

## [0.5.0] — 2026-08-20

Initial public release.

- Plain-SVG label convention 0.2 (`docs/convention.md`) — templates
  preview in a browser and diff in git.
- Dependency-free, decode-verified encoders: Code 39 (+extended),
  Code 128, QR v1-40 with center-logo overlay, DataMatrix ECC200, PDF417.
- `etiqedit` designer: layers, groups, inspector panel, inline text
  editing, grid/element snapping, fit modes, data mode with live preview
  and print, variant compose.
- `etiq` CLI: validate, render, print; field resolution from prompts,
  serials, lists, REST sources.
- Auto-apply updater with standalone / framework-dependent flavors.
