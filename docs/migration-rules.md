# Migration rules and compatibility preflight

`VsTestToMtp.Migration.CompatibilityPreflight.Assess(inventory)` decides, before anything is changed, which test applications of an
[inventory](inventory-msbuild-usage.md) can move from VSTest to Microsoft.Testing.Platform (MTP) v2 under the .NET 10 SDK, which are already
there and which are blocked, and plans the in-family package changes the move needs. It reads only evaluated MSBuild state (properties,
package references and where they are declared); project and file names never decide anything. It works offline: every threshold is data in
`MigrationRuleSet`, and nothing is looked up on a feed.

The rules are versioned (`MigrationRuleSet.Id` = `mtp-v2-dotnet10`, `MigrationRuleSet.Version` = `1`) and every `PreflightResult` records
the rule set it was made with. Bump the version whenever a threshold, upgrade target or rule changes; `MigrationRuleSetTests` pins the
matrix so such a change is deliberate.

## Verdicts

Each project gets a `MigrationReadiness`:

| Readiness | Meaning |
| --- | --- |
| `NotATestApplication` | Classified as production (it may still reference a test library). Nothing to migrate. |
| `Eligible` | Can be migrated; `PackageChanges` lists the package edits it needs (possibly none, for example MSTest 4 without `EnableMSTestRunner`). |
| `AlreadyMigrated` | Already on MTP v2 (MSTest.Sdk without `UseVSTest`, `EnableMSTestRunner`/`EnableNUnitRunner` with MTP v2 packages, xUnit v3 on an MTP v2 variant, TUnit 0.71+), with nothing to change. |
| `Blocked` | At least one error diagnostic. Its other findings, and any package changes it would also need, are still listed so they can be fixed together. |

Selection-level diagnostics (no `Project`) apply to the whole run configuration. `PreflightResult.CanMigrate` is false when any error exists.

## The matrix

### Platform

| Rule | Value | Evidence |
| --- | --- | --- |
| .NET SDK | 10.0.100 or later (the evaluated `NETCoreSdkVersion`) | MTP v2 only runs under the MTP mode of `dotnet test` (`global.json` `test.runner`), added in the .NET 10 SDK; the VSTest mode that `TestingPlatformDotnetTestSupport` enabled for MTP v1 is no longer supported ([MTP v1 to v2](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-migration-from-v1-to-v2)). |
| Runtime targets | .NET 8 or later, .NET Framework 4.6.2 or later, from each target framework's evaluated `TargetFrameworkIdentifier`/`TargetFrameworkVersion` | Same page: .NET Core 3.1 to .NET 7 were dropped. |
| xUnit v3 on .NET Framework | 4.7.2 or later | The lowest .NET Framework target of the `xunit.v3.core.*` packages. |

### Test frameworks and runners

| Framework | Runs on MTP v2 | Below that | Upgrade target |
| --- | --- | --- | --- |
| MSTest (`MSTest`, `MSTest.TestAdapter`, `MSTest.TestFramework`, `MSTest.Analyzers`) | 4.0.0 (`MSTest.TestAdapter` 4.0.0 depends on `Microsoft.Testing.Platform.MSBuild` 2.0.0; 3.11 on 1.9.0) | Upgrade, all packages of the family | 4.0.2 |
| `MSTest.Sdk` | 4.0.0 (sets `MicrosoftTestingPlatformVersion` 2.0.0; 3.11 sets 1.9.0). MTP is on unless `UseVSTest=true`. | Upgrade the SDK version where it is written: the `Sdk` reference, or `global.json` `msbuild-sdks` | 4.0.2 |
| NUnit (`NUnit3TestAdapter`) | 6.0.0 (depends on `Microsoft.Testing.Platform.MSBuild` 2.0.2; 5.2.0 on 1.9.0) | Upgrade | 6.3.1 |
| NUnit framework (`NUnit`) | 3.0.0 | **Blocked**: NUnit3TestAdapter does not run NUnit 2, and moving to NUnit 3 is a framework migration. The NUnit 2 adapter (`NUnitTestAdapter`) is blocked too. | none |
| xUnit v3 (`xunit.v3`, `xunit.v3.core`, each with `.mtp-v1`, `.mtp-v2`, `.mtp-off` or no suffix) | the `.mtp-v2` variant (3.2.0 and later), or no suffix from 4.0.0 ([xUnit v3 and MTP](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform)) | Replace with the `.mtp-v2` variant at the same version, or at 3.2.2 when older than 3.2.0. Still xUnit v3 either way. | 3.2.2 (only below 3.2.0) |
| xUnit v2 (`xunit`, `xunit.core`) | never | **Blocked**: never upgraded to xUnit v3 | none |
| TUnit (`TUnit`, `TUnit.Engine`, `TUnit.Core`, `TUnit.Assertions`) | 0.71.0 (`TUnit.Engine` 0.71.0 depends on `Microsoft.Testing.Platform` 2.0.1; 0.70.7 on 1.9.0) | Upgrade, all packages of the family | 1.45.0 |

