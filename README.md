# vstest-to-mtp

A .NET global tool that migrates .NET test projects from [VSTest](https://github.com/microsoft/vstest) to [Microsoft.Testing.Platform (MTP)](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro) for use with `dotnet test`.

> **Status:** This is an early scaffold. The CLI entry point, packaging and CI are set up. The read-only project inventory (see below) exists as a library, but no migration behavior has been implemented yet.

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

`VsTestToMtp.Inventory.InventoryBuilder.Build(path)` inventories a `.csproj`, `.sln` or `.slnx` without modifying the repository: exact solution membership, MSBuild-evaluated properties, package references and project references per target framework (with file/line, condition and import-chain provenance), the imported `Directory.Build.props/targets` and `Directory.Packages.props`, the effective `global.json`, and CI/script files (generated and hidden directories are skipped). Projects are classified as test applications from evaluated `IsTestProject` and package evidence declared in the project itself, never from file names. Anything that cannot be determined (missing imports, evaluation failures, environment-dependent conditions, `IsTestProject` conditions in `Directory.Build.props`, ...) is reported as an actionable blocker instead of a guess.

Evaluation uses the .NET SDK's MSBuild (located with `Microsoft.Build.Locator`, honoring `global.json`), so an installed SDK is required at runtime. Nothing in the repository is restored, built or written, and `obj/*.nuget.g.*` files are ignored so results do not depend on restore state. As with any MSBuild evaluation, an SDK referenced with a version (for example `MSTest.Sdk/3.6.4`) that is not in the NuGet cache yet is downloaded there, as `dotnet restore` would; set `MSBUILDDISABLENUGETSDKRESOLVER=1` to prevent that (such projects are then reported as blockers). This is also what keeps classification correct: `Microsoft.NET.Test.Sdk` sets `IsTestProject=true` for every project that references it once restored, so a production project that merely references it must not be treated as a test application on that basis.

See [`docs/inventory-msbuild-usage.md`](docs/inventory-msbuild-usage.md) for the documented MSBuild API constraints, how the inventory complies, and what was verified against a real MSBuild.

### VSTest migration fixtures

[`fixtures/`](fixtures/README.md) contains checked-in MSTest, NUnit, xUnit v3 and xUnit v2 sample projects (each in plain and Central Package Management variants) generated from the official templates and running under VSTest. Converter tests work on isolated copies of them. CI builds and tests every fixture in place.

### Packing and installing locally

The tool uses [RID-specific packaging](https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools). A single `dotnet pack` produces self-contained packages for `win-x64`, `linux-x64` and `osx-arm64`, a portable `any` CoreCLR fallback, and a top-level pointer package:

```cli
dotnet pack --configuration Release -o ./artifacts/package/release
```

This creates:

- `vstest-to-mtp.win-x64.<version>.nupkg`
- `vstest-to-mtp.linux-x64.<version>.nupkg`
- `vstest-to-mtp.osx-arm64.<version>.nupkg`
- `vstest-to-mtp.any.<version>.nupkg` (portable CoreCLR fallback)
- `vstest-to-mtp.<version>.nupkg` (top-level pointer package)

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
