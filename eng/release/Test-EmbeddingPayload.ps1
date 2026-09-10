[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$root = Get-RepositoryRoot
$sourceManifest = Join-Path $root 'src/Threadsmith.Embeddings.Local/minilm-assets.json'
$manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
$assetDirectory = Join-Path $StageDirectory 'embeddings/all-MiniLM-L12-v2'
$expected = @($manifest.artifacts.name) + @('minilm-assets.json', 'LICENSE.txt')
$actual = @(Get-ChildItem -LiteralPath $assetDirectory -File | Select-Object -ExpandProperty Name)
if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw 'Embedding payload files do not match the closed manifest.' }
foreach ($asset in $manifest.artifacts) {
    $path = Join-Path $assetDirectory $asset.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw "Embedding payload asset $($asset.name) is missing or does not match the pinned artifact." }
}
foreach ($pair in @(@($sourceManifest, (Join-Path $assetDirectory 'minilm-assets.json')), @((Join-Path $root 'eng/release/legal/licenses/Apache-2.0.txt'), (Join-Path $assetDirectory 'LICENSE.txt')))) {
    if (-not (Test-Path -LiteralPath $pair[1] -PathType Leaf) -or (Get-FileHash -LiteralPath $pair[0]).Hash -ne (Get-FileHash -LiteralPath $pair[1]).Hash) { throw 'Embedding manifest or full Apache license is absent or mismatched.' }
}
$native = if ($RuntimeIdentifier.StartsWith('win-')) { 'onnxruntime.dll' } elseif ($RuntimeIdentifier.StartsWith('osx-')) { 'libonnxruntime.dylib' } else { 'libonnxruntime.so' }
foreach ($name in @('Threadsmith.Embeddings.Local.dll', 'Microsoft.ML.OnnxRuntime.dll', 'Microsoft.ML.Tokenizers.dll', $native)) {
    if (-not (Test-Path -LiteralPath (Join-Path $StageDirectory $name) -PathType Leaf)) { throw "Embedding release payload is missing $name for $RuntimeIdentifier." }
}

$nativeAsset = $manifest.nativeAssets.PSObject.Properties[$RuntimeIdentifier].Value
$nativePath = Join-Path $StageDirectory $nativeAsset.name
if ((Get-Item -LiteralPath $nativePath).Length -ne $nativeAsset.bytes -or (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nativeAsset.sha256) { throw "The ONNX native library is not the pinned $RuntimeIdentifier asset." }