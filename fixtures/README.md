# VSTest migration fixtures

Real, checked-in, SDK-style C# VSTest sample projects that converter rules are built and tested against. They are **generated once from the official .NET / xUnit templates and committed**; tests and CI never regenerate them.

## Layout

```text
fixtures/
  generate.ps1                  maintainer-only reproduction script (never run by tests/CI)
  <framework>/<variant>/        one self-contained mini repository per fixture
    Fixture.slnx
    global.json                 pins SDK 10.0.401 (latestMinor); no MTP runner => `dotnet test` runs VSTest
    Directory.Build.props       empty on purpose (nothing inherited from the repository root)
    Directory.Packages.props    cpm: central versions | plain: CPM explicitly disabled
    .editorconfig               root = true (nothing inherited from the repository root)
    src/Calculator/             production project (`Arithmetic`: Add/Subtract/Multiply/Divide)
    tests/Calculator.Tests/     template output + deterministic tests, references src/Calculator
```

| Framework folder | Role |
| --- | --- |
| `mstest` | MSTest (VSTest runner) |
| `nunit` | NUnit (VSTest runner) |
| `xunit-v3` | xUnit v3 in **non-MTP** (VSTest) mode via `xunit.v3.mtp-off` |
| `xunit-v2` | xUnit v2 – **intentional migration blocker**; no automatic v2 → v3 upgrade is expected |

Each framework has two variants: `plain` (versions inline in the csproj, as the template emits them; CPM explicitly off) and `cpm` (the same project with Central Package Management – versions moved to `Directory.Packages.props`). That is 8 fixture roots.

Every root has its own `global.json`, so a later MTP conversion of one copy can never invalidate another fixture. Conversion tests must work on an isolated copy (`VsTestToMtp.Tests.Fixtures.FixtureWorkspace.Create`) and leave the committed originals unchanged.

## Recorded versions

Generated on **.NET SDK 10.0.401**, target framework `net10.0`.

| Fixture | Template (source) | Test packages |
| --- | --- | --- |
| `mstest` | `dotnet new mstest --test-runner VSTest` (built into SDK 10.0.401) | `MSTest` 4.0.2 |
| `nunit` | `dotnet new nunit` (built into SDK 10.0.401) | `NUnit` 4.3.2, `NUnit3TestAdapter` 5.0.0, `NUnit.Analyzers` 4.7.0, `Microsoft.NET.Test.Sdk` 17.14.0, `coverlet.collector` 6.0.4 |
| `xunit-v3` | `dotnet new xunit3 --test-runner vstest` from **`xunit.v3.templates` 4.0.1** | `xunit.v3.mtp-off` 4.0.1, `xunit.runner.visualstudio` 4.0.0, `Microsoft.NET.Test.Sdk` 18.10.0 |
| `xunit-v2` | `dotnet new xunit` (built into SDK 10.0.401) | `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1, `coverlet.collector` 6.0.4 |

Notes verified from the generated output, not assumed:

- The `mstest` template defaults to VSTest; `--test-runner VSTest` is passed anyway to make it explicit.
- The `xunit3` template defaults to MTP; `--test-runner vstest` swaps in `xunit.v3.mtp-off` (xUnit's VSTest-mode package), which is why the `xunit-v3` fixture still has `OutputType` `Exe` (an xUnit v3 requirement) but runs under VSTest.
- The template-emitted `net8.0` default of `xunit3` is overridden with `--framework net10.0` so every fixture targets .NET 10.
- No fixture sets `TestingPlatformDotnetTestSupport`, `UseMicrosoftTestingPlatformRunner`, `EnableMSTestRunner` or similar MTP opt-ins (asserted by `FixtureStructureTests`).

## Baseline `dotnet test` results (.NET 10 SDK, VSTest mode)

Every fixture contains the same deterministic tests (no I/O, time or randomness): `Add`, `Subtract`, `Divide`‑by‑zero throws, and a data-driven `Multiply` with three rows – **6 test cases**.

| Fixture | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| `mstest/plain`, `mstest/cpm` | 6 | 0 | 0 | 6 |
| `nunit/plain`, `nunit/cpm` | 6 | 0 | 0 | 6 |
| `xunit-v3/plain`, `xunit-v3/cpm` | 6 | 0 | 0 | 6 |
| `xunit-v2/plain`, `xunit-v2/cpm` | 6 | 0 | 0 | 6 |

xUnit v2 passes in VSTest mode today; it is only a blocker for the later MTP conversion.

## Reproduction

Only for deliberately refreshing the baselines; then review the diff and update this file.

```powershell
pwsh fixtures/generate.ps1 -XunitV3TemplatesVersion 4.0.1
```

The script installs the pinned `xunit.v3.templates`, regenerates all 8 roots from the templates, adds the production project and tests, produces the CPM variants, and runs `dotnet test` in each.

Run a single fixture by hand:

```powershell
cd fixtures/mstest/plain
dotnet test Fixture.slnx
```

## How the fixtures are verified

- `VsTestToMtp.Tests/Fixtures/FixtureStructureTests.cs` – fast structural checks (own `global.json` without an MTP runner, isolation files, CPM flag matches the variant, test project references the production project, expected VSTest packages and xUnit major versions).
- `VsTestToMtp.Tests/Fixtures/FixtureBaselineTests.cs` (`Category("Integration")`) – copies each fixture to a temp directory, runs the real `dotnet test`, asserts the baseline above, and checks the committed original is byte-for-byte unchanged.
- CI (`.github/scripts/Invoke-FixtureBuildAndTest.ps1`, run from `.github/workflows/build-and-test.yml`) – restores, builds (Release) and tests every committed fixture in place, then verifies `git status` for `fixtures/` is clean.

The fixtures are intentionally **not** part of `VsTestToMtp.slnx`, so the repository's own build, test and format steps ignore them.
