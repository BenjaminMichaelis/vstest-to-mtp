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

$index = (Invoke-RestMethod -Uri $ReleasesIndexUrl -MaximumRetryCount 3).'releases-index'
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
$scenarios = [System.Collections.Generic.List[object]]::new()
function Add-Scenario([string]$Name, [object[]]$Installs, $PinMajor) {
    $scenarios.Add([ordered]@{ name = $Name; installs = $Installs; pinMajor = $PinMajor; minimumSdk = $minimum })
}

foreach ($m in $majors) {
    Add-Scenario "sdk$m" @(@{ major = $m }) $m
}

# Older repository on a machine that also has the bundled major, and the bundled major's repository next to the newest release.
if ($bundled - 1 -ge $minimum) { Add-Scenario "sdk$($bundled - 1)-and-$bundled" @(@{ major = $bundled - 1 }, @{ major = $bundled }) ($bundled - 1) }
if ($newestAny -gt $bundled) { Add-Scenario "sdk$bundled-and-$newestAny" @(@{ major = $bundled }, @{ major = $newestAny }) $bundled }

Add-Scenario "runtime$bundled-no-sdk" @(@{ major = $bundled; runtimeOnly = $true }) $null
Add-Scenario 'no-dotnet' @() $null

$machines = @(
    @{ os = 'ubuntu-latest'; rid = 'linux-x64' },
    @{ os = 'windows-latest'; rid = 'win-x64' }
)
$include = foreach ($machine in $machines) {
    foreach ($scenario in $scenarios) {
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
}
