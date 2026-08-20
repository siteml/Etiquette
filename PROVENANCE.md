# Provenance boundaries

This repo deliberately separates components by the origin of the knowledge
they embody, so each piece's ownership story is clean and the pieces are
severable from one another. Maintained for IP-hygiene reasons; day-to-day project context lives in an
internal handoff document that is not part of the published repository.

## Tier 1 — independent platform (the Etiquette project proper)

Designed from scratch for this project; no employer-specific knowledge,
data, or access required to create or use it:

- `docs/convention.md` + `examples/` — the Etiquette SVG label convention
- `src/Etiq.Core/` — template model/validator, PNG decoder, PDF writer,
  ZPL raster path, counter provider interface, DPAPI credential helper,
  generic Epicor Kinetic REST client (written against Epicor's public,
  documented product API — contains no site-specific identifiers)
- `src/etiq/` CLI, `tests/`, `inkscape/`, `engine/` (BTW recon commands
  compile in only when the private module is present)
- `docs/counters.md` — a design for atomic counters on stock Kinetic
  features (UD tables + Functions), applicable to any Kinetic tenant

## Tier 2 — BTW import (separate private repo `etiquette-btw`)

Not in this repository at all: the BTW import module lives in the sibling
private repo `etiquette-btw` (src/Etiq.Btw, tools/recon, docs/btw-format.md)
and is picked up by conditional project reference + `ETIQ_BTW` define when
cloned alongside. This repo builds and runs fully without it.

- Rationale: the BarTender 10.1 container /
  archive format knowledge (docs/btw-format.md) was produced by clean-room
  analysis of `.btw` files the maintainer had access to through employment,
  plus save-as variant files produced in an employer-licensed copy of
  BarTender. The format itself is Seagull Scientific's, not the
  employer's; but the *access* that enabled the research came via work.
  Everything BTW-related therefore lives outside the open-source tree
  entirely, and no license is granted on it.

## Tier 3 — employer-specific (not part of the project)

- `reference/labelprint/` — a working application developed FOR and
  deployed at the maintainer's employer: proprietary, the employer's.
  Kept on the maintainer's disk for reference only; the whole `reference/`
  directory is gitignored and must never be committed or published.
- Site configuration (BAQ IDs, column maps, tenant URLs, counter keys,
  API keys) — never committed; lives only in local `config.json` files.
- Label corpora and anything derived from them (previews, galleries,
  census output) — customer/production data, gitignored, never committed.

## Rules that keep the boundary

1. Nothing in this repo may contain BTW format knowledge; Tier 2 code is
   referenced only conditionally, by existence check on the sibling repo.
2. Site-specific values only ever appear in gitignored config, never code.
3. Corpus-derived findings land in `docs/btw-format.md` (Tier 2), not in
   Tier 1 docs.
