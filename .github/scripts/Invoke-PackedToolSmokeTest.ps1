param(
    # The directory holding the nupkg files produced by `dotnet pack` (pointer package plus the RID package under test).
    [Parameter(Mandatory)][string]$PackageDirectory,
    # The package under test: win-x64, linux-x64, osx-arm64 (Native AOT) or any (CoreCLR fallback).
    [Parameter(Mandatory)][string]$Rid,
    [string]$FixturesPath = (Join-Path $PSScriptRoot '../../fixtures'),
    [int]$ExpectedFixtureCount = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Installs the packed tool the way a user would and runs it over every committed fixture, so the packaging (AOT executable,
# MSBuild host folders, execute permissions) is exercised, not just the code. The fixtures are only read: nothing is restored or built.

$PackageDirectory = (Resolve-Path $PackageDirectory).Path
$toolPath = Join-Path ([IO.Path]::GetTempPath()) "vstest-to-mtp-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $toolPath | Out-Null

# The repository NuGet.config uses package source mapping, which rejects --add-source, so install with a config of our own.
$nugetConfig = Join-Path $toolPath 'NuGet.config'
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$PackageDirectory" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $nugetConfig

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$installDirectory = Join-Path $toolPath 'tool'
$version = (Get-ChildItem $PackageDirectory -Filter 'vstest-to-mtp.*.nupkg' |
    Where-Object { $_.Name -notmatch '\.(win-x64|linux-x64|osx-arm64|any)\.' } |
    Select-Object -First 1).BaseName -replace '^vstest-to-mtp\.', ''
# The pointer package resolves to the RID package for this machine. The any package is only chosen for platforms without a RID package,
# so install it directly by id to test it on a platform that has one.
$packageId = if ($Rid -eq 'any') { 'vstest-to-mtp.any' } else { 'vstest-to-mtp' }
Invoke-Checked dotnet @('tool', 'install', $packageId, '--tool-path', $installDirectory, '--configfile', $nugetConfig, '--version', $version, '--ignore-failed-sources')

# The shim is vstest-to-mtp.exe for a managed tool and vstest-to-mtp.cmd for a native (AOT) one on Windows; just vstest-to-mtp elsewhere.
$tool = @('vstest-to-mtp.exe', 'vstest-to-mtp.cmd', 'vstest-to-mtp') |
    ForEach-Object { Join-Path $installDirectory $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $tool) { throw "No vstest-to-mtp command was installed into '$installDirectory'." }
$isAot = $Rid -ne 'any'

Write-Host "::group::Installed layout"
Get-ChildItem $installDirectory -Recurse -Directory | Where-Object { $_.Name -like 'msbuild-host*' } | ForEach-Object { Write-Host $_.FullName }
Write-Host '::endgroup::'

# An AOT build is a native executable; the fallback is a managed .NET tool. Check the package is the kind we think it is.
$nativeFiles = @(Get-ChildItem $installDirectory -Recurse -File -Filter 'vstest-to-mtp.dll')
if ($isAot -and $nativeFiles.Count -ne 0) { throw "The $Rid package contains vstest-to-mtp.dll, so it is not a Native AOT build." }
if (-not $isAot -and $nativeFiles.Count -eq 0) { throw 'The any package has no vstest-to-mtp.dll, so it is not the CoreCLR fallback.' }

Invoke-Checked $tool @('--help')

$fixtureRoots = @(
    Get-ChildItem -Path $FixturesPath -Recurse -File -Filter global.json |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.Directory.FullName } |
        Sort-Object
)
if ($fixtureRoots.Count -ne $ExpectedFixtureCount) {
    throw "Expected $ExpectedFixtureCount fixture roots under '$FixturesPath' but found $($fixtureRoots.Count)."
}

function Invoke-Inventory {
    param([string]$FixtureRoot, [hashtable]$Environment = @{})

    $previous = @{}
    foreach ($name in $Environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $Environment[$name])
    }

    try {
        # Run from inside the fixture so its global.json selects the SDK, as a user would.
        Push-Location $FixtureRoot
        try {
            $output = & $tool inventory (Join-Path $FixtureRoot 'Fixture.slnx') --root $FixtureRoot 2>&1
            if ($LASTEXITCODE -ne 0) { throw "inventory failed for '$FixtureRoot' with exit code $LASTEXITCODE`n$output" }
            return ($output -join "`n")
        }
        finally {
            Pop-Location
        }
    }
    finally {
        foreach ($name in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name])
        }
    }
}

function Assert-Inventory {
    param([string]$Json, [string]$FixtureRoot)

    $result = $Json | ConvertFrom-Json
    $tests = @($result.projects | Where-Object { $_.classification -eq 'TestApplication' })
    $production = @($result.projects | Where-Object { $_.classification -eq 'Production' })
    if (@($result.blockers).Count -ne 0) { throw "'$FixtureRoot' reported blockers: $(($result.blockers | ForEach-Object { "$($_.code): $($_.message)" }) -join '; ')" }
    if (@($result.projects).Count -ne 2 -or $tests.Count -ne 1 -or $production.Count -ne 1) {
        throw "'$FixtureRoot' did not inventory one production and one test project: $(($result.projects | ForEach-Object { "$($_.path)=$($_.classification)" }) -join ', ')"
    }
    if (@($tests[0].frameworks).Count -ne 1) { throw "'$FixtureRoot' test project has no single detected test framework." }
    if (@($tests[0].runners) -notcontains 'VSTest') { throw "'$FixtureRoot' test project is not detected as running on VSTest." }
}

foreach ($root in $fixtureRoots) {
    Write-Host "::group::Inventory $root"
    $json = Invoke-Inventory -FixtureRoot $root
    Assert-Inventory -Json $json -FixtureRoot $root

    if ($isAot) {
        # The host normally runs on the machine's .NET runtime. Make `dotnet` unusable as a host launcher so the AOT package has to
        # fall back to its bundled, self-contained host, and require the same answer.
        $fallbackJson = Invoke-Inventory -FixtureRoot $root -Environment @{ DOTNET_HOST_PATH = Join-Path $toolPath 'no-such-dotnet' }
        Assert-Inventory -Json $fallbackJson -FixtureRoot $root
        if ($fallbackJson -ne $json) { throw "'$root': the bundled MSBuild host produced a different inventory than the machine-runtime host." }
    }

    Write-Host '::endgroup::'
}

Write-Host "Smoke-tested the $Rid package over $($fixtureRoots.Count) fixtures$(if ($isAot) { ' (machine-runtime host and bundled host)' })."
Remove-Item -Recurse -Force $toolPath -ErrorAction SilentlyContinue
