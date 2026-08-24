# Changelog

All notable changes to Etiquette are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/); versions follow semver
(pre-1.0: minor bumps may change behavior).

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
