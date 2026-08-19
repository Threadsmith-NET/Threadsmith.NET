[CmdletBinding()]
param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $RuntimeIdentifier, [string] $OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/release'))
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseVersion $Version; Assert-ReleaseRid $RuntimeIdentifier
if (-not $IsWindows -or -not $RuntimeIdentifier.StartsWith('win-')) { throw 'Build-WindowsInstaller requires Windows and a win RID.' }
$output = Initialize-CleanDirectory $OutputRoot
$publish = Join-Path $output 'payload-build'
& (Join-Path $PSScriptRoot 'Publish-Release.ps1') -Version $Version -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $publish | Out-Null
$artifacts = New-Item -ItemType Directory (Join-Path $output 'artifacts')
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source }
& $iscc (Join-Path $PSScriptRoot 'windows/Threadsmith.iss') "/DSourceDir=$(Join-Path $publish 'stage')" "/DOutputDir=$($artifacts.FullName)" "/DAppVersion=$Version" "/DTargetRid=$RuntimeIdentifier"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup packaging failed.' }
$artifact = Join-Path $artifacts "Threadsmith-$Version-$RuntimeIdentifier-setup.exe"
if (-not (Test-Path $artifact)) { throw 'Inno Setup did not produce the expected artifact.' }
Get-FileHash $artifact -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($artifact))" } | Set-Content "$artifact.sha256" -Encoding ascii
& (Join-Path $PSScriptRoot 'New-ArtifactCompliance.ps1') -ArtifactPath $artifact -StageDirectory (Join-Path $publish 'stage') -RuntimeIdentifier $RuntimeIdentifier
