# Central serial counters — Epicor Kinetic Function + UD table

Design + setup guide for HANDOFF decision #4: serial numbers are issued by an
**atomic, central** counter living in Epicor (Public Cloud, REST-only), never
by per-station files. Client side is `ICounterProvider` /
`EpicorCounterProvider` in `src/Etiq.Core/Counters.cs`.

Written from general Kinetic knowledge — menu names can drift between
releases; verify against your SaaS instance and correct this doc where it
disagrees. Nothing here needs Epicor support tickets: UD tables ship empty in
every Kinetic install and Functions are standard.

## Data model — UD01

One row per counter. UD tables (UD01–UD40) are empty, indexed, and already
exposed to BOs/Functions, so no schema work:

| UD01 column   | Use                                          |
|---------------|----------------------------------------------|
| `Key1`        | counter key, e.g. `ACME`, `NORTHWIND` (coarse per-customer default) |
| `Key2..Key5`  | leave `""` (reserved for future finer splits — plant, part family) |
| `Number01`    | current value (last issued)                  |
| `Character01` | free-text note: who/what the counter is for  |
| `Date01`      | last-issued timestamp (audit)                |

Rollover, format, alphabet are **template concerns** (`etiq:field
format=/alphabet=`), not stored in Epicor — the counter is a bare integer.

## Function library `EtiqCounters`

Two functions, called via `POST /api/v2/efx/{Company}/EtiqCounters/{fn}`:

- `NextSerial(counter: string, count: int) → { next: long }`
  Atomically advances `Number01` by `count`, returns the FIRST value of the
  reserved block (client owns `next .. next+count-1`; block reservation is
  what makes a 500-label job gap-safe).
- `PeekSerial(counter: string) → { current: long }` — read-only.

### Click-by-click

1. Kinetic browser client → menu search **"Functions"** → **Function
   Maintenance** (System Setup → Business Process Management).
2. **New Library** → ID `EtiqCounters`. On the library sheet:
   - check **Enabled**
   - check **Allow Custom Code** if present (needed for path A below;
     Public Cloud allows library-level custom code in current releases —
     if your tenant refuses, use path B, widgets-only)
3. **New Function** → `NextSerial`.
   - Request parameters: `counter` (System.String), `count` (System.Int32)
   - Response parameter: `next` (System.Int64)
4. Function body — **path A (custom-code block, preferred: single
   statement, truly atomic)**:

   ```csharp
   // Uses the Db context available to Functions. Row-locks the counter row
   // for the duration of the transaction => concurrent stations serialize.
   if (string.IsNullOrWhiteSpace(counter)) throw new Ice.BLException("counter key required");
   if (count < 1) count = 1;
   Db.Validate();
   using (var txScope = IceContext.CreateDefaultTransactionScope())
   {
       var row = Db.UD01.With(LockHint.UpdLock)
                 .FirstOrDefault(r => r.Company == Session.CompanyID && r.Key1 == counter
                                   && r.Key2 == "" && r.Key3 == "" && r.Key4 == "" && r.Key5 == "");
       if (row == null)
       {
           row = new Ice.Tables.UD01.UD01Row { Company = Session.CompanyID, Key1 = counter,
               Key2 = "", Key3 = "", Key4 = "", Key5 = "", Number01 = 0 };
           Db.UD01.Insert(row);
       }
       next = (long)row.Number01 + 1;
       row.Number01 = row.Number01 + count;
       row.Date01 = DateTime.Now;
       Db.Validate();
       txScope.Complete();
   }
   ```

   (Exact row-creation API varies slightly by release — Application Studio's
   code editor will red-squiggle what needs renaming.)

5. Path B (**widgets-only fallback**, if custom code is disabled on your
   tenant): implement as widget flow calling the `UD01` business object —
   `GetByID` → (not found → `GetaNewUD01` + set keys) → set
   `Number01 = Number01 + count` → `Update`. The BO `Update` enforces
   optimistic concurrency: under a race one caller gets a "record has been
   modified" error. Add a condition widget looping up to ~5 retries on that
   error. Atomic in effect, chattier than path A.
6. `PeekSerial`: same pattern, read-only (`GetByID`, return `Number01 + 1`
   as `current`... or last issued — pick one and match Counters.cs, which
   expects `current` = next value that would be issued).
7. Library sheet → **Publish** (functions are callable only from published
   libraries).

## REST + security

1. Verify the library appears under
   `GET /api/v2/efx/{Company}/EtiqCounters/` (Swagger: your instance's
   `/api/help`).
2. API key: reuse the labelprint key or mint a dedicated one (Kinetic:
   **API Key Maintenance**, tied to a service account). The account needs
   access to the Function library security ID (**Menu/Security
   Maintenance** → Function security) and nothing else — counters don't
   need BAQ or ERP method rights beyond UD01 via the function.
3. Client call (already implemented):
   `EpicorCounterProvider.ReserveAsync("ACME", 500)` →
   `POST /api/v2/efx/EPIC01/EtiqCounters/NextSerial` body
   `{"counter":"ACME","count":500}` → `{"next":100042}`.

## Operational notes

- **Seeding**: to start a counter at a BarTender label's current value, run
  NextSerial once with `count = <current value>` on the fresh row (advances
  0 → current), or set `Number01` directly in UD01 maintenance
  (menu search "UD01").
- **Never reset** a production counter downward; duplicates on customer
  labels are the one unforgivable failure. If a customer mandates a reset,
  fork a NEW counter key (e.g. `NORTHWIND-2027`) and record it in the
  template.
- Gaps are fine and expected (reserved blocks not fully printed, reprints
  refused, etc.). AIAG serial rules care about uniqueness, not density.
- `LocalFileCounterProvider` (Counters.cs) exists for dev machines without
  Epicor creds. It is single-machine only and must never ship to stations.
