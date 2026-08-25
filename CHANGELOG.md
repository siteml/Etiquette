# Changelog

All notable changes to Etiquette are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/); versions follow semver
(pre-1.0: minor bumps may change behavior).

## [Unreleased]

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
