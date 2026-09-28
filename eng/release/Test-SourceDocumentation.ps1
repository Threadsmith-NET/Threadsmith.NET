[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$root = Get-RepositoryRoot
$project = Join-Path $root 'src/Threadsmith.App/Threadsmith.App.csproj'
# Evaluate the actual packaging items without restoring, compiling, or duplicating the file catalog.
$evaluated = & dotnet msbuild $project -nologo -getItem:None
if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate packaged documentation items.' }
$items = ($evaluated -join [Environment]::NewLine | ConvertFrom-Json).Items.None
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $temporaryRoot "threadsmith-docs-preflight-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    foreach ($item in $items) {
        if (-not $item.PSObject.Properties['Link']) { continue }
        $link = $item.Link.Replace('\', '/')
        if (-not $link.StartsWith('ThreadsmithDocs/', [StringComparison]::Ordinal)) { continue }
        if ($item.CopyToPublishDirectory -notin @('Always', 'PreserveNewest', 'IfDifferent')) { continue }
        $destination = [IO.Path]::GetFullPath((Join-Path $stage $link))
        $stagePrefix = $stage + [IO.Path]::DirectorySeparatorChar
        if (-not $destination.StartsWith($stagePrefix, [StringComparison]::Ordinal)) {
            throw "Documentation item escapes its staging directory: $link"
        }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $item.FullPath -Destination $destination
    }
    & (Join-Path $PSScriptRoot 'Test-PackagedDocumentation.ps1') -StageDirectory $stage
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -or
        [IO.Path]::GetFileName($resolved) -notlike 'threadsmith-docs-preflight-*') {
        throw 'Refusing cleanup outside the documentation preflight temporary directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
