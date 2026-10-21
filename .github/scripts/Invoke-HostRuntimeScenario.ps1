param(
    # Directory holding the nupkg files from `dotnet pack` for the RID under test and the `any` fallback (plus the pointer package).
    [Parameter(Mandatory)][string]$PackageDirectory,
    # The Native AOT package under test: win-x64 or linux-x64 (the scenarios need an OS that can install old SDKs side by side).
    [Parameter(Mandatory)][string]$Rid,
    # The scenario as JSON, from New-HostRuntimeScenarioPlan.ps1: { name, installs: [{ major, runtimeOnly? }], pinMajor }.
    [Parameter(Mandatory)][string]$Spec,
    [string]$FixtureRoot = (Join-Path $PSScriptRoot '../../fixtures/mstest/plain')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Runs the packed tool on a machine that has exactly the .NET installs the scenario asks for, and checks what each --host-runtime does there:
#   auto    the machine's runtime unless the bundled one is newer
#   machine only the installed runtime
#   bundled only the runtime shipped inside the package
# The expected outcome is derived from what actually got installed (stable runtimes, the pinned SDK), the bundled runtime and the oldest
# supported SDK, so it stays right when a preview becomes stable or the tool is retargeted. An outcome is 'ok' (a clean inventory), a blocker
# code the inventory must report, or 'cannot-run' (the command itself fails: the portable package needs a machine runtime to start at all).

$scenario = $Spec | ConvertFrom-Json
$name = $scenario.name
$modes = @('auto', 'machine', 'bundled')
$minimumSdk = [int]$scenario.minimumSdk

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

# The runtime the package bundles (written at build time next to the bundled host); its major is also the minimum the machine host needs.
$bundledFile = Get-ChildItem $aotDirectory -Recurse -Force -File -Filter 'runtime-version.txt' | Select-Object -First 1
if (-not $bundledFile) { throw "The $Rid package has no runtime-version.txt (no bundled runtime)." }
$bundledMajor = ([version](Get-Content $bundledFile.FullName -Raw).Trim()).Major

# --- Build the isolated machine ---------------------------------------------------------------------------------------------------------------
$dotnetRoot = Join-Path $work 'dotnet'
New-Item -ItemType Directory -Path $dotnetRoot | Out-Null

$installScript = Join-Path $work $(if ($IsWindows) { 'dotnet-install.ps1' } else { 'dotnet-install.sh' })
if (@($scenario.installs).Count -gt 0) {
    Invoke-WebRequest -Uri "https://dot.net/v1/$(Split-Path $installScript -Leaf)" -OutFile $installScript -MaximumRetryCount 3
}

foreach ($install in @($scenario.installs)) {
    $runtimeOnly = $install.PSObject.Properties.Name -contains 'runtimeOnly' -and $install.runtimeOnly
    $channel = "$($install.major).0"
    Write-Host "::group::Installing .NET $channel$(if ($runtimeOnly) { ' (runtime only)' }) into the isolated root"
    # The download is the one flaky step (the SDK archives are 250-300 MB); retry it a few times before giving up.
    $installed = $false
    for ($attempt = 1; $attempt -le 3 -and -not $installed; $attempt++) {
        try {
            $global:LASTEXITCODE = 0
            if ($IsWindows) {
                # A hashtable so the arguments are bound by name (an array would be passed positionally).
                $installArgs = @{ InstallDir = $dotnetRoot; Channel = $channel; NoPath = $true }
                if ($runtimeOnly) { $installArgs.Runtime = 'dotnet' }
                & $installScript @installArgs
            }
            else {
                $installArgs = @($installScript, '--install-dir', $dotnetRoot, '--channel', $channel, '--no-path')
                if ($runtimeOnly) { $installArgs += @('--runtime', 'dotnet') }
                & bash @installArgs
            }

            if ($LASTEXITCODE -ne 0) { throw "dotnet-install of $channel exited with $LASTEXITCODE" }
            $installed = $true
        }
        catch {
            Write-Host "::warning::Installing .NET $channel failed (attempt $attempt of 3): $($_.Exception.Message)"
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (5 * $attempt)
        }
    }

    Write-Host '::endgroup::'
}

# What the machine really has now. A channel installs its newest release even when that is only a preview.
$runtimeNames = @(Get-ChildItem (Join-Path $dotnetRoot 'shared/Microsoft.NETCore.App') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
$sdkNames = @(Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d' } | ForEach-Object { $_.Name })
Write-Host "Isolated machine: runtimes [$($runtimeNames -join ', ')], SDKs [$($sdkNames -join ', ')]"

# The runtime the machine host would run on, by .NET roll-forward: the highest release at or above the bundled major, and only when there is
# none a pre-release one (11.0.0-rc.2). With DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 (the scenario's opt-in) a pre-release may win over a release.
# Null when nothing qualifies.
$preReleaseOptIn = $scenario.PSObject.Properties.Name -contains 'rollForwardToPrerelease' -and [bool]$scenario.rollForwardToPrerelease
$qualifying = @($runtimeNames | ForEach-Object { [pscustomobject]@{ Major = ([version](($_ -split '-')[0])).Major; Stable = $_ -notmatch '-' } } | Where-Object { $_.Major -ge $bundledMajor })
$machineRuntime = $null
if ($preReleaseOptIn -and $qualifying.Count -gt 0) {
    $machineRuntime = [int]($qualifying | Measure-Object Major -Maximum).Maximum
}
else {
    foreach ($stable in $true, $false) {
        $candidates = @($qualifying | Where-Object { $_.Stable -eq $stable })
        if ($candidates.Count -gt 0) { $machineRuntime = [int]($candidates | Measure-Object Major -Maximum).Maximum; break }
    }
}

# --- The repository the tool inventories ----------------------------------------------------------------------------------------------------
$repo = Join-Path $work 'repo'
Copy-Item -Recurse -Path $FixtureRoot -Destination $repo
Get-ChildItem $repo -Recurse -Directory -Force | Where-Object { $_.Name -in 'bin', 'obj' } | Remove-Item -Recurse -Force

# What the projects target (a scenario may override the fixture's net10.0 in every project: an older target, a multi-target, .NET Framework, ...).
$projectTargets = if ($scenario.PSObject.Properties.Name -contains 'projectTargets' -and $scenario.projectTargets) { [string]$scenario.projectTargets } else { $null }
$expectedTargets = @()
if ($projectTargets) {
    $expectedTargets = @($projectTargets -split ';' | Sort-Object -CaseSensitive -Unique)
    $element = if ($expectedTargets.Count -gt 1) { "<TargetFrameworks>$projectTargets</TargetFrameworks>" } else { "<TargetFramework>$projectTargets</TargetFramework>" }
    foreach ($project in Get-ChildItem $repo -Recurse -File -Filter '*.csproj') {
        $text = Get-Content $project.FullName -Raw
        if ($text -notmatch '<TargetFramework>[^<]*</TargetFramework>') { throw "'$($project.FullName)' has no <TargetFramework> to replace." }
        Set-Content -Path $project.FullName -Value ($text -replace '<TargetFramework>[^<]*</TargetFramework>', $element) -NoNewline
    }
}

$pinnedSdk = $null
if ($null -ne $scenario.pinMajor) {
    $pinnedSdk = $sdkNames | Where-Object { ([version](($_ -split '-')[0])).Major -eq $scenario.pinMajor } | Sort-Object { [version](($_ -split '-')[0]) } -Descending | Select-Object -First 1
    if (-not $pinnedSdk) { throw "The scenario pins SDK $($scenario.pinMajor), but none was installed ([$($sdkNames -join ', ')])." }

    # Exactly the installed SDK (a pre-release one included), as a repository that pins it would.
    @{ sdk = [ordered]@{ version = $pinnedSdk; rollForward = 'disable'; allowPrerelease = $true } } | ConvertTo-Json | Set-Content -Path (Join-Path $repo 'global.json')
}

# --- Expected outcomes -------------------------------------------------------------------------------------------------------------------------
# What happens once the host runs on runtime $Runtime (a major) and loads the pinned SDK's MSBuild; mirrors MsBuildEnvironment.TryRegister.
function Get-HostOutcome([int]$Runtime) {
    if (-not $pinnedSdk) { return 'MsBuildNotFound' }
    $sdkMajor = ([version](($pinnedSdk -split '-')[0])).Major
    if ($sdkMajor -lt $minimumSdk) { return 'MsBuildSdkUnsupported' }
    if ($sdkMajor -gt $Runtime) { return 'MsBuildSdkNewerThanRuntime' }
    return 'ok'
}

function Get-Expected([string]$Package, [string]$Mode) {
    if ($Package -eq 'any') {
        # The portable CLI is itself a framework-dependent program: without a machine runtime it does not start.
        if ($null -eq $machineRuntime) { return 'cannot-run' }
        if ($Mode -eq 'bundled') { return 'MsBuildHostFailed' }
        return Get-HostOutcome $machineRuntime
    }

    switch ($Mode) {
        'machine' { if ($null -eq $machineRuntime) { return 'MsBuildHostFailed' } else { return Get-HostOutcome $machineRuntime } }
        'bundled' { return Get-HostOutcome $bundledMajor }
        default {
            # auto: the machine's runtime unless the bundled one is newer (or there is none), so the higher of the two.
            $runtime = if ($null -eq $machineRuntime) { $bundledMajor } else { [Math]::Max($machineRuntime, $bundledMajor) }
            return Get-HostOutcome $runtime
        }
    }
}

# --- Run every package and mode ---------------------------------------------------------------------------------------------------------------
# The environment a machine with only these installs would have: dotnet from the isolated root and nothing else.
$isolation = [ordered]@{
    PATH = $dotnetRoot
    DOTNET_ROOT = $dotnetRoot
    DOTNET_HOST_PATH = $null
    DOTNET_ROLL_FORWARD = $null
    DOTNET_ROLL_FORWARD_TO_PRERELEASE = $(if ($preReleaseOptIn) { '1' } else { $null })
    DOTNET_MULTILEVEL_LOOKUP = '0'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    MSBUILD_EXE_PATH = $null
    MSBuildSDKsPath = $null
}

function Invoke-Mode([string]$Tool, [string]$Mode) {
    $saved = @{}
    foreach ($key in $isolation.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $isolation[$key])
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
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
}

# 'ok' when the inventory is clean, a blocker code when it reports one, 'cannot-run' when the command fails before producing an inventory.
function Get-Outcome($Run) {
    if ($Run.ExitCode -ne 0) { return @{ Outcome = 'cannot-run'; Codes = @() } }
    try { $result = $Run.Output | ConvertFrom-Json } catch { return @{ Outcome = 'cannot-run'; Codes = @() } }

    $codes = @($result.blockers | ForEach-Object { $_.code } | Select-Object -Unique)
    if ($codes.Count -eq 0) {
        $kinds = @($result.projects | ForEach-Object { $_.classification } | Sort-Object)
        if (($kinds -join ',') -ne 'Production,TestApplication') { return @{ Outcome = 'unexpected-classification'; Codes = @() } }

        # Every project must be evaluated for exactly the target frameworks it declares, whatever they are.
        if ($expectedTargets.Count -gt 0) {
            foreach ($project in $result.projects) {
                $actualTargets = @($project.targetFrameworks | ForEach-Object { $_.targetFramework } | Sort-Object -CaseSensitive -Unique)
                if (($actualTargets -join ';') -ne ($expectedTargets -join ';')) {
                    return @{ Outcome = "unexpected-target-frameworks ($($project.path): $($actualTargets -join ';'), expected $($expectedTargets -join ';'))"; Codes = @() }
                }
            }
        }

        return @{ Outcome = 'ok'; Codes = @() }
    }

    return @{ Outcome = $codes[0]; Codes = $codes }
}

$failures = @()
$rows = @()
$packages = @(
    @{ Name = "aot ($Rid)"; Id = 'aot'; Tool = $aotTool },
    @{ Name = 'any'; Id = 'any'; Tool = $anyTool }
)

foreach ($package in $packages) {
    foreach ($mode in $modes) {
        $expected = Get-Expected $package.Id $mode
        $run = Invoke-Mode $package.Tool $mode
        $actual = Get-Outcome $run
        $passed = if ($expected -in 'ok', 'cannot-run') { $actual.Outcome -eq $expected } else { $actual.Codes -contains $expected }

        # The plan fixes some outcomes whatever is installed. Expectations derived from the real machine must agree with them: if they do not, this
        # is not the machine the scenario was planned for (the wrong SDK installed, a channel that moved), and passing would test something else.
        $required = @($scenario.required | Where-Object { $_.package -eq $package.Id -and $_.mode -eq $mode })
        $planned = $required.Count -gt 0
        $matchesPlan = -not $planned -or $required[0].outcome -eq $expected

        $rows += [pscustomobject]@{ Scenario = $name; Package = $package.Name; Mode = $mode; Expected = $expected; Planned = $(if ($planned) { $required[0].outcome } else { '-' }); Actual = $(if ($actual.Codes.Count) { $actual.Codes -join '+' } else { $actual.Outcome }); Result = $(if ($passed -and $matchesPlan) { 'pass' } else { 'FAIL' }) }
        if (-not $matchesPlan) {
            $failures += "$($package.Name) --host-runtime ${mode}: the plan requires '$($required[0].outcome)' but this machine implies '$expected'. The installed .NET is not what the scenario was planned for (runtimes [$($runtimeNames -join ', ')], SDKs [$($sdkNames -join ', ')])."
        }

        if (-not $passed) {
            $failures += "$($package.Name) --host-runtime ${mode}: expected '$expected' but got '$($actual.Outcome)' (exit $($run.ExitCode))`n$($run.Output.Substring(0, [Math]::Min(1500, $run.Output.Length)))"
        }
    }
}

Write-Host "Machine: host runtime major $(if ($null -ne $machineRuntime) { $machineRuntime } else { 'none' }); bundled runtime $bundledMajor; pinned SDK $(if ($pinnedSdk) { $pinnedSdk } else { 'none' }); minimum supported SDK $minimumSdk"
Write-Host ($rows | Format-Table -AutoSize | Out-String)
if ($env:GITHUB_STEP_SUMMARY) {
    "### Host runtime scenario: ``$name``" | Add-Content $env:GITHUB_STEP_SUMMARY
    "Machine host runtime $(if ($null -ne $machineRuntime) { $machineRuntime } else { 'none' }), bundled runtime $bundledMajor, pinned SDK $(if ($pinnedSdk) { $pinnedSdk } else { 'none' })" | Add-Content $env:GITHUB_STEP_SUMMARY
    '' | Add-Content $env:GITHUB_STEP_SUMMARY
    '| Package | Mode | Required by plan | Expected | Actual | Result |' | Add-Content $env:GITHUB_STEP_SUMMARY
    '|---|---|---|---|---|---|' | Add-Content $env:GITHUB_STEP_SUMMARY
    foreach ($row in $rows) { "| $($row.Package) | $($row.Mode) | $($row.Planned) | $($row.Expected) | $($row.Actual) | $($row.Result) |" | Add-Content $env:GITHUB_STEP_SUMMARY }
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
if ($failures.Count -gt 0) { throw "Scenario '$name' did not behave as expected:`n$($failures -join "`n`n")" }

# Native commands above (the any tool failing on purpose) leave a non-zero $LASTEXITCODE, which a CI step would report as its exit code.
$global:LASTEXITCODE = 0
Write-Host "Scenario '$name': all $($rows.Count) combinations behaved as expected."
