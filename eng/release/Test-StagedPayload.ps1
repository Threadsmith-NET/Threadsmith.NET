[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$suffix = if ($RuntimeIdentifier.StartsWith('win-')) { '.exe' } else { '' }
$ripgrepRelativePath = "tools/rg$suffix"
foreach ($name in @("Threadsmith.App$suffix", "Threadsmith.Scripting.Worker$suffix", $ripgrepRelativePath, 'third-party/ripgrep/LICENSE-MIT', 'third-party/ripgrep/UNLICENSE', 'third-party/ripgrep/SOURCE.json', 'third-party/THIRD-PARTY-NOTICES.txt', 'third-party/sbom.spdx.json', 'third-party/dotnet-runtime/LICENSE.txt', 'third-party/dotnet-runtime/THIRD-PARTY-NOTICES.txt', 'third-party/dotnet-runtime/PROVENANCE.json', 'release-compliance.json', 'LICENSE', 'config.example', 'providers.example.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $name) -PathType Leaf)) { throw "Staged payload is missing $name." }
}
& (Join-Path $PSScriptRoot 'Test-ReleaseCompliance.ps1') -StageDirectory $stage -RuntimeIdentifier $RuntimeIdentifier | Out-Null
$ripgrepSource = Get-Content -LiteralPath (Join-Path $stage 'third-party/ripgrep/SOURCE.json') -Raw | ConvertFrom-Json
if ($ripgrepSource.product -ne 'ripgrep' -or $ripgrepSource.version -notmatch '^\d+\.\d+\.\d+$' -or $ripgrepSource.selectedLicense -ne 'MIT') {
    throw 'Staged ripgrep provenance or licensing metadata is invalid.'
}
$stagedMitHash = (Get-FileHash -LiteralPath (Join-Path $stage 'third-party/ripgrep/LICENSE-MIT') -Algorithm SHA256).Hash.ToLowerInvariant()
$stagedUnlicenseHash = (Get-FileHash -LiteralPath (Join-Path $stage 'third-party/ripgrep/UNLICENSE') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($stagedMitHash -ne $ripgrepSource.licenseFiles.'LICENSE-MIT' -or $stagedUnlicenseHash -ne $ripgrepSource.licenseFiles.UNLICENSE) {
    throw 'Staged ripgrep license-file digests do not match their provenance metadata.'
}
$hostRid = if ($IsWindows) { 'win-' } elseif ($IsMacOS) { 'osx-' } else { 'linux-' }
$hostArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant().Replace('x64', 'x64').Replace('arm64', 'arm64')
if ($RuntimeIdentifier -eq "$hostRid$hostArch") {
    & (Join-Path $stage "Threadsmith.App$suffix") --version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Native staged application smoke check failed.' }
    $ripgrepVersion = @(& (Join-Path $stage $ripgrepRelativePath) --version)
    if ($LASTEXITCODE -ne 0 -or $ripgrepVersion.Count -eq 0 -or
        -not $ripgrepVersion[0].StartsWith("ripgrep $($ripgrepSource.version) ", [StringComparison]::Ordinal)) {
        throw 'Native staged ripgrep smoke check failed.'
    }
}
