[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $StageDirectory,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $WorkingDirectory
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseVersion $Version
$root = Get-RepositoryRoot
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path

# Bundled extensions are framework-dependent on purpose: the host loads each one into a collectible
# AssemblyLoadContext over its own self-contained runtime. Each extension keeps its private
# dependencies in its own folder and must never ship the shared contract assembly.
$bundled = @(
    [pscustomobject]@{ Name = 'Neo4j'; Project = 'src/Extensions/Neo4j/Threadsmith.Extensions.Neo4j.csproj'; Assembly = 'Threadsmith.Extensions.Neo4j.dll' }
)

foreach ($extension in $bundled) {
    $publish = Join-Path $WorkingDirectory $extension.Name
    dotnet publish (Join-Path $root $extension.Project) -c Release -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Extension publish failed: $($extension.Name)" }
    foreach ($name in @($extension.Assembly, 'threadsmith.extension.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $name) -PathType Leaf)) {
            throw "Published extension $($extension.Name) is missing $name."
        }
    }
    if (Test-Path -LiteralPath (Join-Path $publish 'Threadsmith.Extensions.Abstractions.dll') -PathType Leaf) {
        throw "Published extension $($extension.Name) ships the host contract assembly; the host owns the single copy."
    }
    $destination = Join-Path (Join-Path $stage 'extensions') $extension.Name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item (Join-Path $publish '*') $destination -Recurse -Force
}

Write-Output (Join-Path $stage 'extensions')
