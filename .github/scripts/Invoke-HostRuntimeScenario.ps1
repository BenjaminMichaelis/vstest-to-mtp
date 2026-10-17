param(
    # Directory holding the nupkg files from `dotnet pack` for the RID under test and the `any` fallback (plus the pointer package).
    [Parameter(Mandatory)][string]$PackageDirectory,
    # The Native AOT package under test: win-x64 or linux-x64 (the scenarios need an OS that can install old SDKs side by side).
    [Parameter(Mandatory)][string]$Rid,
    # One of the scenario names in $scenarios below.
    [Parameter(Mandatory)][string]$Scenario,
    [string]$FixtureRoot = (Join-Path $PSScriptRoot '../../fixtures/mstest/plain')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Runs the packed tool on a machine that has exactly one set of .NET installs and checks what each --host-runtime does there:
#   auto    the machine's runtime unless the bundled one is newer
#   machine only the installed runtime
#   bundled only the runtime shipped inside the package
# Expected outcomes: 'ok' (a clean inventory), a blocker code the inventory must report, or 'cannot-run' (the command itself fails, e.g.
# the portable package needs a .NET 10 runtime to start at all). Each scenario is an isolated dotnet root; nothing else on the runner is visible.

$modes = @('auto', 'machine', 'bundled')

# Install: what goes into the isolated root. Pin: the global.json the repository would have (null = the fixture's own).
# Aot / Any: expected outcome per mode (auto, machine, bundled) for the Native AOT package and the portable `any` package.
$scenarios = [ordered]@{
    'sdk10' = @{
        Install = @(@{ Channel = '10.0' }); Pin = $null
        Aot = @('ok', 'ok', 'ok'); Any = @('ok', 'ok', 'MsBuildHostFailed')
    }
    'sdk9' = @{
        # No runtime 10 on the machine: only the bundled runtime can run the host, and it loads SDK 9's MSBuild.
        Install = @(@{ Channel = '9.0' }); Pin = @{ version = '9.0.100'; rollForward = 'latestFeature' }
        Aot = @('ok', 'MsBuildHostFailed', 'ok'); Any = @('cannot-run', 'cannot-run', 'cannot-run')
    }
    'sdk8' = @{
        # The oldest SDK whose MSBuild we support.
        Install = @(@{ Channel = '8.0' }); Pin = @{ version = '8.0.100'; rollForward = 'latestFeature' }
        Aot = @('ok', 'MsBuildHostFailed', 'ok'); Any = @('cannot-run', 'cannot-run', 'cannot-run')
    }
    'sdk6' = @{
        # Not supported: the SDK is older than the oldest we can load. The bundled runtime is what reports it; the machine one cannot even run the host.
        Install = @(@{ Channel = '6.0' }); Pin = @{ version = '6.0.100'; rollForward = 'latestFeature' }
        Aot = @('MsBuildSdkUnsupported', 'MsBuildHostFailed', 'MsBuildSdkUnsupported'); Any = @('cannot-run', 'cannot-run', 'cannot-run')
    }
    'sdk9-and-10' = @{
        # Both installed, the repository pins 9: the machine's runtime 10 runs the host, which loads SDK 9's MSBuild.
        Install = @(@{ Channel = '9.0' }, @{ Channel = '10.0' }); Pin = @{ version = '9.0.100'; rollForward = 'latestFeature' }
        Aot = @('ok', 'ok', 'ok'); Any = @('ok', 'ok', 'MsBuildHostFailed')
    }
    'sdk10-and-11' = @{
        # A newer major side by side (skipped until a stable .NET 11 can be installed). The repository pins 11: the machine's runtime 11 runs the
        # host, but the bundled runtime 10 cannot load SDK 11's MSBuild.
        Install = @(@{ Channel = '10.0' }, @{ Channel = '11.0' }); Pin = @{ version = '11.0.100'; rollForward = 'latestFeature' }; SkipWhenUnavailable = $true
        Aot = @('ok', 'ok', 'MsBuildSdkNewerThanRuntime'); Any = @('ok', 'ok', 'MsBuildHostFailed')
    }
    'sdk10-and-newest-11' = @{
        # A newer major next to the one the repository uses, whatever its quality today (a release candidate now, stable later): the host must keep
        # working. A pre-release runtime is ignored by roll-forward (the host stays on 10); once 11 is stable the host runs on 11 and loads SDK 10's MSBuild.
        Install = @(@{ Channel = '10.0' }, @{ Channel = '11.0' }); Pin = $null
        Aot = @('ok', 'ok', 'ok'); Any = @('ok', 'ok', 'MsBuildHostFailed')
    }
    'runtime10-no-sdk' = @{
        # A runtime but no SDK: the host runs, then finds no MSBuild to load.
        Install = @(@{ Channel = '10.0'; RuntimeOnly = $true }); Pin = $null
        Aot = @('MsBuildNotFound', 'MsBuildNotFound', 'MsBuildNotFound'); Any = @('MsBuildNotFound', 'MsBuildNotFound', 'MsBuildHostFailed')
    }
    'no-dotnet' = @{
        # Nothing installed: the bundled host starts (it needs no machine runtime) and reports the missing SDK; the machine host has no `dotnet`.
        Install = @(); Pin = $null
        Aot = @('MsBuildNotFound', 'MsBuildHostFailed', 'MsBuildNotFound'); Any = @('cannot-run', 'cannot-run', 'cannot-run')
    }
}

if (-not $scenarios.Contains($Scenario)) { throw "Unknown scenario '$Scenario'. Known: $($scenarios.Keys -join ', ')." }
$plan = $scenarios[$Scenario]

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

$work = Join-Path ([IO.Path]::GetTempPath()) "vstest-to-mtp-scenario-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
$PackageDirectory = (Resolve-Path $PackageDirectory).Path

# --- Install the packed tools with the runner's own dotnet (before any isolation) -------------------------------------------------------------
$nugetConfig = Join-Path $work 'NuGet.config'
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$PackageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $nugetConfig

$version = (Get-ChildItem $PackageDirectory -Filter 'vstest-to-mtp.*.nupkg' |
    Where-Object { $_.Name -notmatch '\.(win-x64|win-arm64|linux-x64|linux-arm64|osx-arm64|any)\.' } |
    Select-Object -First 1).BaseName -replace '^vstest-to-mtp\.', ''

function Install-Tool([string]$PackageId, [string]$Directory) {
    Invoke-Checked dotnet @('tool', 'install', $PackageId, '--tool-path', $Directory, '--configfile', $nugetConfig, '--version', $version, '--ignore-failed-sources')
}

$aotDirectory = Join-Path $work 'aot'
$anyDirectory = Join-Path $work 'any'
Install-Tool 'vstest-to-mtp' $aotDirectory
Install-Tool 'vstest-to-mtp.any' $anyDirectory

# The AOT package's real executable (no shim, so it needs nothing on PATH); the portable package's shim (an apphost that finds its runtime via DOTNET_ROOT).
$exeName = if ($IsWindows) { 'vstest-to-mtp.exe' } else { 'vstest-to-mtp' }
$aotTool = Get-ChildItem $aotDirectory -Recurse -Force -File -Filter $exeName | Where-Object { $_.FullName -match [regex]::Escape($Rid) } | Select-Object -First 1 -ExpandProperty FullName
$anyTool = Join-Path $anyDirectory $exeName
if (-not $aotTool) { throw "No $exeName for $Rid was found under '$aotDirectory'." }
if (-not (Test-Path $anyTool)) { throw "No $exeName was installed into '$anyDirectory'." }

# --- Build the isolated machine ---------------------------------------------------------------------------------------------------------------
$dotnetRoot = Join-Path $work 'dotnet'
New-Item -ItemType Directory -Path $dotnetRoot | Out-Null

$installScript = Join-Path $work $(if ($IsWindows) { 'dotnet-install.ps1' } else { 'dotnet-install.sh' })
if ($plan.Install.Count -gt 0) {
    Invoke-WebRequest -Uri "https://dot.net/v1/$(Split-Path $installScript -Leaf)" -OutFile $installScript -MaximumRetryCount 3
}

foreach ($install in $plan.Install) {
    Write-Host "::group::Installing .NET $($install.Channel)$(if ($install.ContainsKey('RuntimeOnly')) { ' (runtime only)' }) into the isolated root"
    $runtimeOnly = $install.ContainsKey('RuntimeOnly')
    try {
        if ($IsWindows) {
            # A hashtable so the arguments are bound by name (an array would be passed positionally).
            $installArgs = @{ InstallDir = $dotnetRoot; Channel = $install.Channel; NoPath = $true }
            if ($runtimeOnly) { $installArgs.Runtime = 'dotnet' }
            & $installScript @installArgs
        }
        else {
            $installArgs = @($installScript, '--install-dir', $dotnetRoot, '--channel', $install.Channel, '--no-path')
            if ($runtimeOnly) { $installArgs += @('--runtime', 'dotnet') }
            & bash @installArgs
        }

        if ($LASTEXITCODE -ne 0) { throw "dotnet-install exited with $LASTEXITCODE" }
    }
    catch {
        if ($plan.ContainsKey('SkipWhenUnavailable')) {
            Write-Host "::notice::Skipping scenario '$Scenario': .NET $($install.Channel) cannot be installed yet ($($_.Exception.Message))."
            Write-Host '::endgroup::'
            Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
            return
        }

        throw
    }

    Write-Host '::endgroup::'
}

# dotnet-install falls back to the newest release in a channel even when it is only a preview/RC. A pre-release runtime is ignored by the
# host's roll-forward (and a pre-release SDK is not selected by a plain pin), so the scenario is only meaningful once a stable one exists.
if ($plan.ContainsKey('SkipWhenUnavailable')) {
    $shared = Join-Path $dotnetRoot 'shared/Microsoft.NETCore.App'
    $preview = @(Get-ChildItem $shared -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '-' })
    if ($preview.Count -gt 0) {
        Write-Host "::notice::Skipping scenario '$Scenario': only a pre-release is available ($($preview.Name -join ', ')); it runs once a stable release exists."
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
        return
    }
}

