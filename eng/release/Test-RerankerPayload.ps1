[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$root = Get-RepositoryRoot
$sourceManifest = Join-Path $root 'src/Threadsmith.Reranking.Local/crossencoder-assets.json'
$manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.model -ne 'cross-encoder/ms-marco-MiniLM-L6-v2' -or $manifest.source -ne 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2' -or $manifest.revision -notmatch '^[0-9a-f]{40}$' -or $manifest.license -ne 'Apache-2.0') {
    throw 'Reranker manifest identity or licensing is invalid.'
}

$assetDirectory = Join-Path $StageDirectory 'crossencoders/ms-marco-MiniLM-L6-v2'
$expected = @($manifest.artifacts.name) + @('crossencoder-assets.json', 'LICENSE.txt')
$actual = @(Get-ChildItem -LiteralPath $assetDirectory -File | Select-Object -ExpandProperty Name)
if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw 'Reranker payload files do not match the closed manifest.' }
foreach ($asset in $manifest.artifacts) {
    $path = Join-Path $assetDirectory $asset.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
        throw "Reranker payload asset $($asset.name) is missing or does not match the pinned artifact."
    }
}

foreach ($pair in @(@($sourceManifest, (Join-Path $assetDirectory 'crossencoder-assets.json')), @((Join-Path $root 'eng/release/legal/licenses/Apache-2.0.txt'), (Join-Path $assetDirectory 'LICENSE.txt')))) {
    if (-not (Test-Path -LiteralPath $pair[1] -PathType Leaf) -or (Get-FileHash -LiteralPath $pair[0]).Hash -ne (Get-FileHash -LiteralPath $pair[1]).Hash) {
        throw 'Reranker manifest or full Apache license is absent or mismatched.'
    }
}

$native = if ($RuntimeIdentifier.StartsWith('win-')) { 'onnxruntime.dll' } elseif ($RuntimeIdentifier.StartsWith('osx-')) { 'libonnxruntime.dylib' } else { 'libonnxruntime.so' }
foreach ($name in @('Threadsmith.Reranking.Local.dll', 'Microsoft.ML.OnnxRuntime.dll', 'Microsoft.ML.Tokenizers.dll', $native)) {
    if (-not (Test-Path -LiteralPath (Join-Path $StageDirectory $name) -PathType Leaf)) { throw "Reranker release payload is missing $name for $RuntimeIdentifier." }
}