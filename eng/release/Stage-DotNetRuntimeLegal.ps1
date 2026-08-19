[CmdletBinding()]
param([Parameter(Mandatory)][string] $RuntimeIdentifier, [Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $AssetsFile, [string] $RuntimeLegalDirectory)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$evidence = & (Join-Path $PSScriptRoot 'Test-ReleaseLicenseEvidence.ps1')
$runtimeVersion = [string]$evidence.windowsSelfContainedDecision.runtimeVersion
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$runtimePackId = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
$runtimePackLibrary = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -eq "$runtimePackId/$runtimeVersion" -and $_.Value.type -eq 'package' })
if ($runtimePackLibrary.Count -ne 1) { throw "Restore assets do not contain the reviewed runtime pack $runtimePackId/$runtimeVersion." }
if ([string]::IsNullOrWhiteSpace($RuntimeLegalDirectory)) {
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    $runtimePackLocations = @($packageFolders | ForEach-Object { Join-Path $_ "$($runtimePackId.ToLowerInvariant())/$runtimeVersion" } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    if ($runtimePackLocations.Count -ne 1) { throw "The exact restored runtime-pack directory for $runtimePackId/$runtimeVersion was not found uniquely." }
    $RuntimeLegalDirectory = (Resolve-Path -LiteralPath $runtimePackLocations[0]).Path
} else { $RuntimeLegalDirectory = (Resolve-Path -LiteralPath $RuntimeLegalDirectory).Path }
$destination = Join-Path $StageDirectory 'third-party/dotnet-runtime'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$records = @()
foreach ($entry in @(@('LICENSE.txt', 'LICENSE.txt'), @('ThirdPartyNotices.txt', 'THIRD-PARTY-NOTICES.txt'))) {
    $sourceName = $entry[0]
    $name = $entry[1]
    $sources = @(Get-ChildItem -LiteralPath $RuntimeLegalDirectory -File | Where-Object { $_.Name.Equals($sourceName, [StringComparison]::OrdinalIgnoreCase) })
    if ($sources.Count -ne 1 -or ($sources[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Required .NET runtime legal file is missing, ambiguous, or linked: $name" }
    $source = $sources[0].FullName
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name)
    $records += [ordered]@{ path = "third-party/dotnet-runtime/$name"; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$provenance = [ordered]@{ schemaVersion = 1; runtimeIdentifier = $RuntimeIdentifier; runtimeVersion = $runtimeVersion; files = $records }
[IO.File]::WriteAllText((Join-Path $destination 'PROVENANCE.json'), (($provenance | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
