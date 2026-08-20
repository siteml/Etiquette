# Roadmap

## Phase 0 — recon (done)
- [x] Crack the 10.1 .btw container (header / previews / zlib archive)
- [x] Object class inventory across ~490-file corpus
- [x] Units identified (mils, int32 LE); LineData geometry decoded
- [x] Serialization record decoded (increment/rollover/alphabet/prompt)
- [x] Corpus tooling: inventory, preview extraction, gallery PDF, serialization census

## Phase 1 — field maps
- [x] Text/barcode anchor positions (x,y mils) — decoded + corpus-verified
      97.7% (tools/recon/btwgeom.py; docs/btw-format.md "Object positions")
- [x] Font height (mils), weight, rotation — corpus-mined LOGFONT block
      (docs/btw-format.md "Font block")
- [x] Justification flag (0-4 = L/C/R/J/D) — pinned via controlled-edit
      pairs 2026-07-19; corpus is ~100% left
- [x] Position reference point (enum 0-8 grid; anchor stores the ref
      point's own location) — pinned via controlled-edit pair 2026-07-20
- [x] Text box width (post-value blob, after justification) — confirmed by
      anchor-shift math
- [ ] Text box height (implied by geometry; stored form not yet found)
- [ ] Barcode symbology parameters
- [ ] Serialization enable flag + live counter value location
- [ ] Fonts, rotation, colors, inverse-print flags
- [ ] Pre-10.1 container variant (17 files in reference corpus)

## Phase 1.5 — C# port of recon tools
- [x] Container parser (Etiq.Core/BtwFile.cs) — compiled, tested, full parity vs btwtool.py on the 491-file corpus
- [x] gallery + census commands in etiq CLI (dependency-free PNG decoder + PDF writer in Etiq.Core)

## Phase 2 — converter (`etiq convert`)
- [x] Scaffold (2026-08-02): BtwGeometry.cs (C# port of btwgeom.py +
      width/value-string extraction), BtwConverter.cs (0.2 SVG emit w/
      layers + data-width/overflow), `etiq convert <dir> [out]` writing
      .svg + fidelity.tsv, converted output auto-validated. Synthetic
      geometry doc exercises every decoded finding (tests green).
- [ ] .btw → Etiquette SVG + metadata — remaining: barcode box sizes +
      symbology params, value→field binding recovery, box height, faces,
      colors; corpus run on the main machine to calibrate heuristics
      (value-string locator is low-confidence until corpus-verified)
- [ ] Per-label fidelity report (SSIM vs embedded preview) — needs the
      SVG renderer (Svg.Skia, Phase 3); fidelity.tsv v1 is structural
- [ ] Corpus batch mode with triage ranking

## Phase 3 — print engine (`etiq print`)
- [ ] Build the engine in C#/.NET 8 (WinForms GUI, self-contained single-file exe; labelprint in reference/ is the Go prior art):
      GUI + CLI, Epicor REST data source, prompts, copies
      (done early: EpicorClient.cs — BAQ + efx REST, mock-tested;
      CredentialStore.cs — DPAPI via P/Invoke, needs on-Windows test)
- [x] Counter provider interface + Epicor Function reference implementation
      — done early: ICounterProvider/EpicorCounterProvider/SerialFormat +
      LocalFileCounterProvider (Counters.cs); setup docs in docs/counters.md
      (Function itself still to be created in Kinetic per that doc)
- [x] Raw-ZPL raster path — done early: ZplRaster.cs (^GFA, Zebra hex
      compression, round-trip tested); needs a live ZT230 print test
- [ ] SVG resolve → render: driver path (DEVMODE DPI pin, dot-snapped
      barcodes), raw-ZPL raster path, PDF export
- [ ] ZXing.Net barcode generation; Svg.Skia rendering
- [x] Batch merge expansion (2026-08-02, gLabels merge model):
      BatchRunner.Run(template, records, ctx, copies) → ResolvedLabel list
      (record columns feed epicor fields, so BAQ-designed templates merge
      from CSV unchanged; fresh resolver per label = per-label serials;
      prompts asked once per job; autos labelindex/labelcount/copyindex/
      recordindex; blocking failure names label+record). Csv.cs = dep-free
      RFC 4180 reader. `etiq resolve <svg> [--set F=V] [--csv f] [--copies N]`
      = dry-run binding debugger (throwaway local counter). Remaining:
      hook to the render/print stage when it exists.
- [x] Minimal media + printer registry (2026-08-02): Registry.cs loads
      printers.json + media.json (deliberately lean — the fleet + shop
      stocks, not an Avery universe); FeasibilityChecker checks label fit
      (both feed orientations — rotated-feed = warning) and barcode
      module-vs-dot-pitch (sub-dot = error, single-dot = warn, >20%
      nearest-dot snap deviation = warn). Wired into
      `etiq validate <dir> [configDir]`. Example data:
      config/*.example.json (copy to printers.json/media.json and edit
      real fleet values).
- [x] REST connection profiles (2026-08-02): RestClient.cs +
      ConnectionProfiles (config/connections.example.json) — kinds
      none|headers|basic|glpi (GLPI initSession dance implemented,
      session cached, mock-tested); JsonPick evaluates the convention's
      dotted-path pick; plugs into FieldResolver via ctx.Rest. Secrets
      dpapi-wrapped via CredentialStore, never plaintext in config.


## Phase 4 — designer support
Decision 2026-08-02: build our own lightweight WYSIWYG label editor
(BarTender 5.1-class object model, from scratch — clean-room, no BT code
or assets copied) instead of leaning on Inkscape as the primary editor.
Inkscape stays as free interchange/fallback because the editor reads and
writes the same Etiquette SVG. Rationale: hard parts already exist
(format semantics, GDI renderer prior art in reference/nifcotag,
ZplRaster, validate); Inkscape has no data-source/barcode object concept
and the wrong mental model for shop-floor edits. Cost lives in
interaction (selection handles, rotated hit-testing, snapping, undo/redo,
in-place text edit, zoom) — plan weeks, not days.

Pinned scope decisions (2026-08-02):
- Data binding = SEGMENT LIST, not a language. A field is ordered
  segments: literal | counter | date/time(format) | prompt-at-print |
  REST-pick | substring/pad transform. Concatenate at print time;
  renderer only ever sees the final string (GDI/ZplRaster untouched).
  Counters.cs is the counter segment; EpicorClient is the REST segment.
- Conditions are a CLOSED VOCABULARY (if-empty-use-X, lookup-table map,
  prefix-match) — an enum of behaviors, never syntax. No expression
  evaluator, no scripting, no DB connections (that's the 10.1 scope
  bomb; slot back in post-maturity if ever needed).
- REST segments need a per-field print-time failure policy decided up
  front: cached-last-value vs block-print (fetch is easy; offline isn't).
- Bindings live in the model; resolution happens in ONE place before
  render; nothing downstream knows fields exist.
- Layers = named top-level SVG `<g>` groups + editor-only visible/lock
  flags. Document order = z-order. Zero file-format cost, Inkscape
  interop stays exact; "promote group to layer" is just a rename.

- [x] Headless editor core (2026-08-02, src/Etiq.Editor.Core, net8.0, no
      WinForms — unit-tests cross-platform): THE XML IS THE MODEL — typed
      object/layer wrappers are views over live XElements, so foreign
      attributes/elements/comments (Inkscape's included) round-trip
      untouched. EditorDoc (layers, add/remove, z-reorder, promote-group-
      to-layer, hit-test front-to-back skipping locked layers, validate);
      EditorObject (text/barcode/line/box/image; bounds w/ injectable
      ITextMeasurer — GDI metrics come from the shell; rotated hit-testing;
      undoable Move/Resize/SetRotation/SetText); Geometry (rotate,
      point-to-segment, 8 handles + rotate handle, ResizeBy w/ min-size,
      grid snap); EditCommand/UndoStack w/ merge keys (a drag = ONE undo).
- [~] WYSIWYG editor shell SCAFFOLDED (2026-08-02, src/Etiq.Editor,
      net8.0-windows WinForms, refs Etiq.Editor.Core; `etiqedit`).
      NOT yet compiled (sandbox can't restore the Windows ref pack) —
      first VS build may need shallow fixes. In the scaffold: MainForm
      (menus w/ shortcuts, outline tree layers→objects, PropertyGrid via
      ObjectProps adapter — all setters undoable, status bar w/ cursor
      mils), CanvasControl (zoom-at-cursor, middle-drag pan, fit; paints
      sheet/lines/boxes/text w/ shrink-to-fit parity/barcode hatch
      placeholders; selection chrome + 8 handles from core Geometry; drag
      move/resize w/ grid snap; arrow nudge 10/1 mils; Del/Esc),
      GdiTextMeasurer (real GDI metrics → ITextMeasurer).
      DESIGN/DATA MODE (user feature, 2026-08-02): Design = full editing;
      Data = accidental-edit guard for print stations — all layout
      interaction disabled, no-print layers hidden, right panel swaps to
      prompt entry (uppercase, from template prompt fields) + Refresh
      Preview (FieldResolver w/ preview-only local counters; rest/epicor
      offline → on-fail policies exercise) + Print button (stub until
      Phase 3 render). Iterate on Windows, NifcoTag-style.
      v1 TODOs: inline text edit (double-click), rotate handle drag, line
      endpoint handles, layer lock/hide/rename context menu in outline,
      add-object palette, layer dimming, real barcode render (ZXing).
      Toolkit decision (2026-08-02): WinForms over Avalonia/MAUI —
      (1) WYSIWYG parity: canvas GDI+ = the driver print path's text
      stack, so preview metrics match printed output (Skia-based toolkits
      drift); GdiTextMeasurer implements the core's ITextMeasurer.
      (2) Fleet and deployment are Windows-only; editor-on-Linux is on no
      roadmap. (3) Maintainer fluency. Self-contained single-file works
      either way, so that wasn't the discriminator. Cheap to revisit:
      everything hard lives in Etiq.Editor.Core (proven cross-platform);
      a future Avalonia shell would be a second thin layer, not a rewrite.
- [ ] Editor interaction layer: selection handles, rotated hit-testing,
      snap-to-grid, z-order ops, multi-select, undo/redo, zoom/pan
- [ ] Inkscape extension pack (now fallback/interchange path):
      mark-as-field (BAQ-aware dropdown), insert barcode, label
      properties, validate, test-print
- [x] `etiq validate` CLI (initial: field cross-check, barcode sanity,
      units/bounds; printer-feasibility check waits on printer registry)

## Later / maybe
- b-PAC output path (Brother PT-series tape printers). PT-D410 CONFIRMED
  supported in b-PAC SDK 3.4 (Brother developer site, checked 2026-08-02)
  — official COM SDK, so media-width query, per-job auto/half-cut and
  chain printing need NO reverse engineering. Adapter design: one stub
  .lbx containing a single full-bleed image object; engine renders
  Etiquette SVG → mono bitmap at printer dpi (existing render path), b-PAC
  sets image + cut options and prints. SVG stays source of truth; b-PAC is
  a third output path beside driver + raw-ZPL, behind the same interface,
  Brother-only, never in core. COM via ProgID/[ComImport] — still no
  NuGet. Cost: b-PAC client component install on the station (vendor
  runtime, same category as Seagull driver). Until then the D410 still
  prints via the plain driver path w/ queue-default cut settings.
- GLPI equipment ID tags (QR on P-touch tape) — the concrete use case
  driving `source="rest"` + the b-PAC path: rest connection profile for
  GLPI (App-Token/Session-Token), asset query, QR tag template. First
  non-Epicor consumer of the platform; good forcing function for keeping
  connections/profiles generic. Sequenced AFTER shop-floor path works.
- JRXML + RDL exporters
- Non-Windows print paths (CUPS raw)
- Older/newer BTW versions
