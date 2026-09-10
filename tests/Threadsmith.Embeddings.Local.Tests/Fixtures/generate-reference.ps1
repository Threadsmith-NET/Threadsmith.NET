[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ReferenceEnginePath,
    [Parameter(Mandatory)][string] $AssetsPath,
    [string] $OutputPath = (Join-Path $PSScriptRoot 'production-reference-parity.json')
)
$ErrorActionPreference = 'Stop'
$enginePath = (Resolve-Path -LiteralPath $ReferenceEnginePath).Path
$assetsRoot = (Resolve-Path -LiteralPath $AssetsPath).Path
$destination = [System.IO.Path]::GetFullPath($OutputPath)
$expectedSource = '97e23f75fbb593c8247c7ac2c1391c0678d7fcd48b92c1ffd4b4619e42ca304a'
if ((Get-FileHash -LiteralPath $enginePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSource) {
    throw 'Reference engine differs from the independently verified source. Review provenance before changing goldens.'
}

# Own a uniquely named temporary directory; no product source is copied or built.
$temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryParent ('threadsmith-reference-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $escapedEngine = [System.Security.SecurityElement]::Escape($enginePath)
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="$escapedEngine" Link="MpnetEmbedderEngine.cs" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.23.0" />
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0-preview.1.25127.4" />
  </ItemGroup>
</Project>
"@
    $projectPath = Join-Path $temporaryRoot 'ReferenceFixture.csproj'
    [System.IO.File]::WriteAllText($projectPath, $project)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReferenceFixtureGenerator.cs.template') -Destination (Join-Path $temporaryRoot 'Program.cs')
    $emptySource = Join-Path $temporaryRoot 'empty-package-source'
    [System.IO.Directory]::CreateDirectory($emptySource) | Out-Null
    # Dependencies must already be cached. This explicit utility never downloads assets or packages.
    & dotnet restore $projectPath --source $emptySource --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Reference restore failed; install the documented exact packages before offline regeneration.' }
    & dotnet run --project $projectPath --no-restore -- $enginePath $assetsRoot (Join-Path $PSScriptRoot 'production-reference-texts.json') $destination
    if ($LASTEXITCODE -ne 0) { throw 'Reference fixture generation failed.' }
}
finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    if ([System.IO.Path]::GetDirectoryName($resolvedRoot) -ne $temporaryParent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) -or
        [System.IO.Path]::GetFileName($resolvedRoot) -notmatch '^threadsmith-reference-[0-9a-f]{32}$') {
        throw 'Refusing cleanup outside the owned reference fixture directory.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
