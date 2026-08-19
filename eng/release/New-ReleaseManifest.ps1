[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $ReleaseTag,
    [Parameter(Mandatory)][string] $SourceCommit,
    [Parameter(Mandatory)][string] $ArtifactDirectory
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseVersion $Version
if ($ReleaseTag -ne "v$Version") { throw "Release tag '$ReleaseTag' does not match version '$Version'." }
if ($SourceCommit -notmatch '^[0-9a-fA-F]{7,64}$') { throw 'SourceCommit must be a Git object id.' }
$directory = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$expected = @(
    "Threadsmith-$Version-win-x64-setup.exe", "Threadsmith-$Version-win-arm64-setup.exe",
    "Threadsmith-$Version-linux-x64.tar.gz", "Threadsmith-$Version-linux-arm64.tar.gz",
    "Threadsmith-$Version-osx-x64.pkg", "Threadsmith-$Version-osx-arm64.pkg")
$primary = @(Get-ChildItem $directory -File | Where-Object { $_.Name -notlike '*.sha256' -and $_.Name -notlike '*.compliance.json' -and $_.Name -notin @('release-manifest.json', 'SHA256SUMS') })
$actual = @($primary.Name | Sort-Object)
$wanted = @($expected | Sort-Object)
if (Compare-Object $wanted $actual) { throw "Release artifact set is incomplete or unexpected. Expected: $($wanted -join ', '); actual: $($actual -join ', ')." }
$records = @()
foreach ($name in $expected) {
    $file = Get-Item -LiteralPath (Join-Path $directory $name)
    $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = "$($file.FullName).sha256"
    if (Test-Path $sidecar) {
        $declared = ((Get-Content $sidecar -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
        if ($declared -ne $digest) { throw "Checksum sidecar does not match $name." }
    }
    $compliancePath = "$($file.FullName).compliance.json"
    if (-not (Test-Path -LiteralPath $compliancePath -PathType Leaf)) { throw "Artifact compliance result is missing for $name." }
    $compliance = Get-Content -LiteralPath $compliancePath -Raw | ConvertFrom-Json
    $rid = $name.Substring("Threadsmith-$Version-".Length) -replace '-setup\.exe$|\.tar\.gz$|\.pkg$', ''
    if ($compliance.outcome -ne 'passed' -or $compliance.runtimeIdentifier -ne $rid -or $compliance.artifact -ne $name -or $compliance.artifactSha256 -ne $digest) { throw "Artifact compliance result does not bind to $name." }
    $records += [ordered]@{ name = $name; size = $file.Length; sha256 = $digest; signingState = 'not-recorded'; compliance = $compliance }
}
$timestamp = [DateTimeOffset]::UtcNow.ToString('O')
[ordered]@{ schemaVersion = 1; product = 'Threadsmith.NET'; version = $Version; releaseTag = $ReleaseTag; sourceCommit = $SourceCommit.ToLowerInvariant(); generatedAtUtc = $timestamp; dotnetSdk = (& dotnet --version); artifacts = $records } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $directory 'release-manifest.json') -Encoding utf8NoBOM
$records | ForEach-Object { "$($_.sha256)  $($_.name)" } | Set-Content (Join-Path $directory 'SHA256SUMS') -Encoding ascii
