[CmdletBinding()]
param([Parameter(Mandatory)][string] $AssetsFile, [Parameter(Mandatory)][string] $OutputDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$evidence = & (Join-Path $PSScriptRoot 'Test-ReleaseLicenseEvidence.ps1')
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$resolved = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object {
    $separator = $_.Name.LastIndexOf('/')
    [pscustomobject]@{ id = $_.Name.Substring(0, $separator); version = $_.Name.Substring($separator + 1); sha512 = $_.Value.sha512 }
} | Sort-Object id, version)
$runtimePackPattern = '^Microsoft\.(?:NETCore|AspNetCore)\.App\.(?:Runtime|Host)\.[A-Za-z0-9.-]+$'
$resolvedRuntimePacks = @(Get-RestoredRuntimePacks $assets)
if (-not @($resolvedRuntimePacks | Where-Object { $_.id -eq "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier" })) { throw "Restore closure does not contain the requested $RuntimeIdentifier runtime pack." }
$resolved = @($resolved | Where-Object { $_.id -notmatch $runtimePackPattern })
$runtimeEvidence = @($evidence.components | Where-Object scope -EQ 'runtime-pack')
if ($resolvedRuntimePacks.Count -eq 0 -or $runtimeEvidence.Count -ne 1) { throw 'The exact restore closure must contain runtime packs covered by one reviewed runtime-pack entry.' }
foreach ($runtimePack in $resolvedRuntimePacks) {
    if ($runtimePack.version -ne $runtimeEvidence[0].version) { throw "Resolved runtime pack $($runtimePack.id)/$($runtimePack.version) does not match reviewed runtime evidence." }
}
$approvedPackages = @($evidence.components | Where-Object scope -EQ 'bundled-package')
foreach ($package in $resolved) {
    $match = @($approvedPackages | Where-Object { $_.id -eq $package.id -and $_.version -eq $package.version })
    if ($match.Count -ne 1 -or $match[0].packageSha512 -ne $package.sha512) { throw "Resolved package $($package.id)/$($package.version) does not match reviewed evidence." }
}
if (@($approvedPackages | Where-Object { $_.id -notin $resolved.id }).Count -gt 0) { throw 'Reviewed bundled-package evidence is stale relative to the exact release closure.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$notice = [Text.StringBuilder]::new()
[void]$notice.AppendLine('THREADSMITH.NET THIRD-PARTY NOTICES').AppendLine('Generated deterministically from reviewed release evidence.').AppendLine()
foreach ($package in $resolved) {
    $entry = @($approvedPackages | Where-Object { $_.id -eq $package.id -and $_.version -eq $package.version })[0]
    [void]$notice.AppendLine("================================================================================").AppendLine("$($entry.id) $($entry.version)").AppendLine("License: $($entry.licenseExpression)").AppendLine("Copyright: $($entry.copyrightText)").AppendLine("Source: $($entry.provenance)")
    if ($entry.id -eq 'PrettyPrompt') { [void]$notice.AppendLine("MPL source availability: $($entry.sourceAvailability)") }
    [void]$notice.AppendLine().AppendLine((Get-Content -LiteralPath (Join-Path $PSScriptRoot ([string]$entry.licenseText)) -Raw).Trim()).AppendLine()
    if ($entry.PSObject.Properties.Name -contains 'additionalNotices') {
        foreach ($relativeNotice in $entry.additionalNotices) {
            [void]$notice.AppendLine((Get-Content -LiteralPath (Join-Path $PSScriptRoot ([string]$relativeNotice)) -Raw).Trim()).AppendLine()
        }
    }
    if ($entry.id.StartsWith('SQLitePCLRaw.', [StringComparison]::Ordinal)) { [void]$notice.AppendLine((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legal/SQLitePCLRaw-NOTICE.txt') -Raw).Trim()).AppendLine() }
}
$model = Get-Content -LiteralPath (Join-Path (Get-RepositoryRoot) 'src/Threadsmith.Embeddings.Local/minilm-assets.json') -Raw | ConvertFrom-Json
[void]$notice.AppendLine('================================================================================').AppendLine("$($model.model) $($model.revision)").AppendLine('License: Apache-2.0').AppendLine("Source: $($model.source)/tree/$($model.revision)").AppendLine('Model license declaration: bundled README.md at the pinned revision.').AppendLine((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legal/licenses/Apache-2.0.txt') -Raw).Trim()).AppendLine()
$reranker = Get-Content -LiteralPath (Join-Path (Get-RepositoryRoot) 'src/Threadsmith.Reranking.Local/crossencoder-assets.json') -Raw | ConvertFrom-Json
[void]$notice.AppendLine('================================================================================').AppendLine("$($reranker.model) $($reranker.revision)").AppendLine('License: Apache-2.0').AppendLine("Source: $($reranker.source)/tree/$($reranker.revision)").AppendLine("Model license declaration: $($reranker.licenseEvidence).").AppendLine((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legal/licenses/Apache-2.0.txt') -Raw).Trim()).AppendLine()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt'), $notice.ToString().Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
$packages = @($resolved | ForEach-Object {
    $resolvedPackage = $_
    $entry = @($approvedPackages | Where-Object { $_.id -eq $resolvedPackage.id -and $_.version -eq $resolvedPackage.version })[0]
    [ordered]@{ SPDXID = "SPDXRef-Package-$([Uri]::EscapeDataString($entry.id))"; name = $entry.id; versionInfo = $entry.version; downloadLocation = $entry.provenance; licenseDeclared = $entry.licenseExpression; licenseConcluded = $(if ($entry.PSObject.Properties.Name -contains 'licenseConcluded') { $entry.licenseConcluded } else { $entry.licenseExpression }); checksums = @([ordered]@{ algorithm = 'SHA512'; checksumValue = [Convert]::ToHexString([Convert]::FromBase64String([string]$entry.packageSha512)).ToLowerInvariant() }) }
})
$modelArtifact = @($model.artifacts | Where-Object name -EQ 'model.onnx')[0]
$packages += [ordered]@{ SPDXID = 'SPDXRef-Model-MiniLM-L12-v2'; name = $model.model; versionInfo = $model.revision; downloadLocation = "$($model.source)/resolve/$($model.revision)/onnx/model.onnx"; licenseDeclared = 'Apache-2.0'; licenseConcluded = 'Apache-2.0'; checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $modelArtifact.sha256 }) }
$rerankerArtifact = @($reranker.artifacts | Where-Object name -EQ 'model.onnx')[0]
$packages += [ordered]@{ SPDXID = 'SPDXRef-Model-MS-Marco-MiniLM-L6-v2'; name = $reranker.model; versionInfo = $reranker.revision; downloadLocation = "$($reranker.source)/resolve/$($reranker.revision)/$($rerankerArtifact.path)"; licenseDeclared = 'Apache-2.0'; licenseConcluded = 'Apache-2.0'; checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $rerankerArtifact.sha256 }) }
$sbom = [ordered]@{ spdxVersion = 'SPDX-2.3'; dataLicense = 'CC0-1.0'; SPDXID = 'SPDXRef-DOCUMENT'; name = "Threadsmith.NET-$RuntimeIdentifier"; documentNamespace = "https://threadsmith.net/sbom/$RuntimeIdentifier/$((Get-FileHash $AssetsFile -Algorithm SHA256).Hash.ToLowerInvariant())"; creationInfo = [ordered]@{ created = '1970-01-01T00:00:00Z'; creators = @('Tool: Threadsmith.NET release engineering') }; packages = $packages }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'sbom.spdx.json'), (($sbom | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
