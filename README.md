# vstest-to-mtp

A .NET global tool that migrates .NET test projects from [VSTest](https://github.com/microsoft/vstest) to [Microsoft.Testing.Platform (MTP)](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro) for use with `dotnet test`.

> **Status:** This is an early scaffold. The CLI entry point, packaging and CI are set up, but no migration behavior has been implemented yet.

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
