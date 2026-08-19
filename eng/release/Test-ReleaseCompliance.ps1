[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier, [string] $LayoutManifest)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
& (Join-Path $PSScriptRoot 'Test-ReleaseLicenseEvidence.ps1') | Out-Null
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
foreach ($relative in @('LICENSE', 'third-party/THIRD-PARTY-NOTICES.txt', 'third-party/sbom.spdx.json', 'third-party/dotnet-runtime/LICENSE.txt', 'third-party/dotnet-runtime/THIRD-PARTY-NOTICES.txt', 'third-party/dotnet-runtime/PROVENANCE.json', 'third-party/ripgrep/LICENSE-MIT', 'third-party/ripgrep/SOURCE.json')) {
    $file = Join-Path $stage $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or ((Get-Item $file).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Release compliance required file is missing or linked: $relative" }
}
$provenance = Get-Content -LiteralPath (Join-Path $stage 'third-party/dotnet-runtime/PROVENANCE.json') -Raw | ConvertFrom-Json
if ($provenance.runtimeIdentifier -ne $RuntimeIdentifier) { throw 'Runtime legal provenance has the wrong RID.' }
foreach ($record in $provenance.files) {
    $path = Join-Path $stage ([string]$record.path)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.sha256) { throw "Runtime legal digest mismatch: $($record.path)" }
}
$sbom = Get-Content -LiteralPath (Join-Path $stage 'third-party/sbom.spdx.json') -Raw | ConvertFrom-Json
if ($sbom.spdxVersion -ne 'SPDX-2.3' -or $sbom.name -ne "Threadsmith.NET-$RuntimeIdentifier" -or $sbom.packages.Count -eq 0) { throw 'The staged SPDX SBOM is malformed or has the wrong RID.' }
if ($LayoutManifest) {
    $layout = Get-Content -LiteralPath $LayoutManifest -Raw | ConvertFrom-Json
    if ($layout.runtimeIdentifier -ne $RuntimeIdentifier) { throw 'Staged layout has the wrong RID.' }
}
$complianceRecord = Join-Path $stage 'release-compliance.json'
$record = $null
if (Test-Path -LiteralPath $complianceRecord) {
    $record = Get-Content -LiteralPath $complianceRecord -Raw | ConvertFrom-Json
    if ($record.outcome -ne 'passed' -or $record.runtimeIdentifier -ne $RuntimeIdentifier) { throw 'The staged compliance record is invalid.' }
}
$stageDigest = Get-DirectoryDigest $stage -ExcludeRelativePaths @('release-compliance.json')
if ($null -ne $record -and ($record.stageSha256 -ne $stageDigest -or [string]::Join(',', @($record.stageDigestExcludes)) -ne 'release-compliance.json')) { throw 'The staged compliance digest does not describe the finalized staged payload.' }
[ordered]@{ schemaVersion = 1; outcome = 'passed'; runtimeIdentifier = $RuntimeIdentifier; componentCount = $sbom.packages.Count; stageSha256 = $stageDigest; stageDigestExcludes = @('release-compliance.json') }