$dotnetName = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
if (Test-Path (Join-Path $dotnetRoot $dotnetName)) {
    Write-Host "::group::Isolated machine"
    & (Join-Path $dotnetRoot $dotnetName) --info | Select-String -Pattern 'Version:|Microsoft.NETCore.App' | ForEach-Object { Write-Host $_.Line }
    Write-Host '::endgroup::'
}
else {
    Write-Host 'Isolated machine: no dotnet installed.'
}

# --- The repository the tool inventories ----------------------------------------------------------------------------------------------------
$repo = Join-Path $work 'repo'
Copy-Item -Recurse -Path $FixtureRoot -Destination $repo
Get-ChildItem $repo -Recurse -Directory -Force | Where-Object { $_.Name -in 'bin', 'obj' } | Remove-Item -Recurse -Force
if ($null -ne $plan.Pin) {
    @{ sdk = $plan.Pin } | ConvertTo-Json | Set-Content -Path (Join-Path $repo 'global.json')
}

# --- Run every mode ----------------------------------------------------------------------------------------------------------------------------
# The environment a machine with only these installs would have: dotnet from the isolated root and nothing else.
$isolation = [ordered]@{
    PATH = $dotnetRoot
    DOTNET_ROOT = $dotnetRoot
    DOTNET_HOST_PATH = $null
    DOTNET_ROLL_FORWARD = $null
    DOTNET_ROLL_FORWARD_TO_PRERELEASE = $null
    DOTNET_MULTILEVEL_LOOKUP = '0'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    MSBUILD_EXE_PATH = $null
    MSBuildSDKsPath = $null
}

