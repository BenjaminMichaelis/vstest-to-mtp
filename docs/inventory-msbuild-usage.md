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
| The `Microsoft.Build` package version must be less than or equal to the oldest MSBuild you support (same page). | We compile against 18.0.2. Verified (see below) to evaluate correctly under the MSBuild of SDK 6.0, 9.0 and 10.0. |
| With a working directory, `QueryVisualStudioInstances` lists installed SDKs with the one `hostfxr` resolves for that directory (honoring `global.json`) first; it throws when `global.json` cannot be satisfied ([MSBuildLocator](https://github.com/microsoft/MSBuildLocator)). | We take the first instance, never "the newest". `UnsatisfiableGlobalJson_...` and `SelectionPinnedToADifferentSdk...` cover it. |
| MSBuild can be registered once per process. | The registered SDK is remembered; a selection that needs a different SDK gets an `MsBuildSdkMismatch` blocker instead of silently using the wrong one. Failed lookups are not remembered. |
| `ProjectLoadSettings`: `IgnoreMissingImports` also hides an unresolved `Sdk=` unless `FailOnUnresolvedSdk` is set; circular imports are only rejected with `RejectCircularImports` ([ProjectLoadSettings](https://learn.microsoft.com/dotnet/api/microsoft.build.evaluation.projectloadsettings)). | Both are set. Without them an unresolvable SDK produced misleading `MissingImport` blockers and a circular import was accepted as a complete evaluation (found by comparing against the docs). |
| `EvaluationContext` extends the lifetime of evaluation caches across evaluations; create one, pass it to each, throw it away when the environment changes ([EvaluationContext](https://learn.microsoft.com/dotnet/api/microsoft.build.evaluation.context.evaluationcontext)). | One shared context per inventory run, discarded afterwards. |
| `ProjectImportedEventArgs.ImportIgnored` is set only for an import that would have been included but was ignored as invalid; not for a conditioned import that evaluated to false or a glob with no matches; `ImportedProjectFile` is null/empty for those ([ProjectImportedEventArgs](https://learn.microsoft.com/dotnet/api/microsoft.build.framework.projectimportedeventargs)). | `ImportLogger` records only ignored imports, so an ignored import with no file is one whose path expanded to nothing. Tests cover a true condition, a false condition and an unmatched glob. |

## Verified against a real MSBuild (not assumed)

- Booleans: `true/on/yes/!false/!off/!no` (any case) are true and `false/off/no/!true/...` are false, in both boolean and `== 'true'`
  contexts; `1`, `0` and other text are rejected with MSB4113. `InventoryMsBuildOracleTests` evaluates each spelling with MSBuild and
  requires our parsing to agree.
- A bare condition on an unset property (`Condition="$(X)"`) is rejected by MSBuild itself (MSB4113), so it surfaces as `EvaluationFailed`.
- A file imported twice is not an ignored import and does not fail evaluation.
- A locked or access-denied project file is reported by MSBuild as `InvalidProjectFileException`, not `IOException`.
- `Microsoft.NET.Test.Sdk`'s build props set `IsTestProject=true` for any project that references it, which is why restore output is
  ignored (`ImportProjectExtensionProps=false`): a restored production project that merely references it must not become a test project.
- `Project.GetItemProvenance` is documented as not yet implementing `Update`/`Remove`, but in practice returns them; the provenance tests
  pin that, so a regression in a newer MSBuild would be caught.

## Known limits

- One MSBuild/SDK per process (see above). Inventorying repositories that pin different SDKs needs one process per SDK.
- Package-contributed properties (from `obj/*.nuget.g.props`) are deliberately not seen; package evidence comes from `TestPackageCatalog`.
- Condition analysis follows property references in conditions; it does not evaluate conditions itself.
