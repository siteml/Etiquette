# Roadmap

Where Etiquette is and where it's going. Current release: **v0.7.0**
(see the Releases page; `etiqedit` checks it via Help → Check for Updates).

Legend: `[x]` done · `[~]` partially done · `[ ]` not started.

## Shipped — v0.7.0

- [x] **Named connections + datasets** — machine-side credential store
      (DPAPI), declared `etiq:source` BAQ fetches referenced by name,
      dataset (environment) switching per machine/session, `.etiqcreds`
      provisioning bundles, F4 Sources tab, overrideable pulled fields.
- [x] **Print-station mode** — persisted locked shop-floor presentation
      (left-pane data entry, auto-fitting preview, deliberate unlock),
      with a per-template configurable data panel (`etiq:panel`: inputs,
      buttons, order, embedded copies/collation/printer, direct print).
- [x] **Editor safety & ergonomics** — unsaved-changes guard, File >
      Close / Open Recent, menu gray-out, Clear button, DPI/system-font
      scaling, inline-editor autosize, assorted Data-mode fixes.

## Shipped — v0.6.0

- [x] **Four new symbologies, decode-verified** — GS1-128 (parenthesized
      AI syntax, automatic FNC1 separators), ITF-14 (automatic GS1 check
      digit), rMQR (ISO/IEC 23941, all 32 versions; version follows the
      target box aspect), and Aztec (ISO/IEC 24778, compact and full).
      `iqr` was removed: the spec was never published openly and no open
      decoder exists to verify against — `rmqr` covers rectangular needs.
- [x] **Rectangular DataMatrix** — the six ECC200 rectangle formats
      (8x18 … 16x48), selected by the target box aspect
      (`data-dmshape="rect"`).
- [x] **HRI rendering** — `data-hri="below|above"` now draws the
      human-readable line inside the target box for all linear codes
      (for ITF-14 it shows the digits actually encoded, check digit
      included).
- [x] **Editor: tight bounding boxes** — 2D symbols can keep their box
      snapped to the exact drawn symbol through resizes (`data-tight`).
- [x] **Editor performance & feel** — inspector control-set caching
      (instant selection switching), leak fixes (snappy exit), click
      dead-zone (no accidental nudges), selection-over-z-order digging,
      fit-none clip-edge handles, inline multiline editing fixes.

## Shipped — v0.5.0

The suite is usable end to end for the design → data → print loop:

- [x] **Label convention 0.2** — plain SVG + an `etiq:` metadata block
      (`docs/convention.md`). Templates preview in a browser, diff in git.
- [x] **Barcode encoders, dependency-free** (`src/Etiq.Core`) — Code 128,
      Code 39 (+extended), QR versions 1–40 (all ECC levels), DataMatrix
      (ECC200), PDF417 (GF(929)). Every encoder is decode-verified against
      two independent readers; `tests --dump-barcodes` emits SHA-256
      vectors for external verification.
      *(This supersedes the earlier "use ZXing.Net" plan — no NuGet
      dependency was needed.)*
- [x] **QR logo overlay** — QR only / project logo / custom image, with
      path-or-URL sourcing, embed-as-data-URI and extract, ECC H with a
      forced minimum version, a 25 % side ceiling and keep-out ring so
      codes stay scannable (verified against both decoders).
- [x] **Field resolution** — one resolver, used everywhere: prompts,
      embedded pick lists, composition with conditional variants, lookup
      maps, serial counters, dates, autos, and REST sources with
      per-field failure policies.
- [x] **Snippets** — reusable metadata templates; ships a US address block
      and a country-aware international address block (~35 countries).
- [x] **Counters** — `ICounterProvider`, `LocalFileCounterProvider`, and an
      Epicor Kinetic Function implementation with a setup guide
      (`docs/counters.md`). The Kinetic-side Function is created per
      tenant by the operator.
- [x] **Media + printer registry and feasibility checks** — label fit in
      both feed orientations, barcode module vs. dot pitch, nearest-dot
      snap deviation. Example config in `config/*.example.json`.
- [x] **REST connection profiles** — `none | headers | basic | glpi` auth
      kinds, dotted-path JSON pick, secrets DPAPI-wrapped, never plaintext.
- [x] **`etiq` CLI** — `validate`, `resolve` (dry-run binding debugger),
      batch merge from CSV (RFC 4180 reader, no dependencies).
- [x] **`etiqedit` designer** — see Designer below.
- [x] **Native print path** — GDI text/graphics rendering shared by canvas,
      preview and printer, plus a ZPL raster path (`^GFA`, Zebra hex
      compression) for thermal printers. No NuGet dependencies.
