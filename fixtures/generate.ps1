<#
.SYNOPSIS
    Maintainer-only: reproduces the committed VSTest migration fixtures from the .NET SDK templates.

.DESCRIPTION
    The fixtures under this directory are generated ONCE and committed. Tests and CI never run this
    script; they only consume the committed output. Re-run it only to deliberately refresh the
    baselines (new SDK / template / package versions), then review the diff and update README.md.

    Each fixture root (fixtures/<framework>/<plain|cpm>) is a self-contained mini repository:
    its own global.json (VSTest, i.e. no Microsoft.Testing.Platform runner), Directory.Build.props,
    Directory.Packages.props and .editorconfig, so nothing is inherited from the repository root.

.PARAMETER XunitV3TemplatesVersion
    Exact version of the xunit.v3.templates NuGet template package to install and use.
#>
param(
    [string]$XunitV3TemplatesVersion = '4.0.1',
    [string]$Framework = 'net10.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$fixturesRoot = $PSScriptRoot
$sdkVersion = (dotnet --version).Trim()
Write-Host "SDK $sdkVersion; xunit.v3.templates $XunitV3TemplatesVersion"

dotnet new install "xunit.v3.templates@$XunitV3TemplatesVersion" --force | Out-Null

$frameworks = @(
    @{ Name = 'mstest'; Template = @('mstest', '--test-runner', 'VSTest'); Cleanup = @('Test1.cs') },
    @{ Name = 'nunit'; Template = @('nunit'); Cleanup = @('UnitTest1.cs') },
    @{ Name = 'xunit-v3'; Template = @('xunit3', '--test-runner', 'vstest'); Cleanup = @('UnitTest1.cs') },
    @{ Name = 'xunit-v2'; Template = @('xunit'); Cleanup = @('UnitTest1.cs') }
)

$arithmetic = @'
namespace Calculator;

public static class Arithmetic
{
    public static int Add(int left, int right) => left + right;

    public static int Subtract(int left, int right) => left - right;

    public static int Multiply(int left, int right) => left * right;

    public static int Divide(int dividend, int divisor) => dividend / divisor;
}

'@

# Deterministic tests: 3 single tests + 1 data-driven test with 3 rows = 6 test cases per fixture.
$testSources = @{
    'mstest' = @'
namespace Calculator.Tests;

[TestClass]
public sealed class ArithmeticTests
{
    [TestMethod]
    public void Add_ReturnsSum() => Assert.AreEqual(5, Arithmetic.Add(2, 3));

    [TestMethod]
    public void Subtract_ReturnsDifference() => Assert.AreEqual(-1, Arithmetic.Subtract(2, 3));

    [TestMethod]
    public void Divide_ByZero_Throws() =>
        Assert.ThrowsExactly<DivideByZeroException>(() => Arithmetic.Divide(1, 0));

    [TestMethod]
    [DataRow(2, 3, 6)]
    [DataRow(-2, 3, -6)]
    [DataRow(0, 5, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected) =>
        Assert.AreEqual(expected, Arithmetic.Multiply(left, right));
}

'@
    'nunit'  = @'
namespace Calculator.Tests;

[TestFixture]
public class ArithmeticTests
{
    [Test]
    public void Add_ReturnsSum() => Assert.That(Arithmetic.Add(2, 3), Is.EqualTo(5));

    [Test]
    public void Subtract_ReturnsDifference() => Assert.That(Arithmetic.Subtract(2, 3), Is.EqualTo(-1));

    [Test]
    public void Divide_ByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Arithmetic.Divide(1, 0));

    [TestCase(2, 3, 6)]
    [TestCase(-2, 3, -6)]
    [TestCase(0, 5, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected) =>
        Assert.That(Arithmetic.Multiply(left, right), Is.EqualTo(expected));
}

'@
    'xunit'  = @'
namespace Calculator.Tests;

public class ArithmeticTests
{
    [Fact]
    public void Add_ReturnsSum() => Assert.Equal(5, Arithmetic.Add(2, 3));

    [Fact]
    public void Subtract_ReturnsDifference() => Assert.Equal(-1, Arithmetic.Subtract(2, 3));

    [Fact]
    public void Divide_ByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Arithmetic.Divide(1, 0));

    [Theory]
    [InlineData(2, 3, 6)]
    [InlineData(-2, 3, -6)]
    [InlineData(0, 5, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected) =>
        Assert.Equal(expected, Arithmetic.Multiply(left, right));
}

'@
}

function Write-LfFile([string]$Path, [string]$Content) {
    $full = Join-Path (Get-Location) $Path
    [System.IO.File]::WriteAllText($full, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

foreach ($fw in $frameworks) {
    foreach ($variant in 'plain', 'cpm') {
        $root = Join-Path $fixturesRoot (Join-Path $fw.Name $variant)
        if (Test-Path $root) { Remove-Item $root -Recurse -Force }
        New-Item -ItemType Directory -Path $root | Out-Null
        Push-Location $root
        try {
            # Isolation files first, so `dotnet new` resolves SDK/context from this fixture, not the repo root.
            Write-LfFile 'global.json' (@{ sdk = @{ version = $sdkVersion; rollForward = 'latestMinor' } } | ConvertTo-Json)
            Write-LfFile '.editorconfig' "root = true`n"
            Write-LfFile 'Directory.Build.props' "<!-- Intentionally empty: stops MSBuild inheriting the repository root's Directory.Build.props. -->`n<Project />`n"

            dotnet new sln --format slnx --name Fixture | Out-Null
            dotnet new classlib --name Calculator --output src/Calculator --framework $Framework --no-restore | Out-Null
            Remove-Item src/Calculator/Class1.cs
            Write-LfFile 'src/Calculator/Arithmetic.cs' $arithmetic

            $testsDir = 'tests/Calculator.Tests'
            dotnet new @($fw.Template) --name Calculator.Tests --output $testsDir --framework $Framework --no-restore | Out-Null
            foreach ($file in $fw.Cleanup) { Remove-Item (Join-Path $testsDir $file) }
            $sourceKey = $fw.Name -replace '-v\d$', ''
            Write-LfFile (Join-Path $testsDir 'ArithmeticTests.cs') $testSources[$sourceKey]

            dotnet add "$testsDir/Calculator.Tests.csproj" reference src/Calculator/Calculator.csproj | Out-Null
            dotnet sln Fixture.slnx add src/Calculator/Calculator.csproj "$testsDir/Calculator.Tests.csproj" | Out-Null

            if ($variant -eq 'cpm') {
                # Move every PackageReference Version into Directory.Packages.props (Central Package Management).
                $csproj = Join-Path $testsDir 'Calculator.Tests.csproj'
                $text = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $csproj))
                $versions = [ordered]@{}
                $text = [regex]::Replace($text, '<PackageReference Include="([^"]+)" Version="([^"]+)"', {
                        param($m)
                        $versions[$m.Groups[1].Value] = $m.Groups[2].Value
                        "<PackageReference Include=`"$($m.Groups[1].Value)`""
                    })
                [System.IO.File]::WriteAllText((Join-Path (Get-Location) $csproj), $text)
                $items = ($versions.GetEnumerator() | ForEach-Object { "    <PackageVersion Include=`"$($_.Key)`" Version=`"$($_.Value)`" />" }) -join "`n"
                Write-LfFile 'Directory.Packages.props' "<Project>`n  <PropertyGroup>`n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`n  </PropertyGroup>`n  <ItemGroup>`n$items`n  </ItemGroup>`n</Project>`n"
            }
            else {
                Write-LfFile 'Directory.Packages.props' "<!-- Central Package Management explicitly off: stops inheriting the repository root's Directory.Packages.props. -->`n<Project>`n  <PropertyGroup>`n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`n  </PropertyGroup>`n</Project>`n"
            }

            Write-Host "== $($fw.Name)/$variant"
            dotnet test Fixture.slnx --nologo
        }
        finally {
            Pop-Location
        }
    }
}
