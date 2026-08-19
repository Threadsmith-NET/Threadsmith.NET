[CmdletBinding()]
param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $RuntimeIdentifier, [string] $OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/release'))
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseVersion $Version; Assert-ReleaseRid $RuntimeIdentifier
if (-not $IsMacOS -or -not $RuntimeIdentifier.StartsWith('osx-')) { throw 'Build-MacPackage requires macOS and an osx RID.' }
$output = Initialize-CleanDirectory $OutputRoot
$publish = Join-Path $output 'payload-build'
& (Join-Path $PSScriptRoot 'Publish-Release.ps1') -Version $Version -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $publish | Out-Null
$packageRoot = Join-Path $output 'package-root/usr/local/lib/threadsmith'
New-Item -ItemType Directory $packageRoot -Force | Out-Null
Copy-Item (Join-Path $publish 'stage/*') $packageRoot -Recurse
Copy-Item (Join-Path $PSScriptRoot 'macos/uninstall.sh') $packageRoot
chmod +x (Join-Path $packageRoot 'Threadsmith.App') (Join-Path $packageRoot 'Threadsmith.Scripting.Worker') (Join-Path $packageRoot 'tools/rg') (Join-Path $packageRoot 'uninstall.sh')
$scripts = Join-Path $output 'scripts'; New-Item -ItemType Directory $scripts | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'macos/postinstall') $scripts; chmod +x (Join-Path $scripts 'postinstall')
$artifacts = New-Item -ItemType Directory (Join-Path $output 'artifacts')
$artifact = Join-Path $artifacts "Threadsmith-$Version-$RuntimeIdentifier.pkg"
pkgbuild --root (Join-Path $output 'package-root') --scripts $scripts --identifier net.threadsmith.cli --version $Version --install-location / $artifact
if ($LASTEXITCODE -ne 0) { throw 'pkgbuild failed.' }
pkgutil --check-signature $artifact | Out-Null
Get-FileHash $artifact -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($artifact))" } | Set-Content "$artifact.sha256" -Encoding ascii
& (Join-Path $PSScriptRoot 'New-ArtifactCompliance.ps1') -ArtifactPath $artifact -StageDirectory (Join-Path $publish 'stage') -RuntimeIdentifier $RuntimeIdentifier