function Invoke-Mode([string]$Tool, [string]$Mode) {
    $saved = @{}
    foreach ($name in $isolation.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $isolation[$name])
    }

    try {
        Push-Location $repo
        try {
            $output = & $Tool inventory (Join-Path $repo 'Fixture.slnx') --root $repo --host-runtime $Mode 2>&1
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
        }
        finally { Pop-Location }
    }
    finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}

# 'ok' when the inventory is clean, a blocker code when it reports one, 'cannot-run' when the command fails before producing an inventory.
function Get-Outcome($Run) {
    if ($Run.ExitCode -ne 0) { return @{ Outcome = 'cannot-run'; Codes = @() } }
    try { $result = $Run.Output | ConvertFrom-Json } catch { return @{ Outcome = 'cannot-run'; Codes = @() } }

    $codes = @($result.blockers | ForEach-Object { $_.code } | Select-Object -Unique)
    if ($codes.Count -eq 0) {
        $kinds = @($result.projects | ForEach-Object { $_.classification } | Sort-Object)
        return @{ Outcome = $(if (($kinds -join ',') -eq 'Production,TestApplication') { 'ok' } else { 'unexpected-classification' }); Codes = @() }
    }

    return @{ Outcome = $codes[0]; Codes = $codes }
}

$failures = @()
$rows = @()
$packages = @(
    @{ Name = "aot ($Rid)"; Tool = $aotTool; Expected = $plan.Aot },
    @{ Name = 'any'; Tool = $anyTool; Expected = $plan.Any }
)

