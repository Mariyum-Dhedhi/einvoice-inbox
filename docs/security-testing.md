# Security scan and mutation testing

Package 15b added two checks of the tests themselves:
- **A ZAP scan in CI.** It looks at the running app the way an attacker's tools do.
- **Mutation testing with Stryker.NET.** It shows which rules the unit tests do not actually check.

Both found real gaps. Each gap was fixed behind a failing test.

## ZAP baseline scan

**What it does.** The `zap` CI job does the following:
1. Starts the app (Development, throwaway passwords) on a fresh database built by db-init.
2. Signs in as the clerk through the real login form (`tests/zap/scan.sh`).
3. Runs two ZAP 2.17.0 baseline scans (`zaproxy/zap-stable`, pinned by digest):
   - the public pages
   - the app signed in, with every request carrying the clerk's session cookie

The signed-in scan reaches the inbox, its filters and the invoice pages. The spider never submits forms, so it
cannot sign itself out or change data.

**Any alert not listed in `tests/zap/rules.tsv` fails the build, warnings included.** Every listed alert says why it
is intended, or which test pins it instead.

**What the first scan found** (2026-11-16, before the fixes):

| Finding | Fix | Test |
|---|---|---|
| The only content security policy was Blazor's `frame-ancestors 'self'` | A full policy: scripts, styles, images, fonts and connections from this site only; no plug-ins; framed by nobody; forms post only here (`SecurityHeaders`) | `SecurityHeaderTests`: exactly one policy, directive by directive, on a page, an API answer, a static file and an unknown address |
| No cross-origin isolation headers | `Cross-Origin-Opener-Policy`, `-Resource-Policy`, `-Embedder-Policy` | same |
| No `Permissions-Policy` | camera, microphone, location, payment, USB, clipboard reading, full screen: off | same |
| Antiforgery cookie without `Secure` | `__Host-einvoice-antiforgery`: Secure, HttpOnly, SameSite=Strict, host only | `SecurityHeaderTests` |
| No HSTS (the scan runs in Development) | A year outside Development; never in Development, where a browser would pin `localhost` for every project | `SecurityHeaderTests`, both environments |
| (not flagged) | `Referrer-Policy: no-referrer`: invoice ids in addresses stay on this site | `SecurityHeaderTests` |

**What the strict policy broke, and how the tests caught it.** With `script-src 'self'`, the browser refused
one inline script on every page: Blazor's `<ImportMap />`. The browser journeys fail on any console error, so all five
failed with "Executing inline script violates the following Content Security Policy directive".

The import map changes with every build, because it lists the fingerprinted file names, so a fixed hash cannot allow it. Every script here is loaded by its
address, so it was removed. Styles still allow inline (`'unsafe-inline'`): MudBlazor sets positions and colours as
style attributes. Scripts do not.

**The journeys now run over HTTPS.** Once the antiforgery cookie insisted on `Secure`, ASP.NET Core refused to render a form over plain
HTTP. The journey host used plain HTTP, and the sign-in cookie had only worked because cookie authentication does not check. The journeys now use HTTPS
like the real app, with the ASP.NET Core development certificate (CI creates it).

**Planted bugs:**
- `script-src` with `'unsafe-inline'`: caught.
- The import map back: caught by the journeys.
- An antiforgery cookie without `Secure`: caught.
- Blazor's own `frame-ancestors` policy switched back on: **not** caught. That turned out to be correct: our header
  replaces it when the response starts, so there is still exactly one policy. The setting that switched it off was dead code and is gone.

## Mutation testing (Stryker.NET 5.0.0)

How to run it, and what is and is not mutated: [`tests/mutation/README.md`](../tests/mutation/README.md). Summaries of
the runs: [`tests/mutation/results`](../tests/mutation/results).

**Scores, 2026-11-16, same configuration before and after the new tests:**

| Project | Before | After | Mutants killed / survived / not covered (after) |
|---|---:|---:|---|
| EInvoice.Domain | 72.65 % | **90.58 %** | 202 / 14 / 7 |
| EInvoice.Application | 73.51 % | **77.82 %** | 614 / 129 / 46 |

The first run, before the configuration left out null checks, exception messages and the five database-tested
files, scored 60.81 % and 57.97 %. Most of that difference is noise the configuration removes, not tests.

**What the surviving mutants showed, now tested:**

| Rule | What no test noticed |
|---|---|
| IBAN | 27 of 32 country lengths never parsed; a character that is no letter or digit but happens to give a valid checksum |
| Money | `<` and `>` for equal amounts |
| Matching | an invoice with **no** lines counted as a full match |
| Invoice | lines returned in line-number order (the database does not keep order) |
| Model reply (AI) | every limit tested only far above it, never exactly at it (`>` vs `>=`); a trailing comma; a comment |
| Evidence check (AI) | for order reference, seller, VAT id, tax total, tax rate and order line, a check that always said "confident" went unnoticed; one invented IBAN among real ones; numbers with thousands separators; five of seven date forms |
| Audit check | the last good event and hash reported when a chain is broken |
| IBAN finding | which of several payment accounts is not approved |

The evidence check decides whether a value the model read may go on without a person, so those survivors mattered
most. The code was right; nothing proved it.

**Still surviving, deliberately:**
- **Which reason a doubtful field gets** ("no value extracted" or "not found"): either way the field is not confident and goes to a person.
- **Number-boundary details** inside longer numbers that the tests above already cover from the outside.
- **Explanation and message texts** in `RuleBasedExplainer`, `ExplanationFacts` and the refusal details.
- **Mapper and posting-worker branches** that the database tests drive.

Each of these can be read in the summaries.

**Not in CI.** A run takes about five minutes and its score varies with the configuration, so it is a tool for
finding blind spots, not a gate.
