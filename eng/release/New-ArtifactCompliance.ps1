[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArtifactPath, [Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$artifact = Get-Item -LiteralPath $ArtifactPath
$stageResult = Get-Content -LiteralPath (Join-Path $StageDirectory 'release-compliance.json') -Raw | ConvertFrom-Json
if ($stageResult.outcome -ne 'passed' -or $stageResult.runtimeIdentifier -ne $RuntimeIdentifier) { throw 'A passing same-RID staged compliance result is required.' }
$expectedStageDigest = Get-DirectoryDigest $StageDirectory -ExcludeRelativePaths @('release-compliance.json')
if ($stageResult.stageSha256 -ne $expectedStageDigest -or [string]::Join(',', @($stageResult.stageDigestExcludes)) -ne 'release-compliance.json') { throw 'The staged compliance digest does not describe the finalized staged payload.' }
& (Join-Path $PSScriptRoot 'Test-ArtifactPayload.ps1') -ArtifactPath $artifact.FullName -StageDirectory $StageDirectory -RuntimeIdentifier $RuntimeIdentifier
$record = [ordered]@{ schemaVersion = 1; outcome = 'passed'; runtimeIdentifier = $RuntimeIdentifier; artifact = $artifact.Name; artifactSha256 = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); stagedPayloadSha256 = $stageResult.stageSha256; componentCount = $stageResult.componentCount }
[IO.File]::WriteAllText("$($artifact.FullName).compliance.json", (($record | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