`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are compatible and left alone (xUnit recommends keeping them until every environment
runs MTP).

### Extensions

| Packages | Runs on MTP v2 | Upgrade target |
| --- | --- | --- |
| `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild` and the extensions versioned with them (`AzureDevOpsReport`, `CrashDump`, `HangDump`, `HotReload`, `Retry`, `Telemetry`, `TrxReport`, `TrxReport.Abstractions`, `VSTestBridge`) | 2.0.0 | 2.5.1 |
| `Microsoft.Testing.Extensions.CodeCoverage` | 18.1.0 (depends on MTP 2.0.0; 18.0.6 on 1.8.4) | 18.8.0 |
| `Microsoft.Testing.Extensions.Fakes` | 18.1.1 (what MSTest.Sdk 4.0.0 uses; 3.11 uses 17.14.1) | 18.1.1 |
| `coverlet.MTP` | 8.0.0 (every release is on MTP v2) | 8.0.0 |
| `coverlet.collector`, `coverlet.msbuild` | never: a VSTest data collector, and a hook on the `VSTest` target | **Warning**, not blocking: replacing them is part of the migration ([#13](https://github.com/BenjaminMichaelis/vstest-to-mtp/issues/13)) so coverage is not lost |

## What blocks

Every one of these is an error diagnostic with a location and a remediation; none is rewritten silently:

- `XUnitV2NotSupported`, `UnsupportedTestFramework` (no supported framework, NUnit 2), `MultipleTestFrameworks`, `MissingRunnerPackage`.
- `UnsupportedTargetFramework`: any target framework below the runtime targets, per target framework. Projects are never retargeted.
- `CustomEntryPoint`: `StartupObject`, `GenerateProgramFile=false` or `GenerateTestingPlatformEntryPoint=false` on a project that still has
  to move (MTP generates the entry point). A project already on MTP v2 keeps its own.
- `UnresolvableVersion`: a version only a feed can resolve (floating `4.*`, an exclusive or missing lower bound) or none at all. A bare
  version and an inclusive range resolve to their lower bound, as NuGet does.
- `PackageNotInRepository`: a package that needs to change is declared by an SDK or package, not a repository file.
- `UnresolvedProjectState`: any inventory error or warning on a test application (failed evaluation, missing imports, conditions on unset or
  environment-provided properties, `IsTestProject` in early props, ...). On a production project it is a warning.
- `UnclassifiedProject`: a project the inventory could not classify, or whose target frameworks classify differently.
- `UnresolvedSelection`, `UnsupportedSdk` and `MixedTestRunners` (selection level): `global.json` `test.runner` applies to every project
  `dotnet test` runs from there, and the MTP mode refuses VSTest projects ("All projects must use that test runner"), so a selection where
  some test applications would move and others stay on VSTest (or cannot be classified) is reported instead of half-migrated.

## Verified

The upgrade targets were run, not only read: copies of the committed fixtures were converted by hand and run with `dotnet test` in MTP
mode (`global.json` `test.runner` = `Microsoft.Testing.Platform`, .NET SDK 10.0.4xx), all 6 tests passing in each:

- MSTest 4.0.2 with `EnableMSTestRunner=true` and `OutputType=Exe`.
- NUnit 4.3.2 with NUnit3TestAdapter 6.3.1, `EnableNUnitRunner=true`, `OutputType=Exe` and Microsoft.Testing.Extensions.TrxReport 2.5.1.
- `xunit.v3.mtp-v2` 4.0.1 (from `xunit.v3.mtp-off` 4.0.1) and `xunit.v3.mtp-v2` 3.2.2.
- TUnit 1.45.0 and Microsoft.Testing.Extensions.CodeCoverage 18.8.0 are what this repository's own tests run on.
- The xUnit v2 fixture in MTP mode fails with "All projects must use that test runner", which is what `MixedTestRunners` prevents.

The thresholds come from each package's own `Microsoft.Testing.Platform*` dependency in its nuspec on nuget.org (and, for MSTest.Sdk, its
`Sdk.props`), recorded in `PackageRule.Evidence`.

## Tests

- `MigrationRuleSetTests` pins the matrix; `PackageVersionTests` the version comparison and range resolution.
- `PreflightFixtureTests` runs the preflight on every committed fixture: MSTest, NUnit and xUnit v3 pass (NUnit with the adapter upgrade,
  xUnit v3 with the variant swap, both pointing at the exact `PackageReference` and `PackageVersion`/`Version` lines), xUnit v2 is blocked,
  and the fixtures are not modified.
- `PreflightScenarioTests` evaluates representative passing and blocked projects with MSBuild, including MSTest.Sdk (from a local feed)
  with and without `UseVSTest`, with its version in the project or in `global.json`.
