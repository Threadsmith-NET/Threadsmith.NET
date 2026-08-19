[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$failures = [Collections.Generic.List[string]]::new()
function Test-Contract([string] $Name, [scriptblock] $Test) {
    try { & $Test; Write-Host "PASS $Name" } catch { $failures.Add("${Name}: $($_.Exception.Message)") }
}
Test-Contract 'supported target matrix' {
    foreach ($rid in @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) { Assert-ReleaseRid $rid }
    try { Assert-ReleaseRid 'linux-riscv64'; throw 'Unsupported RID was accepted.' } catch { if ($_.Exception.Message -eq 'Unsupported RID was accepted.') { throw } }
}
Test-Contract 'semantic versions' {
    Assert-ReleaseVersion '1.2.3'; Assert-ReleaseVersion '1.2.3-rc.1'
    try { Assert-ReleaseVersion 'v1.2.3'; throw 'Invalid version was accepted.' } catch { if ($_.Exception.Message -eq 'Invalid version was accepted.') { throw } }
}
Test-Contract 'workflow scripts are tracked' {
    $root = Get-RepositoryRoot
    foreach ($name in @('Publish-Release.ps1', 'Stage-Ripgrep.ps1', 'Stage-DotNetRuntimeLegal.ps1', 'New-ReleaseLegalArtifacts.ps1', 'Test-ReleaseLicenseEvidence.ps1', 'Test-ReleaseCompliance.ps1', 'New-ArtifactCompliance.ps1', 'Test-ArtifactPayload.ps1', 'Test-StagedPayload.ps1', 'Build-WindowsInstaller.ps1', 'Build-LinuxArchive.ps1', 'Build-MacPackage.ps1', 'New-ReleaseManifest.ps1', 'ripgrep-assets.json', 'release-license-evidence.json')) {
        $path = "eng/release/$name"
        if (-not (Test-Path (Join-Path $root $path))) { throw "Missing $path." }
        git -C $root ls-files --error-unmatch $path 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$path is not tracked by Git." }
    }
}
Test-Contract 'release-license evidence is closed, current, and fail-closed' {
    & (Join-Path $PSScriptRoot 'Test-ReleaseLicenseEvidence.ps1') | Out-Null
    $temp = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-evidence-contract-$([Guid]::NewGuid().ToString('N')).json"
    try {
        $changed = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-license-evidence.json') -Raw | ConvertFrom-Json
        $changed.windowsSelfContainedDecision.expiresOn = '2000-01-01'
        $changed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp -Encoding utf8NoBOM
        try { & (Join-Path $PSScriptRoot 'Test-ReleaseLicenseEvidence.ps1') -EvidencePath $temp; throw 'Expired evidence was accepted.' } catch { if ($_.Exception.Message -eq 'Expired evidence was accepted.') { throw } }
    } finally { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
}
Test-Contract 'legal artifacts are deterministic and cover the exact restore closure' {
    $root = Get-RepositoryRoot
    $assets = Join-Path $root 'src/Threadsmith.App/obj/project.assets.json'
    $runtimePackName = 'Microsoft.NETCore.App.Runtime.linux-x64/'
    $hasRuntimePack = (Test-Path $assets) -and ((Get-Content -LiteralPath $assets -Raw).Contains($runtimePackName, [StringComparison]::Ordinal))
    if (-not $hasRuntimePack) { dotnet restore (Join-Path $root 'src/Threadsmith.App/Threadsmith.App.csproj') --runtime linux-x64 | Out-Null }
    $temp = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-legal-contract-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory (Join-Path $temp 'a'), (Join-Path $temp 'b') -Force | Out-Null
    try {
        & (Join-Path $PSScriptRoot 'New-ReleaseLegalArtifacts.ps1') -AssetsFile $assets -OutputDirectory (Join-Path $temp 'a') -RuntimeIdentifier linux-x64
        & (Join-Path $PSScriptRoot 'New-ReleaseLegalArtifacts.ps1') -AssetsFile $assets -OutputDirectory (Join-Path $temp 'b') -RuntimeIdentifier linux-x64
        foreach ($name in @('THIRD-PARTY-NOTICES.txt', 'sbom.spdx.json')) {
            if ((Get-FileHash (Join-Path $temp "a/$name")).Hash -ne (Get-FileHash (Join-Path $temp "b/$name")).Hash) { throw "$name is not deterministic." }
        }
        $notices = Get-Content (Join-Path $temp 'a/THIRD-PARTY-NOTICES.txt') -Raw
        if (-not $notices.Contains('MPL source availability:', [StringComparison]::Ordinal) -or -not $notices.Contains('SQLite is in the public domain.', [StringComparison]::Ordinal)) { throw 'Critical MPL or SQLite notice treatment is missing.' }
    } finally { Remove-Item $temp -Recurse -Force }
}
Test-Contract 'runtime legal staging binds exact RID and rejects omissions' {
    $temp = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-runtime-legal-contract-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory (Join-Path $temp 'source'), (Join-Path $temp 'stage') -Force | Out-Null
    try {
        Set-Content (Join-Path $temp 'source/LICENSE.txt') 'runtime license' -NoNewline
        Set-Content (Join-Path $temp 'source/ThirdPartyNotices.txt') 'runtime notices' -NoNewline
        $assets = Join-Path (Get-RepositoryRoot) 'src/Threadsmith.App/obj/project.assets.json'
        & (Join-Path $PSScriptRoot 'Stage-DotNetRuntimeLegal.ps1') -RuntimeIdentifier linux-x64 -StageDirectory (Join-Path $temp 'stage') -AssetsFile $assets -RuntimeLegalDirectory (Join-Path $temp 'source')
        $provenance = Get-Content (Join-Path $temp 'stage/third-party/dotnet-runtime/PROVENANCE.json') -Raw | ConvertFrom-Json
        if ($provenance.runtimeIdentifier -ne 'linux-x64' -or $provenance.files.Count -ne 2) { throw 'Runtime provenance did not bind both files to the RID.' }
        Remove-Item (Join-Path $temp 'source/ThirdPartyNotices.txt')
        try { & (Join-Path $PSScriptRoot 'Stage-DotNetRuntimeLegal.ps1') -RuntimeIdentifier linux-x64 -StageDirectory (Join-Path $temp 'stage-2') -AssetsFile $assets -RuntimeLegalDirectory (Join-Path $temp 'source'); throw 'Missing runtime notices were accepted.' } catch { if ($_.Exception.Message -eq 'Missing runtime notices were accepted.') { throw } }
    } finally { Remove-Item $temp -Recurse -Force }
}
Test-Contract 'ripgrep assets are official, licensed, hashed, and complete' {
    $root = Get-RepositoryRoot
    $manifest = Get-Content -LiteralPath (Join-Path $root 'eng/release/ripgrep-assets.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'ripgrep' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Ripgrep manifest identity is invalid.' }
    if ($manifest.sourceRepository -ne 'https://github.com/BurntSushi/ripgrep') { throw 'Ripgrep source repository is not the official upstream repository.' }
    if ($manifest.licenseExpression -ne 'MIT OR Unlicense' -or $manifest.selectedLicense -ne 'MIT') { throw 'Ripgrep licensing metadata is not the approved permissive contract.' }
    if ($manifest.licenseFiles.'LICENSE-MIT' -notmatch '^[0-9a-f]{64}$' -or $manifest.licenseFiles.UNLICENSE -notmatch '^[0-9a-f]{64}$') { throw 'Ripgrep license-file digests are not pinned.' }
    $expected = @('linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64', 'win-arm64', 'win-x64')
    $actual = @($manifest.assets.PSObject.Properties.Name | Sort-Object)
    if ([string]::Join(',', $actual) -ne [string]::Join(',', $expected)) { throw 'Ripgrep assets do not cover exactly the supported RID matrix.' }
    foreach ($rid in $expected) {
        $asset = $manifest.assets.PSObject.Properties[$rid].Value
        if ($asset.sha256 -notmatch '^[0-9a-f]{64}$') { throw "Ripgrep asset $rid does not have a pinned SHA-256 digest." }
        if ($asset.archive -notmatch "^ripgrep-$([regex]::Escape($manifest.version))-") { throw "Ripgrep asset $rid does not pin the approved version." }
        if ($asset.executable -ne $(if ($rid.StartsWith('win-')) { 'rg.exe' } else { 'rg' })) { throw "Ripgrep asset $rid has the wrong executable name." }
    }
}
Test-Contract 'ripgrep staging rejects archive digest mismatch before extraction' {
    $root = Get-RepositoryRoot
    $temp = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-ripgrep-contract-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $temp | Out-Null
    try {
        $archive = Join-Path $temp 'changed.zip'
        [IO.File]::WriteAllText($archive, 'not the pinned archive')
        try {
            & (Join-Path $root 'eng/release/Stage-Ripgrep.ps1') -RuntimeIdentifier win-x64 -StageDirectory (Join-Path $temp 'stage') -WorkingDirectory (Join-Path $temp 'work') -ArchivePath $archive
            throw 'Changed ripgrep archive was accepted.'
        } catch {
            if ($_.Exception.Message -eq 'Changed ripgrep archive was accepted.' -or $_.Exception.Message -notmatch 'did not match') { throw }
        }
    } finally { Remove-Item $temp -Recurse -Force }
}
Test-Contract 'release PowerShell scripts parse' {
    $root = Get-RepositoryRoot
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $root 'eng/release') -Filter '*.ps1' -File) {
        $tokens = $null
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count -gt 0) { throw "$($script.Name) has PowerShell parse errors: $($errors[0].Message)" }
    }
}
Test-Contract 'aggregate manifest rejects partial sets and records the complete matrix' {
    $temp = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-release-contract-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $temp | Out-Null
    try {
        $names = @('win-x64-setup.exe', 'win-arm64-setup.exe', 'linux-x64.tar.gz', 'linux-arm64.tar.gz', 'osx-x64.pkg', 'osx-arm64.pkg')
        foreach ($name in $names) {
            $artifact = Join-Path $temp "Threadsmith-1.2.3-$name"
            Set-Content $artifact "payload-$name" -NoNewline
            $rid = $name -replace '-setup\.exe$|\.tar\.gz$|\.pkg$', ''
            [ordered]@{ schemaVersion = 1; outcome = 'passed'; runtimeIdentifier = $rid; artifact = [IO.Path]::GetFileName($artifact); artifactSha256 = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant(); stagedPayloadSha256 = ('a' * 64); componentCount = 1 } | ConvertTo-Json | Set-Content "$artifact.compliance.json" -Encoding utf8NoBOM
        }
        & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -Version 1.2.3 -ReleaseTag v1.2.3 -SourceCommit 0123456789abcdef -ArtifactDirectory $temp
        $manifest = Get-Content (Join-Path $temp 'release-manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.artifacts.Count -ne 6) { throw 'Manifest did not record all six artifacts.' }
        Remove-Item (Join-Path $temp 'Threadsmith-1.2.3-linux-arm64.tar.gz')
        try { & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -Version 1.2.3 -ReleaseTag v1.2.3 -SourceCommit 0123456789abcdef -ArtifactDirectory $temp; throw 'Partial set was accepted.' } catch { if ($_.Exception.Message -eq 'Partial set was accepted.') { throw } }
    } finally { Remove-Item $temp -Recurse -Force }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host 'All release contracts passed.'
