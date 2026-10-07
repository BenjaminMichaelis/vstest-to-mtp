param(
    [string]$FixturesPath = (Join-Path $PSScriptRoot '../../fixtures'),
    [string]$Configuration = 'Release',
    [int]$ExpectedFixtureCount = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Every fixture root is a self-contained mini repository identified by its own global.json.
# Running from inside each root makes that global.json (VSTest mode) and its Directory.*.props apply.
$fixtureRoots = @(
    Get-ChildItem -Path $FixturesPath -Recurse -File -Filter global.json |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.Directory.FullName } |
        Sort-Object
)

if ($fixtureRoots.Count -ne $ExpectedFixtureCount) {
    throw "Expected $ExpectedFixtureCount fixture roots under '$FixturesPath' but found $($fixtureRoots.Count)."
}

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

foreach ($root in $fixtureRoots) {
    Write-Host "::group::Fixture $root"
    Push-Location $root
    try {
        Invoke-DotNet @('restore', 'Fixture.slnx')
        Invoke-DotNet @('build', 'Fixture.slnx', '--no-restore', '--configuration', $Configuration, '-p:ContinuousIntegrationBuild=True')
        Invoke-DotNet @('test', 'Fixture.slnx', '--no-build', '--configuration', $Configuration)
    }
    finally {
        Pop-Location
        Write-Host '::endgroup::'
    }
}

Write-Host "Built and tested $($fixtureRoots.Count) fixtures."
