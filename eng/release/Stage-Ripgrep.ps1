[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RuntimeIdentifier,
    [Parameter(Mandatory)][string] $StageDirectory,
    [Parameter(Mandatory)][string] $WorkingDirectory,
    [string] $ArchivePath
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier

$manifestPath = Join-Path $PSScriptRoot 'ripgrep-assets.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'ripgrep') {
    throw 'The pinned ripgrep asset manifest is invalid.'
}
if ($manifest.sourceRepository -ne 'https://github.com/BurntSushi/ripgrep' -or
    $manifest.licenseExpression -ne 'MIT OR Unlicense' -or
    $manifest.selectedLicense -ne 'MIT') {
    throw 'The pinned ripgrep source or licensing contract is invalid.'
}

$assetProperty = $manifest.assets.PSObject.Properties[$RuntimeIdentifier]
if ($null -eq $assetProperty) { throw "No pinned ripgrep asset exists for $RuntimeIdentifier." }
$asset = $assetProperty.Value
$archiveName = [string]$asset.archive
$expectedHash = ([string]$asset.sha256).ToLowerInvariant()
if ($archiveName -notmatch "^ripgrep-$([regex]::Escape($manifest.version))-[0-9A-Za-z_-]+\.(zip|tar\.gz)$" -or
    $expectedHash -notmatch '^[0-9a-f]{64}$') {
    throw "The pinned ripgrep asset metadata for $RuntimeIdentifier is invalid."
}

$downloadUri = [Uri]"https://github.com/BurntSushi/ripgrep/releases/download/$($manifest.version)/$archiveName"
if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -ne 'github.com') {
    throw 'The ripgrep download URI is not an approved HTTPS GitHub URI.'
}

New-Item -ItemType Directory -Path $StageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
$resolvedArchivePath = if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    Join-Path $WorkingDirectory $archiveName
} else {
    (Resolve-Path -LiteralPath $ArchivePath).Path
}
$extractDirectory = Join-Path $WorkingDirectory 'extracted'
if (Test-Path -LiteralPath $extractDirectory) { Remove-Item -LiteralPath $extractDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $extractDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    Add-Type -AssemblyName System.Net.Http
    $httpClient = [Net.Http.HttpClient]::new()
    try {
        $httpClient.Timeout = [TimeSpan]::FromMinutes(2)
        $httpClient.MaxResponseContentBufferSize = 16 * 1024 * 1024
        $httpClient.DefaultRequestHeaders.UserAgent.ParseAdd('Threadsmith.NET-release-packaging')
        $archiveBytes = $httpClient.GetByteArrayAsync($downloadUri).GetAwaiter().GetResult()
        [IO.File]::WriteAllBytes($resolvedArchivePath, $archiveBytes)
    } finally {
        $httpClient.Dispose()
    }
}

$actualHash = (Get-FileHash -LiteralPath $resolvedArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "The ripgrep archive checksum for $RuntimeIdentifier did not match the repository-pinned SHA-256 digest."
}

if ($archiveName.EndsWith('.zip', [StringComparison]::Ordinal)) {
    Expand-Archive -LiteralPath $resolvedArchivePath -DestinationPath $extractDirectory
} else {
    tar -xzf $resolvedArchivePath -C $extractDirectory
    if ($LASTEXITCODE -ne 0) { throw "The ripgrep archive for $RuntimeIdentifier could not be extracted." }
}

$assetRoot = Join-Path $extractDirectory ([string]$asset.rootDirectory)
$sourceExecutable = Join-Path $assetRoot ([string]$asset.executable)
$sourceMitLicense = Join-Path $assetRoot 'LICENSE-MIT'
$sourceUnlicense = Join-Path $assetRoot 'UNLICENSE'
foreach ($required in @($sourceExecutable, $sourceMitLicense, $sourceUnlicense)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "The verified ripgrep archive is missing required entry: $required"
    }
    if (((Get-Item -LiteralPath $required).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The verified ripgrep archive contains an unsupported linked entry: $required"
    }
}
$actualMitHash = (Get-FileHash -LiteralPath $sourceMitLicense -Algorithm SHA256).Hash.ToLowerInvariant()
$actualUnlicenseHash = (Get-FileHash -LiteralPath $sourceUnlicense -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualMitHash -ne $manifest.licenseFiles.'LICENSE-MIT' -or
    $actualUnlicenseHash -ne $manifest.licenseFiles.UNLICENSE) {
    throw 'The ripgrep license files do not match the repository-reviewed digests.'
}

$toolsDirectory = Join-Path $StageDirectory 'tools'
$noticeDirectory = Join-Path $StageDirectory 'third-party/ripgrep'
New-Item -ItemType Directory -Path $toolsDirectory, $noticeDirectory -Force | Out-Null
$stagedExecutable = Join-Path $toolsDirectory ([string]$asset.executable)
Copy-Item -LiteralPath $sourceExecutable -Destination $stagedExecutable
Copy-Item -LiteralPath $sourceMitLicense -Destination (Join-Path $noticeDirectory 'LICENSE-MIT')
Copy-Item -LiteralPath $sourceUnlicense -Destination (Join-Path $noticeDirectory 'UNLICENSE')
[ordered]@{
    product = 'ripgrep'
    version = [string]$manifest.version
    sourceRepository = [string]$manifest.sourceRepository
    licenseExpression = [string]$manifest.licenseExpression
    selectedLicense = [string]$manifest.selectedLicense
    licenseFiles = [ordered]@{
        'LICENSE-MIT' = $actualMitHash
        UNLICENSE = $actualUnlicenseHash
    }
    archive = $archiveName
    archiveSha256 = $expectedHash
} | ConvertTo-Json | ForEach-Object {
    [IO.File]::WriteAllText(
        (Join-Path $noticeDirectory 'SOURCE.json'),
        $_,
        [Text.UTF8Encoding]::new($false))
}

if (-not $RuntimeIdentifier.StartsWith('win-')) {
    chmod +x $stagedExecutable
    if ($LASTEXITCODE -ne 0) { throw "The staged ripgrep executable for $RuntimeIdentifier could not be marked executable." }
}

Write-Output $stagedExecutable
