[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ripgrep-assets.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.product -ne 'ripgrep' -or
    $manifest.sourceRepository -ne 'https://github.com/BurntSushi/ripgrep' -or
    $manifest.licenseExpression -ne 'MIT OR Unlicense' -or $manifest.selectedLicense -ne 'MIT') {
    throw 'The pinned ripgrep source or licensing contract is invalid.'
}
$asset = $manifest.assets.PSObject.Properties[$RuntimeIdentifier].Value
$noticeDirectory = Join-Path $StageDirectory 'third-party/ripgrep'
foreach ($name in @('SOURCE.json', 'LICENSE-MIT', 'UNLICENSE')) {
    $path = Join-Path $noticeDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The staged ripgrep legal payload is missing or linked: $name"
    }
}
$source = Get-Content -LiteralPath (Join-Path $noticeDirectory 'SOURCE.json') -Raw | ConvertFrom-Json
if ($source.schemaVersion -ne 1 -or $source.runtimeIdentifier -ne $RuntimeIdentifier -or
    $source.product -ne $manifest.product -or $source.version -ne $manifest.version -or
    $source.sourceRepository -ne $manifest.sourceRepository -or
    $source.licenseExpression -ne $manifest.licenseExpression -or $source.selectedLicense -ne $manifest.selectedLicense -or
    $source.archive -ne $asset.archive -or $source.archiveSha256 -ne $asset.sha256) {
    throw 'Staged ripgrep provenance does not match the repository-pinned runtime asset.'
}
foreach ($name in @('LICENSE-MIT', 'UNLICENSE')) {
    $expected = [string]$asset.licenseFiles.PSObject.Properties[$name].Value
    $recorded = [string]$source.licenseFiles.PSObject.Properties[$name].Value
    $actual = (Get-FileHash -LiteralPath (Join-Path $noticeDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$' -or $recorded -ne $expected -or $actual -ne $expected) {
        throw "The ripgrep $name license does not match the repository-pinned $RuntimeIdentifier bytes."
    }
}
