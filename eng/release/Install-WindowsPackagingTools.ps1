[CmdletBinding()]
param([string] $InstallDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'threadsmith-windows-packaging-tools'))
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
if (-not $IsWindows) { throw 'Windows packaging tools can only be installed on Windows.' }

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'windows-packaging-tools.json') -Raw | ConvertFrom-Json
$invalidManifest = @(
    $manifest.schemaVersion -ne 1
    $manifest.innoSetup.version -notmatch '^\d+\.\d+\.\d+$'
    $manifest.innoExtract.version -notmatch '^\d+$'
    $manifest.innoExtract.sha256 -notmatch '^[0-9a-f]{64}$'
) -contains $true
if ($invalidManifest) {
    throw 'The Windows packaging tool manifest is invalid.'
}

$chocolatey = (Get-Command choco.exe -ErrorAction Stop).Source
& $chocolatey upgrade $manifest.innoSetup.chocolateyPackage --version $manifest.innoSetup.version --allow-downgrade --yes --no-progress
if ($LASTEXITCODE -notin @(0, 1641, 3010)) { throw "Chocolatey could not install Inno Setup $($manifest.innoSetup.version)." }

$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'The pinned Inno Setup compiler was not found after installation.' }
$installedVersion = (Get-Item -LiteralPath $iscc).VersionInfo.ProductVersion
if (-not $installedVersion.StartsWith($manifest.innoSetup.version, [StringComparison]::Ordinal)) {
    throw "Expected Inno Setup $($manifest.innoSetup.version), but found $installedVersion."
}

$tools = Initialize-CleanDirectory $InstallDirectory
$archive = Join-Path $tools $manifest.innoExtract.archive
Invoke-WebRequest -Uri $manifest.innoExtract.uri -OutFile $archive
$actualDigest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualDigest -ne $manifest.innoExtract.sha256) { throw 'The innoextract archive did not match its pinned SHA-256 digest.' }
$extractDirectory = Join-Path $tools 'innoextract'
Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory
$innoExtract = Join-Path $extractDirectory $manifest.innoExtract.executable
if (-not (Test-Path -LiteralPath $innoExtract -PathType Leaf)) { throw 'The pinned innoextract archive did not contain the expected executable.' }

if ($env:GITHUB_ENV) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "THREADSMITH_ISCC_PATH=$iscc"
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "THREADSMITH_INNOEXTRACT_PATH=$innoExtract"
}
if ($env:GITHUB_PATH) { Add-Content -LiteralPath $env:GITHUB_PATH -Value $extractDirectory }
Write-Host "Using Inno Setup $installedVersion and checksum-verified innoextract $($manifest.innoExtract.version)."
