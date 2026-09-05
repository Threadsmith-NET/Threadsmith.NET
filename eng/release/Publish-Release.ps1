[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $RuntimeIdentifier,
    [string] $OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/release')
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseVersion $Version
Assert-ReleaseRid $RuntimeIdentifier
$root = Get-RepositoryRoot
$output = Initialize-CleanDirectory $OutputRoot
$publishRoot = Join-Path $output 'publish'
$stage = Join-Path $output 'stage'
$appPublish = Join-Path $publishRoot 'app'
$workerPublish = Join-Path $publishRoot 'worker'
$review = & (Join-Path $PSScriptRoot 'Test-ReleaseLicenseEvidence.ps1')
$runtimeVersion = [string]$review.windowsSelfContainedDecision.runtimeVersion

dotnet restore (Join-Path $root 'src/Threadsmith.App/Threadsmith.App.csproj') --runtime $RuntimeIdentifier "-p:RuntimeFrameworkVersion=$runtimeVersion"
if ($LASTEXITCODE -ne 0) { throw 'Application restore failed.' }
dotnet restore (Join-Path $root 'src/Threadsmith.Scripting.Worker/Threadsmith.Scripting.Worker.csproj') --runtime $RuntimeIdentifier "-p:RuntimeFrameworkVersion=$runtimeVersion"
if ($LASTEXITCODE -ne 0) { throw 'Worker restore failed.' }
dotnet publish (Join-Path $root 'src/Threadsmith.App/Threadsmith.App.csproj') -c Release -r $RuntimeIdentifier --self-contained true --no-restore -p:Version=$Version "-p:RuntimeFrameworkVersion=$runtimeVersion" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false -o $appPublish
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
Assert-ReleasePromptPayload -PayloadDirectory $appPublish -RuntimeIdentifier $RuntimeIdentifier -SourceRoot $root
dotnet publish (Join-Path $root 'src/Threadsmith.Scripting.Worker/Threadsmith.Scripting.Worker.csproj') -c Release -r $RuntimeIdentifier --self-contained true --no-restore -p:Version=$Version "-p:RuntimeFrameworkVersion=$runtimeVersion" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false -o $workerPublish
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }

New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $appPublish '*') $stage -Recurse -Force
Copy-Item (Join-Path $workerPublish '*') $stage -Recurse -Force
Copy-Item (Join-Path $root 'LICENSE') $stage
& (Join-Path $PSScriptRoot 'Stage-Ripgrep.ps1') -RuntimeIdentifier $RuntimeIdentifier -StageDirectory $stage -WorkingDirectory (Join-Path $output 'ripgrep') | Out-Null
& (Join-Path $PSScriptRoot 'New-ReleaseLegalArtifacts.ps1') -AssetsFile (Join-Path $root 'src/Threadsmith.App/obj/project.assets.json') -OutputDirectory (Join-Path $stage 'third-party') -RuntimeIdentifier $RuntimeIdentifier
& (Join-Path $PSScriptRoot 'Stage-DotNetRuntimeLegal.ps1') -RuntimeIdentifier $RuntimeIdentifier -StageDirectory $stage -AssetsFile (Join-Path $root 'src/Threadsmith.App/obj/project.assets.json')
$compliance = & (Join-Path $PSScriptRoot 'Test-ReleaseCompliance.ps1') -StageDirectory $stage -RuntimeIdentifier $RuntimeIdentifier
[IO.File]::WriteAllText((Join-Path $stage 'release-compliance.json'), (($compliance | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
$appHost = Join-Path $stage $(if ($RuntimeIdentifier.StartsWith('win-')) { 'Threadsmith.App.exe' } else { 'Threadsmith.App' })
$workerHost = Join-Path $stage $(if ($RuntimeIdentifier.StartsWith('win-')) { 'Threadsmith.Scripting.Worker.exe' } else { 'Threadsmith.Scripting.Worker' })
$ripgrepHost = Join-Path $stage $(if ($RuntimeIdentifier.StartsWith('win-')) { 'tools/rg.exe' } else { 'tools/rg' })
foreach ($required in @($appHost, $workerHost, $ripgrepHost, (Join-Path $stage 'config.example'), (Join-Path $stage 'providers.example.json'), (Join-Path $stage 'LICENSE'), (Join-Path $stage 'third-party/ripgrep/LICENSE-MIT'), (Join-Path $stage 'third-party/ripgrep/UNLICENSE'), (Join-Path $stage 'third-party/ripgrep/SOURCE.json'), (Join-Path $stage 'third-party/THIRD-PARTY-NOTICES.txt'), (Join-Path $stage 'third-party/sbom.spdx.json'), (Join-Path $stage 'third-party/dotnet-runtime/LICENSE.txt'), (Join-Path $stage 'third-party/dotnet-runtime/THIRD-PARTY-NOTICES.txt'), (Join-Path $stage 'release-compliance.json'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required staged payload file is missing: $required" }
}
$files = @(Get-ChildItem $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
    $component = if ($relative -eq 'tools/rg' -or $relative -eq 'tools/rg.exe' -or $relative.StartsWith('third-party/ripgrep/', [StringComparison]::Ordinal)) { 'ripgrep' } else { 'payload' }
    Get-FileDigestRecord $_ $stage $component
})
[ordered]@{ schemaVersion = 1; product = 'Threadsmith.NET'; version = $Version; runtimeIdentifier = $RuntimeIdentifier; selfContained = $true; files = $files } |
    ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'staged-layout.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'Test-StagedPayload.ps1') -StageDirectory $stage -RuntimeIdentifier $RuntimeIdentifier
Write-Output $stage