- [x] **Public release + updater** — MIT, published repository, release
      workflow producing both a self-contained and a framework-dependent
      build; in-app update check that picks the right flavor and applies
      the update in place, with a browser-download fallback where the
      install directory isn't writable.

## Designer (`etiqedit`)

- [x] Headless editor core (`src/Etiq.Editor.Core`, cross-platform, unit
      tested): **the XML is the model** — typed object/layer wrappers are
      views over live `XElement`s, so foreign attributes, elements and
      comments (Inkscape's included) round-trip untouched. Undo stack with
      merge keys (a drag is one undo), geometry, rotated hit-testing,
      injectable text measurement.
- [x] WinForms shell: canvas with zoom-at-cursor, pan, fit; selection
      chrome and handles; drag move/resize with grid snap; arrow-key
      nudge; outline tree.
- [x] **Groups** — full model, group/ungroup, group-unit transforms.
- [x] **Layers** — named top-level `<g>` groups with editor-only
      visible/lock/no-print flags; raise/lower/merge/delete; move objects
      to a layer as one undo step. A group cannot span layers;
      cross-layer grouping moves into the first member's layer after
      confirmation.
- [x] **Inline text editing** — double-click a text object for a
      zoom-scaled overlay editor.
- [x] **Inspector panel** — per-kind, symbology-aware property rows with
      nudge keys and full undo (replaced the original PropertyGrid
      adapter).
- [x] **Metadata editor** — data fields, lookup maps, embedded pick lists,
      composition with a compose dialog and snippet library.
- [x] **Design / Data modes** — Data mode is the print-station guard: no
      layout interaction, no-print layers hidden, prompt entry with
      searchable pickers, auto preview, gated Print, and Print All with
      collation control.
- [x] Toolkit decision (2026-08-02): WinForms over Avalonia/MAUI, because
      the canvas GDI+ stack *is* the driver print path's text stack, so
      preview metrics match printed output; the fleet is Windows-only.
      Cheap to revisit — everything hard lives in the cross-platform core.

### Designer — next up

- [ ] Series generation (parameters still to be pinned)
- [ ] Dirty-document close guard (Exit and update-restart never prompt to
      save today)
- [ ] Rotate handle drag, line endpoint handles, rotated-resize snapping
- [ ] Marquee touch-select and multi-object resize
- [ ] Layer dimming; add-object palette
- [ ] HRI rendering on canvas; `data-overflow="wrap"`
- [ ] Connections editor; Epicor context for printing from the editor
- [ ] "Merge line stack" inverse; live compose preview
- [ ] `tspan` export; production counter store

## Legacy `.btw` import

BarTender `.btw` import lives in the **separate private repository
`etiquette-btw`** and is picked up by a conditional project reference when
cloned alongside; this repository builds and runs fully without it. Its
roadmap lives there. See [PROVENANCE.md](../PROVENANCE.md) for why the
boundary exists.

## Print engine — remaining

- [ ] PDF export path
- [ ] Live thermal print test on the ZT230 (raw-ZPL `^GF` path)
- [ ] Epicor Kinetic Function created per tenant per `docs/counters.md`
- [ ] Production counter store (replacing the temp-file serial store)

## Interop

- [x] `etiq validate` — field cross-check, barcode sanity, units/bounds,
      printer feasibility
- [ ] Inkscape extension pack (fallback/interchange path): mark-as-field,
      insert barcode, label properties, validate, test print

## Later / maybe

- **b-PAC output path** (Brother PT-series tape printers). PT-D410 is
  confirmed supported in b-PAC SDK 3.4 — an official COM SDK, so media
  width query, per-job auto/half-cut and chain printing need no reverse
  engineering. Design: a stub `.lbx` with one full-bleed image object; the
  engine renders the template to a mono bitmap at printer dpi and b-PAC
  sets the image and cut options. A third output path beside driver and
  raw-ZPL, behind the same interface, Brother-only, never in core. Costs a
  vendor runtime install on the station. Until then the D410 prints via the
  plain driver path with queue-default cut settings.
- **GLPI equipment ID tags** (QR on P-touch tape) — the data side shipped
  (unreleased, post-0.7.0): `glpi` connection type + `etiq:query` item
  fetches by id or column filter, `examples/glpi-asset-tag.svg`. The
  D410 prints through the plain driver path; the b-PAC path above stays
  optional polish (cut control, media-width query).
- JRXML and RDL exporters
- Non-Windows print paths (CUPS raw)
