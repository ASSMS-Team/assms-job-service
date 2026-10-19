# Mutation testing (Stryker.NET)

Mutation testing evaluates the quality and fault tolerance of this service's
unit test suite. Stryker.NET makes small changes ("mutants") to the
application code, for example `>` to `>=` or `&&` to `||`, and runs the unit
tests against each one:

- **Killed**: a test failed, so the tests detected the change.
- **Survived**: every test still passed, so the tests missed the change.
- **No coverage**: no test executes that code at all.
- **Timeout**: the mutant made the code hang; counted as detected.

**Mutation score = (Killed + Timeout) / (Killed + Timeout + Survived + No coverage).**
Thresholds: 80% or more is high, 60% or more is acceptable. The run never
fails the build on its score (`break: 0`); it measures, it does not gate.

## Files

| File | Purpose |
|---|---|
| `dotnet-tools.json` (repo root) | Pins `dotnet-stryker` 4.16.0 (runs on .NET 8) |
| `quality/mutation/stryker-config.json` | Project, test project, thresholds and excluded files |
| `quality/mutation/run-mutation-tests.ps1` | Runs Stryker and writes the reports; used locally and in CI |
| `.github/workflows/mutation-testing.yml` | Runs the script on push to `test/ase-mutation` or manually |

## Run locally

From the repository root:

```powershell
./quality/mutation/run-mutation-tests.ps1
```

Reports are written to `mutation-reports/<timestamp>/` and copied to
`mutation-reports/latest/` (both git-ignored):

- `mutation-report.html`: open in a browser; every source file with each mutant
  and its status
- `mutation-report.json`: machine-readable result
- `summary.md`: mutation score and mutant counts

## Run in GitHub Actions

The **Mutation testing** workflow runs automatically on every push to
`test/ase-mutation`, or from **Actions > Mutation testing > Run workflow**.
Each run shows the score table on its Summary page and attaches the reports as
the `mutation-report-<repository>` artifact.

## Excluded from mutation

Unit tests use test doubles instead of MySQL, Kafka and the web host, so code
that only talks to that infrastructure cannot be killed by a unit test and is
excluded rather than reported as a coverage gap:

- `Program.cs` (composition root) and database migration runners
- MySQL repositories and the connection factory
- Kafka producer classes that publish to a real broker
- Swagger documentation filter, health-check controller and metric definitions

Mutations inside logging calls (`LogInformation`, `LogError`, ...) are also
ignored. All other code (services, controllers, consumers, outbox publishers,
validation and security logic) is mutated.
