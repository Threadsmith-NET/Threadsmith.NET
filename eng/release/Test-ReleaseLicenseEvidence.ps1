[CmdletBinding()]
param([string] $EvidencePath = (Join-Path $PSScriptRoot 'release-license-evidence.json'), [datetime] $AsOfUtc = [DateTime]::UtcNow)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ($evidence.schemaVersion -ne 1 -or $evidence.policyVersion -ne '1.0') { throw 'Unsupported release-license evidence schema or policy version.' }
if ([string]::IsNullOrWhiteSpace($evidence.reviewOwner) -or $evidence.windowsSelfContainedDecision.owner -ne $evidence.reviewOwner) { throw 'Release evidence has no matching designated owner.' }
if ($evidence.windowsSelfContainedDecision.status -ne 'approved') { throw 'The Windows self-contained distribution decision is not approved.' }
if ([DateTime]::Parse($evidence.windowsSelfContainedDecision.expiresOn).ToUniversalTime() -lt $AsOfUtc.ToUniversalTime()) { throw 'The Windows self-contained distribution decision is expired.' }
$expectedWindowsRids = @('win-arm64', 'win-x64')
if ([string]::Join(',', @($evidence.windowsSelfContainedDecision.runtimeIdentifiers | Sort-Object)) -ne [string]::Join(',', $expectedWindowsRids)) { throw 'The Windows decision does not cover the exact supported Windows RID set.' }
$approved = @($evidence.approvedLicenseExpressions)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($component in $evidence.components) {
    if ([string]::IsNullOrWhiteSpace($component.id) -or [string]::IsNullOrWhiteSpace($component.version) -or $component.reviewState -ne 'approved') { throw 'Release evidence contains an unknown or unapproved component.' }
    if (-not $seen.Add("$($component.id)/$($component.version)")) { throw "Release evidence contains duplicate component $($component.id)/$($component.version)." }
    if ($component.licenseExpression -notin $approved) { throw "Component $($component.id) has an unapproved license expression." }
    if ($component.PSObject.Properties.Name -contains 'supplementalLicenseExpressions') {
        foreach ($expression in $component.supplementalLicenseExpressions) {
            if ($expression -notin $approved) { throw "Component $($component.id) has an unrecorded supplemental license expression." }
        }
    }
    if ($component.scope -eq 'bundled-package') {
        if ($component.packageSha512 -notmatch '^[A-Za-z0-9+/]{80,}={0,2}$') { throw "Bundled package $($component.id) has no closed package digest." }
        if ($component.PSObject.Properties.Name -contains 'additionalNotices') {
            foreach ($relativeNotice in $component.additionalNotices) {
                $noticePath = Join-Path $PSScriptRoot ([string]$relativeNotice)
                if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or (Get-Item -LiteralPath $noticePath).Length -eq 0) { throw "Bundled package $($component.id) has missing supplemental notices." }
            }
        }
        $licensePath = Join-Path $PSScriptRoot ([string]$component.licenseText)
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or (Get-Item $licensePath).Length -eq 0) { throw "Bundled package $($component.id) has no reviewed full license text." }
    }
}
foreach ($critical in @('TUIKit', 'PrettyPrompt', 'SQLitePCLRaw.lib.e_sqlite3', 'dotnet-runtime', 'ripgrep')) {
    if (-not @($evidence.components | Where-Object id -EQ $critical)) { throw "Required legal evidence is missing for $critical." }
}
Write-Output $evidence
