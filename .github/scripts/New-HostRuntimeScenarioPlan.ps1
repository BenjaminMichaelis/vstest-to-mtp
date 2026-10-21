param(
    # Receives the matrix as `matrix=<json>` when running in GitHub Actions; otherwise the plan is only printed.
    [string]$OutputFile = $env:GITHUB_OUTPUT,
    [string]$ReleasesIndexUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Decides which machine setups the host-runtime scenario job runs, from facts that move on their own:
#   B           the major of the runtime bundled with the tool = the target framework of the MSBuild host project
#   minimum     the oldest SDK major whose MSBuild we can load (MsBuildEnvironment.MinimumSdkMajorVersion)
#   the releases index: the newest stable major and the newest major of any quality (a preview or release candidate)
# Everything that encodes policy (the minimum, one unsupported anchor below it) is derived from the code; the rest follows the releases.
# Expected outcomes are not decided here: Invoke-HostRuntimeScenario.ps1 derives them from what actually got installed.

$root = Join-Path $PSScriptRoot '../..'

$hostProject = Get-Content (Join-Path $root 'VsTestToMtp.MsBuildHost/VsTestToMtp.MsBuildHost.csproj') -Raw
if ($hostProject -notmatch '<TargetFramework>net(\d+)\.\d+</TargetFramework>') { throw 'Could not read the MSBuild host target framework.' }
$bundled = [int]$Matches[1]

$environmentSource = Get-Content (Join-Path $root 'VsTestToMtp.MsBuildHost/Inventory/Evaluation/MsBuildEnvironment.cs') -Raw
if ($environmentSource -notmatch 'MinimumSdkMajorVersion\s*=\s*(\d+)') { throw 'Could not read MinimumSdkMajorVersion.' }
$minimum = [int]$Matches[1]
if (-not $minimum) { throw 'MinimumSdkMajorVersion is not a positive number.' }

# A local file is accepted too, to plan against a hypothetical releases index (for example one with no newer major).
$indexDocument = if (Test-Path -LiteralPath $ReleasesIndexUrl) { Get-Content -LiteralPath $ReleasesIndexUrl -Raw | ConvertFrom-Json } else { Invoke-RestMethod -Uri $ReleasesIndexUrl -MaximumRetryCount 3 }
$index = $indexDocument.'releases-index'
$channels = @($index | ForEach-Object {
        [pscustomobject]@{
            Major = [int]($_.'channel-version' -split '\.')[0]
            Minor = [int]($_.'channel-version' -split '\.')[1]
            Type = $_.'release-type'
            Phase = $_.'support-phase'
        }
    } | Where-Object { $_.Minor -eq 0 })
$isPreview = { param($c) $c.Phase -in 'preview', 'go-live', 'rc' }
$stable = @($channels | Where-Object { -not (& $isPreview $_) })
$newestStable = [int]($stable | Measure-Object Major -Maximum).Maximum
$newestAny = [int]($channels | Measure-Object Major -Maximum).Maximum
if ($newestStable -lt $bundled) { throw "The newest stable .NET major ($newestStable) is older than the bundled one ($bundled); check the releases index." }

# --- Which SDK majors a repository might pin ---------------------------------------------------------------------------------------------------
$majors = [System.Collections.Generic.SortedSet[int]]::new()
[void]$majors.Add($minimum - 2)                       # one unsupported anchor
[void]$majors.Add($minimum)                           # the oldest we support
if ($bundled - 1 -ge $minimum) { [void]$majors.Add($bundled - 1) }
foreach ($m in $bundled..$newestAny) { [void]$majors.Add($m) }   # B up to the newest, previews included

# LTS and STS should both be represented among the supported range; add the newest of a missing kind, never a duplicate.
foreach ($type in 'lts', 'sts') {
    $covered = @($channels | Where-Object { $_.Type -eq $type -and $majors.Contains($_.Major) -and $_.Major -ge $minimum -and $_.Major -le $newestStable })
    if ($covered.Count -eq 0) {
        $candidate = $stable | Where-Object { $_.Type -eq $type -and $_.Major -ge $minimum -and $_.Major -le $newestStable } | Sort-Object Major -Descending | Select-Object -First 1
        if ($candidate) { [void]$majors.Add($candidate.Major) }
    }
}

# --- Scenarios ---------------------------------------------------------------------------------------------------------------------------------------
# Each scenario carries `required`: outcomes that must hold whatever the installed .NET looks like (a preview or stable release, a patch level),
# expressed only through how the pinned SDK relates to the oldest supported SDK and to the bundled major B. The run script derives its expectations
# from what is really installed and then insists they equal these: if the machine is not the one the scenario was planned for (the wrong SDK got
# installed, a channel moved), the job fails instead of quietly testing something else.
function New-Required([string]$Package, [string]$Mode, [string]$Outcome) { [ordered]@{ package = $Package; mode = $Mode; outcome = $Outcome } }

function Get-SingleSdkRequired([int]$Major) {
    if ($Major -lt $minimum) {
        # Below the floor: the bundled runtime runs the host and it refuses the SDK; the machine host cannot run (the SDK's runtime is older than B).
        return @(
            (New-Required 'aot' 'auto' 'MsBuildSdkUnsupported'), (New-Required 'aot' 'machine' 'MsBuildHostFailed'), (New-Required 'aot' 'bundled' 'MsBuildSdkUnsupported'),
            (New-Required 'any' 'auto' 'cannot-run'), (New-Required 'any' 'machine' 'cannot-run'), (New-Required 'any' 'bundled' 'cannot-run'))
    }

    if ($Major -lt $bundled) {
        # Supported but older than B: only the bundled runtime can run the host, and it loads this SDK's MSBuild.
        return @(
            (New-Required 'aot' 'auto' 'ok'), (New-Required 'aot' 'machine' 'MsBuildHostFailed'), (New-Required 'aot' 'bundled' 'ok'),
            (New-Required 'any' 'auto' 'cannot-run'), (New-Required 'any' 'machine' 'cannot-run'), (New-Required 'any' 'bundled' 'cannot-run'))
    }

    if ($Major -eq $bundled) {
        return @(
            (New-Required 'aot' 'auto' 'ok'), (New-Required 'aot' 'machine' 'ok'), (New-Required 'aot' 'bundled' 'ok'),
            (New-Required 'any' 'auto' 'ok'), (New-Required 'any' 'machine' 'ok'), (New-Required 'any' 'bundled' 'MsBuildHostFailed'))
    }

    # Newer than B: the bundled runtime can never load it. Whether the machine's runtime can depends on that release being stable yet, so only
    # the bundled column is fixed here (the portable package has no bundled runtime to offer).
    return @((New-Required 'aot' 'bundled' 'MsBuildSdkNewerThanRuntime'), (New-Required 'any' 'bundled' 'MsBuildHostFailed'))
}

$scenarios = [System.Collections.Generic.List[object]]::new()
function Add-Scenario([string]$Name, [object[]]$Installs, $PinMajor, [object[]]$Required, [string]$ProjectTargets = $null, [string]$Scope = 'all', [bool]$PrereleaseOptIn = $false) {
    $scenarios.Add([ordered]@{ name = $Name; installs = $Installs; pinMajor = $PinMajor; minimumSdk = $minimum; projectTargets = $ProjectTargets; scope = $Scope; rollForwardToPrerelease = $PrereleaseOptIn; required = $Required })
}

foreach ($m in $majors) {
    Add-Scenario "sdk$m" @(@{ major = $m }) $m (Get-SingleSdkRequired $m)
}

# A machine that has the bundled major next to another SDK: the host runs on the machine's runtime and loads whichever SDK the repository pins.
$bothWork = @(
    (New-Required 'aot' 'auto' 'ok'), (New-Required 'aot' 'machine' 'ok'), (New-Required 'aot' 'bundled' 'ok'),
    (New-Required 'any' 'auto' 'ok'), (New-Required 'any' 'machine' 'ok'), (New-Required 'any' 'bundled' 'MsBuildHostFailed'))
if ($bundled - 1 -ge $minimum) { Add-Scenario "sdk$($bundled - 1)-and-$bundled" @(@{ major = $bundled - 1 }, @{ major = $bundled }) ($bundled - 1) $bothWork }
if ($newestAny -gt $bundled) { Add-Scenario "sdk$bundled-and-$newestAny" @(@{ major = $bundled }, @{ major = $newestAny }) $bundled $bothWork }

# The repository pins the newest SDK while the machine also has the bundled major: the host stays on the bundled major's release runtime unless the
# newest is stable, or DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 lets it move to a newer pre-release (a release candidate today). Both settings. With the
# opt-in the host can always reach the newest runtime, so that scenario is fully determined.
if ($newestAny -gt $bundled) {
    Add-Scenario "sdk$newestAny-and-$bundled" @(@{ major = $bundled }, @{ major = $newestAny }) $newestAny (Get-SingleSdkRequired $newestAny)
    Add-Scenario "sdk$newestAny-and-$bundled-prerelease-opt-in" @(@{ major = $bundled }, @{ major = $newestAny }) $newestAny @(
        (New-Required 'aot' 'auto' 'ok'), (New-Required 'aot' 'machine' 'ok'), (New-Required 'aot' 'bundled' 'MsBuildSdkNewerThanRuntime'),
        (New-Required 'any' 'auto' 'ok'), (New-Required 'any' 'machine' 'ok'), (New-Required 'any' 'bundled' 'MsBuildHostFailed')) $null 'all' $true
}

# A runtime but no SDK: the host runs, then finds no MSBuild to load. Nothing at all: the bundled host runs and reports the same.
Add-Scenario "runtime$bundled-no-sdk" @(@{ major = $bundled; runtimeOnly = $true }) $null @(
    (New-Required 'aot' 'auto' 'MsBuildNotFound'), (New-Required 'aot' 'machine' 'MsBuildNotFound'), (New-Required 'aot' 'bundled' 'MsBuildNotFound'),
    (New-Required 'any' 'auto' 'MsBuildNotFound'), (New-Required 'any' 'machine' 'MsBuildNotFound'), (New-Required 'any' 'bundled' 'MsBuildHostFailed'))
Add-Scenario 'no-dotnet' @() $null @(
    (New-Required 'aot' 'auto' 'MsBuildNotFound'), (New-Required 'aot' 'machine' 'MsBuildHostFailed'), (New-Required 'aot' 'bundled' 'MsBuildNotFound'),
    (New-Required 'any' 'auto' 'cannot-run'), (New-Required 'any' 'machine' 'cannot-run'), (New-Required 'any' 'bundled' 'cannot-run'))

# What the projects target is independent of the machine: an older target, a multi-target, a .NET Framework target and, when a newer major than the
# bundled one exists, a target newer than most SDKs know. Crossed with the two ends of the supported range (the bundled major's SDK and the oldest
# supported SDK) rather than every machine, and on Linux only (the evaluation does not depend on the OS).
$older = "net$($minimum - 2).0"
$targetSets = [System.Collections.Generic.List[object]]::new()
$targetSets.Add(@{ key = 'older'; value = $older })
$targetSets.Add(@{ key = 'multi'; value = "$older;net$bundled.0" })
$targetSets.Add(@{ key = 'framework'; value = 'net48' })
if ($newestAny -gt $bundled) { $targetSets.Add(@{ key = 'newer'; value = "net$newestAny.0" }) }
foreach ($sdk in @($bundled, $minimum | Select-Object -Unique)) {
    foreach ($set in $targetSets) {
        Add-Scenario "sdk$sdk-targets-$($set.key)" @(@{ major = $sdk }) $sdk (Get-SingleSdkRequired $sdk) $set.value 'linux'
    }
}

# --- Coverage ------------------------------------------------------------------------------------------------------------------------------------------
# Every condition we promise to handle must have a scenario, or the plan fails: a scenario silently disappearing (a releases index that moved, a
# refactor of this script) would otherwise leave that condition untested with a green build.
function Get-SingleSdkScenarios([scriptblock]$Filter) {
    @($scenarios | Where-Object { @($_.installs).Count -eq 1 -and $null -ne $_.pinMajor -and -not $_.projectTargets -and (& $Filter $_.pinMajor) })
}

$coverage = [ordered]@{
    'SDK below the supported floor is refused' = Get-SingleSdkScenarios { param($m) $m -lt $minimum }
    'SDK equal to the bundled runtime works everywhere' = Get-SingleSdkScenarios { param($m) $m -eq $bundled }
    'A runtime with no SDK is reported' = @($scenarios | Where-Object { $_.name -eq "runtime$bundled-no-sdk" })
    'No .NET at all is reported' = @($scenarios | Where-Object { $_.name -eq 'no-dotnet' })
    'Projects targeting an older, multiple or .NET Framework target' = @($scenarios | Where-Object { $_.name -like '*-targets-older' -or $_.name -like '*-targets-multi' -or $_.name -like '*-targets-framework' })
}
if ($minimum -lt $bundled) {
    $coverage['SDK from the floor up to below the bundled runtime works through the bundled runtime'] = Get-SingleSdkScenarios { param($m) $m -ge $minimum -and $m -lt $bundled }
    $coverage['Older repository next to the bundled major'] = @($scenarios | Where-Object { $_.name -eq "sdk$($bundled - 1)-and-$bundled" })
}

if ($newestAny -gt $bundled) {
    $coverage['SDK newer than the bundled runtime is refused by the bundled host'] = Get-SingleSdkScenarios { param($m) $m -gt $bundled }
    $coverage['Newest SDK next to the bundled major, with and without the pre-release opt-in'] = @($scenarios | Where-Object { $_.name -like "sdk$newestAny-and-$bundled*" })
}
else {
    # Not an error: until the next major has a first preview there is nothing newer to install. It is loud so nobody mistakes it for coverage.
    Write-Host "::warning::The releases index has no .NET major newer than the bundled $bundled, so 'SDK newer than the bundled runtime' has no scenario until one appears."
}

$missing = @($coverage.Keys | Where-Object { @($coverage[$_]).Count -eq 0 })
if ($missing.Count -gt 0) { throw "The scenario plan no longer covers: $($missing -join '; ')." }

$machines = @(
    @{ os = 'ubuntu-latest'; rid = 'linux-x64' },
    @{ os = 'windows-latest'; rid = 'win-x64' }
)
$include = foreach ($machine in $machines) {
    foreach ($scenario in $scenarios) {
        if ($scenario.scope -eq 'linux' -and $machine.rid -ne 'linux-x64') { continue }
        [ordered]@{ os = $machine.os; rid = $machine.rid; name = $scenario.name; spec = ($scenario | ConvertTo-Json -Compress -Depth 5) }
    }
}
$matrix = @{ include = @($include) } | ConvertTo-Json -Compress -Depth 6

Write-Host "Bundled major: $bundled; oldest supported SDK: $minimum; newest stable: $newestStable; newest of any quality: $newestAny"
Write-Host "SDK majors under test: $($majors -join ', ')"
Write-Host "Scenarios: $(($scenarios | ForEach-Object { $_.name }) -join ', ')"

if ($OutputFile) { "matrix=$matrix" | Add-Content -Path $OutputFile }
if ($env:GITHUB_STEP_SUMMARY) {
    "### Host runtime scenario plan" | Add-Content $env:GITHUB_STEP_SUMMARY
    "- Bundled runtime major (B): **$bundled**; oldest supported SDK: **$minimum**; newest stable: **$newestStable**; newest of any quality: **$newestAny**" | Add-Content $env:GITHUB_STEP_SUMMARY
    "- SDK majors under test: $($majors -join ', ')" | Add-Content $env:GITHUB_STEP_SUMMARY
    "- Scenarios: $(($scenarios | ForEach-Object { '``' + $_.name + '``' }) -join ', ')" | Add-Content $env:GITHUB_STEP_SUMMARY
    '- Coverage (each condition needs a scenario, or the plan fails):' | Add-Content $env:GITHUB_STEP_SUMMARY
    foreach ($condition in $coverage.Keys) {
        "  - ${condition}: $((@($coverage[$condition]) | ForEach-Object { '``' + $_.name + '``' }) -join ', ')" | Add-Content $env:GITHUB_STEP_SUMMARY
    }
}