foreach ($package in $packages) {
    for ($i = 0; $i -lt $modes.Count; $i++) {
        $mode = $modes[$i]
        $expected = $package.Expected[$i]
        $run = Invoke-Mode $package.Tool $mode
        $actual = Get-Outcome $run
        $passed = if ($expected -in 'ok', 'cannot-run') { $actual.Outcome -eq $expected } else { $actual.Codes -contains $expected }
        $rows += [pscustomobject]@{ Scenario = $Scenario; Package = $package.Name; Mode = $mode; Expected = $expected; Actual = $(if ($actual.Codes.Count) { $actual.Codes -join '+' } else { $actual.Outcome }); Result = $(if ($passed) { 'pass' } else { 'FAIL' }) }
        if (-not $passed) {
            $failures += "$($package.Name) --host-runtime ${mode}: expected '$expected' but got '$($actual.Outcome)' (exit $($run.ExitCode))`n$($run.Output.Substring(0, [Math]::Min(1500, $run.Output.Length)))"
        }
    }
}

$table = $rows | Format-Table -AutoSize | Out-String
Write-Host $table
if ($env:GITHUB_STEP_SUMMARY) {
    "### Host runtime scenario: ``$Scenario``" | Add-Content $env:GITHUB_STEP_SUMMARY
    '| Package | Mode | Expected | Actual | Result |' | Add-Content $env:GITHUB_STEP_SUMMARY
    '|---|---|---|---|---|' | Add-Content $env:GITHUB_STEP_SUMMARY
    foreach ($row in $rows) { "| $($row.Package) | $($row.Mode) | $($row.Expected) | $($row.Actual) | $($row.Result) |" | Add-Content $env:GITHUB_STEP_SUMMARY }
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
if ($failures.Count -gt 0) { throw "Scenario '$Scenario' did not behave as expected:`n$($failures -join "`n`n")" }
# Native commands above (the any tool failing on purpose) leave a non-zero $LASTEXITCODE, which a CI step would report as its exit code.
$global:LASTEXITCODE = 0
Write-Host "Scenario '$Scenario': all $($rows.Count) combinations behaved as expected."
