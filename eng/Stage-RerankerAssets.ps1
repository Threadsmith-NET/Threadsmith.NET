[CmdletBinding()]
param([string] $StageDirectory = (Join-Path $PSScriptRoot '../artifacts/reranker-assets'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $root 'src/Threadsmith.Reranking.Local/crossencoder-assets.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.model -ne 'cross-encoder/ms-marco-MiniLM-L6-v2' -or
    $manifest.source -ne 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2' -or
    $manifest.revision -notmatch '^[0-9a-f]{40}$' -or $manifest.license -ne 'Apache-2.0') {
    throw 'Reranker asset manifest is not a pinned official Apache-2.0 source.'
}

$stage = [IO.Path]::GetFullPath($StageDirectory)
$destination = Join-Path $stage 'crossencoders/ms-marco-MiniLM-L6-v2'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($asset in $manifest.artifacts) {
    if ($asset.name -notmatch '^[A-Za-z0-9_.-]+$' -or $asset.path -notmatch '^(onnx/)?[A-Za-z0-9_.-]+$' -or $asset.sha256 -notmatch '^[0-9a-f]{64}$' -or $asset.bytes -le 0) {
        throw 'Invalid reranker asset manifest entry.'
    }

    $target = Join-Path $destination $asset.name
    if (Test-Path -LiteralPath $target) {
        $info = Get-Item -LiteralPath $target
        if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reranker asset target must not be a link.' }
        if ($info.Length -eq $asset.bytes -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $asset.sha256) { continue }
        throw "Existing reranker asset $($asset.name) is invalid. Remove that exact file and run staging again."
    }

    $sourcePath = $asset.path
    $temporary = Join-Path $destination "$($asset.name).$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Invoke-WebRequest "$($manifest.source)/resolve/$($manifest.revision)/$sourcePath" -OutFile $temporary
        if ((Get-Item -LiteralPath $temporary).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw "Official reranker asset $($asset.name) did not match the pinned digest and size."
        }
        Move-Item -LiteralPath $temporary -Destination $target
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destination 'crossencoder-assets.json') -Force
Copy-Item -LiteralPath (Join-Path $root 'eng/release/legal/licenses/Apache-2.0.txt') -Destination (Join-Path $destination 'LICENSE.txt') -Force
Write-Output $stage