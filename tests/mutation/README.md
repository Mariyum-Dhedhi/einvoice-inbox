# Mutation testing

Stryker.NET changes the code in small ways (`<` to `<=`, `&&` to `||`, a check removed) and runs the unit tests
against each change. A change that no test notices ("survived") shows a rule nobody checks. Results and what they
led to: `docs/security-testing.md`.

Run by hand, not in CI: a run takes about five minutes.

```bash
dotnet tool restore
cd tests/EInvoice.UnitTests
dotnet stryker --config-file stryker-config.json --project EInvoice.Domain.csproj --output ../../StrykerOutput/domain
dotnet stryker --config-file stryker-config.json --project EInvoice.Application.csproj --output ../../StrykerOutput/application
```

Each run writes an HTML report (open it in a browser: every surviving mutant, with its line) and a JSON report.
The full reports are megabytes, so `results/` keeps a summary of each:

```bash
python tests/mutation/summarize.py StrykerOutput/domain/reports/mutation-report.json EInvoice.Domain <commit> > tests/mutation/results/<date>-<state>-Domain.json
```

## What is mutated, and what is not (`tests/EInvoice.UnitTests/stryker-config.json`)

- **Mutated:** Domain and Application, against the unit tests.
- **Not mutated: argument null checks and exception messages.** Removing a null check only moves the failure, and a
  changed message changes no decision. `ConfigureAwait(true/false)` is not mutated either; it changes no result.
- **Left out: five files that only the database tests drive.** These are `InvoiceIntake`, `IntakeModel`,
  `InvoiceRematch`, `Kpis` and `InvoiceDocument`. Stryker runs the unit tests only, so it would report every line
  there as untested, although the database tests do test it.

A surviving mutant that matters gets a test. One that cannot change any result (an equivalent mutant) is noted, not
"fixed" with a test that pins an implementation detail.
