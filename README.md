# vstest-to-mtp

A .NET global tool that migrates .NET test projects from [VSTest](https://github.com/microsoft/vstest) to [Microsoft.Testing.Platform (MTP)](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro) for use with `dotnet test`.

> **Status:** This is an early scaffold. The CLI entry point, packaging and CI are set up. The read-only project inventory and the compatibility preflight (see below) exist as a library, but nothing is migrated yet.

## Install

Requires the .NET 10 SDK or later.

```cli
dotnet tool install --global vstest-to-mtp
```

## Usage

```cli
vstest-to-mtp --help
```

## Development

The repository uses Central Package Management (`Directory.Packages.props`), a `global.json` that pins the SDK and selects the native Microsoft.Testing.Platform `dotnet test` mode, and TUnit for tests.

```cli
dotnet build
dotnet test --no-build
dotnet run --project VsTestToMtp -- --help
```

### Project inventory

`VsTestToMtp.Inventory.InventoryBuilder.Build(path)` inventories a `.csproj`, `.sln` or `.slnx` without modifying the repository: exact solution membership, MSBuild-evaluated properties, package references and project references per target framework (with file/line, condition and import-chain provenance), the imported `Directory.Build.props/targets` and `Directory.Packages.props`, the effective `global.json`, and CI/script files (generated and hidden directories are skipped). Projects are classified as test applications from evaluated `IsTestProject` and package evidence declared in the project itself, never from file names. It also reports which test frameworks (`TestFramework`) and runner platforms (`TestRunnerKind`: VSTest and/or Microsoft.Testing.Platform) the project's packages indicate. Anything that cannot be determined (missing imports, evaluation failures, environment-dependent conditions, `IsTestProject` conditions in `Directory.Build.props`, ...) is reported as an actionable blocker instead of a guess.

Evaluation uses the .NET SDK's MSBuild (located with `Microsoft.Build.Locator`, honoring `global.json`), so an installed SDK is required at runtime. Because the CLI is a Native AOT executable and cannot load managed MSBuild assemblies, evaluation runs in a separate CoreCLR process, `VsTestToMtp.MsBuildHost` (the `vstest-to-mtp-msbuild-host` program in the package), started once per inventory run: the CLI sends a JSON request on its standard input and reads a JSON response from a file (see `MsBuildHostLauncher`). It runs on the machine's .NET runtime when one is installed (rolling forward to a newer major) and falls back to a self-contained copy bundled with the AOT packages when it is not. Nothing in the repository is restored, built or written, and `obj/*.nuget.g.*` files are ignored so results do not depend on restore state. As with any MSBuild evaluation, an SDK referenced with a version (for example `MSTest.Sdk/3.6.4`) that is not in the NuGet cache yet is downloaded there, as `dotnet restore` would; set `MSBUILDDISABLENUGETSDKRESOLVER=1` to prevent that (such projects are then reported as blockers). This is also what keeps classification correct: `Microsoft.NET.Test.Sdk` sets `IsTestProject=true` for every project that references it once restored, so a production project that merely references it must not be treated as a test application on that basis.

See [`docs/inventory-msbuild-usage.md`](docs/inventory-msbuild-usage.md) for the documented MSBuild API constraints, how the inventory complies, and what was verified against a real MSBuild.

### Migration rules and compatibility preflight

`VsTestToMtp.Migration.CompatibilityPreflight.Assess(inventory)` checks an inventory against a versioned, offline rule matrix (`MigrationRuleSet`) for Microsoft.Testing.Platform v2 on the .NET 10 SDK, before anything is changed. Each project is `Eligible`, `AlreadyMigrated`, `Blocked` or `NotATestApplication`, with the in-family package changes it needs (for example NUnit3TestAdapter 5 to 6, or `xunit.v3.mtp-off` to `xunit.v3.mtp-v2` at the same version) pointing at the exact `PackageReference` and version declaration to edit. MSTest, NUnit and xUnit v3 test applications keep their test framework and target framework; TUnit is recognized as already on MTP. xUnit v2, unsupported target frameworks, custom entry points, versions only a feed can resolve, unresolved MSBuild state and a selection that would mix VSTest and MTP test applications in one `dotnet test` run configuration are explicit error diagnostics, never silent rewrites; xUnit v2 is never moved to v3 and no project is retargeted.

See [`docs/migration-rules.md`](docs/migration-rules.md) for the matrix, the evidence behind each threshold and what was run to verify the upgrade targets.

### VSTest migration fixtures

[`fixtures/`](fixtures/README.md) contains checked-in MSTest, NUnit, xUnit v3 and xUnit v2 sample projects (each in plain and Central Package Management variants) generated from the official templates and running under VSTest. Converter tests work on isolated copies of them. CI builds and tests every fixture in place.
### Packing and installing locally

The tool uses [RID-specific packaging](https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools) with Native AOT for `win-x64`, `linux-x64` and `osx-arm64` and a portable CoreCLR `any` fallback. Native AOT compilation requires the build OS to match the target RID's OS, so each package is packed on its own platform (CI does this in a matrix):

```cli
dotnet pack --configuration Release -o ./artifacts/package/release                # pointer package (any platform)
dotnet pack -r win-x64 --configuration Release -o ./artifacts/package/release     # on Windows
dotnet pack -r linux-x64 --configuration Release -o ./artifacts/package/release   # on Linux
dotnet pack -r osx-arm64 --configuration Release -o ./artifacts/package/release   # on macOS (Apple silicon)
dotnet pack -r any --configuration Release -o ./artifacts/package/release         # portable CoreCLR fallback
```

This creates:

- `vstest-to-mtp.win-x64.<version>.nupkg` (Native AOT, with the MSBuild host framework-dependent and self-contained)
- `vstest-to-mtp.linux-x64.<version>.nupkg` (same)
- `vstest-to-mtp.osx-arm64.<version>.nupkg` (same)
- `vstest-to-mtp.any.<version>.nupkg` (portable CoreCLR fallback, framework-dependent MSBuild host)
- `vstest-to-mtp.<version>.nupkg` (top-level pointer package)

Native AOT builds need the platform's native toolchain (the Visual Studio C++ build tools on Windows, `clang` and `zlib1g-dev` on Linux, Xcode command line tools on macOS).

> **Note:** `dotnet pack` may emit `NU5017` on the pointer package. This is a known false positive in .NET SDK 10: NuGet validation does not yet recognize `DotnetToolSettings.xml` as package content. All packages are created correctly.

Install from the local output. The repository `NuGet.config` uses package source mapping, which rejects `--add-source`. Use a separate config file instead (for example `NuGet.local.config` in the repository root; do not commit it):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="./artifacts/package/release" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

```cli
dotnet tool install --global vstest-to-mtp --configfile ./NuGet.local.config
```

## Releasing

1. Add a [NuGet.org](https://www.nuget.org/account/apikeys) API key with push scope to the repository secrets as `NUGET_API_KEY`.
2. Create a GitHub release tagged `v*.*.*` (for example `v1.0.0`).

The deploy workflow publishes the RID-specific packages first and the pointer package last. This order is required: if the pointer package is published before its sub-packages, installs fail.

## Acknowledgements

Scaffolded from the [`bmichaelis.tool`](https://github.com/BenjaminMichaelis/DotnetTemplates/tree/main/templates/Library/DotnetTool) template.
