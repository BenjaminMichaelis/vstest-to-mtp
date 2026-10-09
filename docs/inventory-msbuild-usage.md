# How the inventory uses MSBuild

The inventory (`VsTestToMtp.Inventory`) reads *evaluated* MSBuild state with the `Microsoft.Build` API. This page records the documented
constraints on that API, how we comply, and what was verified against a real MSBuild instead of assumed. When a review or a bug
questions MSBuild behavior, add the verified answer here and pin it with a test.

## Documented constraints and how we comply

| Constraint (source) | How we comply |
| --- | --- |
| Reference `Microsoft.Build` with `ExcludeAssets="runtime"` so it is never shipped; load it from the SDK instead ([Find MSBuild and use its API](https://learn.microsoft.com/visualstudio/msbuild/find-and-use-msbuild-versions)). | `VsTestToMtp.csproj` references it with `ExcludeAssets="runtime" PrivateAssets="all"`. The output contains no `Microsoft.Build.*.dll` except `Microsoft.Build.Locator.dll`. |
| Do not set `ExcludeAssets` on `Microsoft.Build.Locator`. | Referenced normally. |
| Register MSBuild before any `Microsoft.Build` type is touched, and not in a method that references MSBuild types (same page). | `MsBuildEnvironment` references only Locator types; `InventoryBuilder` calls it from a separate un-inlined method before the MSBuild-using method. |
| The `Microsoft.Build` package version must be less than or equal to the oldest MSBuild you support (same page). | We compile against 17.8.43, the MSBuild of SDK 8.0.100 (earlier 17.8.x packages carry a known high-severity advisory, NU1903), so the oldest supported SDK is **8.0**. A selection that resolves to an older SDK is rejected with `MsBuildSdkUnsupported` before its MSBuild is loaded. The suite runs under SDK 10 on CI (Linux, Windows, macOS); the same evaluation was also verified under SDK 9.0 and 10.0 with a pinned `global.json`. SDK 8.0 itself has not been exercised here (see #30). |
| With a working directory, `QueryVisualStudioInstances` lists installed SDKs with the one `hostfxr` resolves for that directory (honoring `global.json`) first; it throws when `global.json` cannot be satisfied ([MSBuildLocator](https://github.com/microsoft/MSBuildLocator)). | We take the first instance, never "the newest". By default the Locator also hides SDKs newer than the running runtime ([DotNetSdkLocationHelper.cs](https://github.com/microsoft/MSBuildLocator/blob/ff0b9004f2548ba616d4d1a0f901c842b1d7f993/src/MSBuildLocator/DotNetSdkLocationHelper.cs#L57-L66)), which would make a *different* SDK come first for a `global.json` pinning one; like Roslyn we set `AllowAllRuntimeVersions` and refuse such an SDK with `MsBuildSdkNewerThanRuntime`. `UnsatisfiableGlobalJson_...`, `SelectionPinnedToADifferentSdk...` and `SelectionPinnedToAnSdkNewerThanTheRuntime...` cover it (the last runs only where such an SDK is installed). |
| MSBuild can be registered once per process. | The registered SDK is remembered; a selection that needs a different SDK gets an `MsBuildSdkMismatch` blocker instead of silently using the wrong one. Failed lookups are not remembered. |
| `ProjectLoadSettings`: `IgnoreMissingImports` also hides an unresolved `Sdk=` unless `FailOnUnresolvedSdk` is set; circular imports are only rejected with `RejectCircularImports` ([ProjectLoadSettings](https://learn.microsoft.com/dotnet/api/microsoft.build.evaluation.projectloadsettings)). | Both are set. Without them an unresolvable SDK produced misleading `MissingImport` blockers and a circular import was accepted as a complete evaluation (found by comparing against the docs). |
| `EvaluationContext` extends the lifetime of evaluation caches across evaluations; create one, pass it to each, throw it away when the environment changes ([EvaluationContext](https://learn.microsoft.com/dotnet/api/microsoft.build.evaluation.context.evaluationcontext)). | One shared context per inventory run, discarded afterwards. It caches SDK resolution and file lookups but not parsed XML ([EvaluationContext.cs](https://github.com/dotnet/msbuild/blob/74878b50aab1c07a7cdcaa15f28115768388cdcc/src/Build/Evaluation/Context/EvaluationContext.cs#L54-L71)), so the run also shares one `ProjectCollection` (unloading projects after each), as Roslyn and slngen do; the suite went from about 3m20s to under 1m. Skipped-import events from the shared logger are attributed to the project being evaluated (`SkippedImports_AreReportedOnlyForTheProjectThatHasThem`). |
| `ProjectImportedEventArgs.ImportIgnored` is set only for an import that would have been included but was ignored as invalid; not for a conditioned import that evaluated to false or a glob with no matches; `ImportedProjectFile` is null/empty for those ([ProjectImportedEventArgs](https://learn.microsoft.com/dotnet/api/microsoft.build.framework.projectimportedeventargs)). | `ImportLogger` records only ignored imports, so an ignored import with no file is one whose path expanded to nothing. Tests cover a true condition, a false condition and an unmatched glob. |

## Verified against a real MSBuild (not assumed)

- Booleans: exactly `true/on/yes/!false/!off/!no` (any case, no surrounding whitespace) are true and `false/off/no/!true/!on/!yes` are false,
  in both boolean and `== 'true'` contexts ([ConversionUtilities.cs](https://github.com/dotnet/msbuild/blob/74878b50aab1c07a7cdcaa15f28115768388cdcc/src/Framework/Utilities/ConversionUtilities.cs#L101-L123));
  `1`, `0`, ` true `, `! true` and `!!true` are rejected with MSB4113. `InventoryMsBuildOracleTests` evaluates each spelling with MSBuild
  and requires our parsing (`MsBuildBoolean`, used for `IsTestProject`) to agree.
- Multi-targeting: the SDK dispatches to inner builds only when `TargetFrameworks` is set and `TargetFramework` is empty
  ([Sdk.targets](https://github.com/dotnet/sdk/blob/78c57f0692e746e84becc60c287634326d6a96db/src/Tasks/Microsoft.NET.Build.Tasks/sdk/Sdk.targets#L16-L18)).
  A project that sets both is a single-target build of `TargetFramework`.
- Implicit SDK imports (`Sdk.props`/`Sdk.targets`) are not in the project XML; `ProjectImportElement.ImplicitImportLocation` says whether each
  goes at the top or the bottom, which `EvaluationOrder` uses. `Project.GetLogicalProject()` only expands imports written in the XML, so it
  cannot replace that walker.
- Which files are the repository's: the .NET root comes from the SDK's `$(NetCoreRoot)`, and every package an SDK was resolved from
  (`ResolvedImport.SdkResult`) is external, wherever NuGet.config put it (`FilesFromAVersionedSdkPackage_...` restores one from a local feed).
- A bare condition on an unset property (`Condition="$(X)"`) is rejected by MSBuild itself (MSB4113), so it surfaces as `EvaluationFailed`.
- A file imported twice is not an ignored import and does not fail evaluation.
- A locked or access-denied project file is reported by MSBuild as `InvalidProjectFileException`, not `IOException`.
- `Microsoft.NET.Test.Sdk`'s build props set `IsTestProject=true` for any project that references it, which is why restore output is
  ignored (`ImportProjectExtensionProps=false`): a restored production project that merely references it must not become a test project.
- `Project.GetItemProvenance` is documented as not yet implementing `Update`/`Remove`, but in practice returns them; the provenance tests
  pin that, so a regression in a newer MSBuild would be caught.

## NuGet rules the inventory mirrors

These come from NuGet's behavior, not MSBuild's, and are covered by tests in `InventoryProvenanceTests` (`PackageVersionResolver` implements them):

- NuGet reads its boolean properties literally: only `true`/`false` (trimmed, any case) count, not MSBuild's other spellings
  ([PackageSpecFactory.cs](https://github.com/NuGet/NuGet.Client/blob/b337f5b80d3363a61f773f2e1c3757526a474610/src/NuGet.Core/NuGet.Commands/RestoreCommand/Utility/PackageSpecFactory.cs#L941-L962); `NuGetBoolean`).
- Central Package Management is on when the evaluated `_CentralPackageVersionsEnabled` is `true`, which is what restore reads outside Visual Studio
  ([PackageSpecFactory.cs](https://github.com/NuGet/NuGet.Client/blob/b337f5b80d3363a61f773f2e1c3757526a474610/src/NuGet.Core/NuGet.Commands/RestoreCommand/Utility/PackageSpecFactory.cs#L513-L517)).
  NuGet.targets sets it only when `ManagePackageVersionsCentrally == 'true'` (an MSBuild comparison, so `yes` counts but ` true ` does not) and a
  `Directory.Packages.props` was imported; without one, `PackageVersion` items are ignored.

- With Central Package Management on, a non-global `PackageReference` must not set `Version` (NU1008); it is an error and no version is effective.
  `GlobalPackageReference` versions are valid. A `GlobalPackageReference` also appears as a generated `PackageReference`; only the user-owned one is reported.
- `VersionOverride` with `CentralPackageVersionOverrideEnabled=false` fails restore (NU1013); it is an error and does not fall back to the central version.
  Only the literal `false` disables overrides; `off` or `no` leave them enabled.
- Duplicate `PackageVersion` items are ambiguous (NU1506), not last-one-wins; the blocker lists every declaration in `RelatedLocations`.

## Prior art

How established Microsoft tools use the same APIs, checked against their source. Our code links the same lines in comments where it follows (or deliberately departs from) them.

| Pattern | Who does it | Us |
| --- | --- | --- |
| Take the first Locator instance for a working directory; list SDKs newer than the runtime (`AllowAllRuntimeVersions`) | Roslyn [NetCoreBuildHost.cs](https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/NetCoreBuildHost.cs) (Roslyn then starts a build host on that runtime) | Same query; we refuse an SDK newer than our runtime with a blocker instead of starting a second process. |
| `ProjectLoadSettings` `IgnoreMissing/Invalid/EmptyImports` + `FailOnUnresolvedSdk` + `RejectCircularImports` | Roslyn [ProjectBuildManager.cs#L166-L171](https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/Build/ProjectBuildManager.cs#L166-L171) | Same, plus `RecordEvaluatedItemElements`. Roslyn also sets `DoNotEvaluateElementsWithFalseCondition`; we have not adopted it, since it changes what `GetItemProvenance` and `ItemsIgnoringCondition` see and would need its own verification. |
| Inner builds by re-evaluating with a global `TargetFramework`, only when `TargetFramework` is empty | Roslyn [ProjectBuildManager.cs#L266-L305](https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/Build/ProjectBuildManager.cs#L266-L305), NuGet `MSBuildAPIUtility`, `dotnet new` `MSBuildEvaluator` | Same. |
| One `ProjectCollection` per run, `UnloadAllProjects` | Roslyn [ProjectBuildManager.cs#L258](https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/Build/ProjectBuildManager.cs#L258), slngen | Same. |
| Leave the NuGet SDK resolver on | Roslyn, slngen, `dotnet new`, NuGet (none set `MSBUILDDISABLENUGETSDKRESOLVER`) | Same (see Known limits). |
| Ignore restore-generated imports (`ExcludeRestorePackageImports=true`) | NuGet's own restore graph (NuGet.targets) | Same, plus `ImportProjectExtensionProps/Targets=false`. |
| `Project.GetItemProvenance`, `ImplicitImportLocation` | No open-source caller outside dotnet/msbuild (Visual Studio uses `GetItemProvenance`, documented as "prone to change") | Used, and pinned by tests. |

## Known limits

- An SDK referenced with a version (`Sdk="MSTest.Sdk/3.6.4"`) is resolved by MSBuild's NuGet SDK resolver, which downloads a missing package
  into the NuGet global packages folder (never the repository). We leave it on, as Roslyn's MSBuildWorkspace, slngen, `dotnet new` and NuGet do;
  disabling it would make every MSTest.Sdk project un-inventoryable even after a restore. `MSBUILDDISABLENUGETSDKRESOLVER=1` is the resolver's
  only off switch ([NuGetSdkResolver.cs](https://github.com/NuGet/NuGet.Client/blob/b337f5b80d3363a61f773f2e1c3757526a474610/src/NuGet.Core/Microsoft.Build.NuGetSdkResolver/NuGetSdkResolver.cs#L56-L60));
  with it set, such projects surface as `EvaluationFailed` blockers.

- One MSBuild/SDK per process (see above). Inventorying repositories that pin different SDKs needs one process per SDK.
- Package-contributed properties (from `obj/*.nuget.g.props`) are deliberately not seen; package evidence comes from `TestPackageCatalog`.
- Condition analysis follows property references in conditions; it does not evaluate conditions itself.
