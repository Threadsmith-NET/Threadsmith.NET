[CmdletBinding()]
param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $RuntimeIdentifier, [string] $OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/release'))
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseVersion $Version; Assert-ReleaseRid $RuntimeIdentifier
if (-not $RuntimeIdentifier.StartsWith('linux-')) { throw 'Build-LinuxArchive requires a linux RID.' }
$output = Initialize-CleanDirectory $OutputRoot
$publish = Join-Path $output 'payload-build'
& (Join-Path $PSScriptRoot 'Publish-Release.ps1') -Version $Version -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $publish | Out-Null
$bundle = Join-Path $output 'bundle'
New-Item -ItemType Directory $bundle | Out-Null
Copy-Item (Join-Path $publish 'stage/*') $bundle -Recurse
Copy-Item (Join-Path $PSScriptRoot 'linux/install.sh'), (Join-Path $PSScriptRoot 'linux/uninstall.sh') $bundle
chmod +x (Join-Path $bundle 'Threadsmith.App') (Join-Path $bundle 'Threadsmith.Scripting.Worker') (Join-Path $bundle 'tools/rg') (Join-Path $bundle 'install.sh') (Join-Path $bundle 'uninstall.sh')
$artifacts = New-Item -ItemType Directory (Join-Path $output 'artifacts')
$artifact = Join-Path $artifacts "Threadsmith-$Version-$RuntimeIdentifier.tar.gz"
tar -czf $artifact -C $bundle .
if ($LASTEXITCODE -ne 0) { throw 'tar packaging failed.' }
Get-FileHash $artifact -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($artifact))" } | Set-Content "$artifact.sha256" -Encoding ascii
& (Join-Path $PSScriptRoot 'New-ArtifactCompliance.ps1') -ArtifactPath $artifact -StageDirectory (Join-Path $publish 'stage') -RuntimeIdentifier $RuntimeIdentifier
