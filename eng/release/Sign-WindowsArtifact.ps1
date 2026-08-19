[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArtifactPath, [Parameter(Mandatory)][string] $CertificateThumbprint, [Parameter(Mandatory)][string] $TimestampUrl)
if (-not $IsWindows) { throw 'Windows signing requires Windows.' }
$artifact = (Resolve-Path -LiteralPath $ArtifactPath).Path
& signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $artifact
if ($LASTEXITCODE -ne 0) { throw 'Artifact signing failed.' }
& signtool verify /pa /all $artifact
if ($LASTEXITCODE -ne 0) { throw 'Artifact signature verification failed.' }
