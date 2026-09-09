[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ripgrep-assets.json') -Raw | ConvertFrom-Json
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-ripgrep-licenses-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
function Assert-Rejected([scriptblock] $Action) {
    try { & $Action; throw 'Invalid ripgrep license fixture was accepted.' }
    catch { if ($_.Exception.Message -eq 'Invalid ripgrep license fixture was accepted.') { throw } }
}
try {
    foreach ($rid in @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
        $asset = $manifest.assets.PSObject.Properties[$rid].Value
        $stage = Join-Path $fixtureRoot $rid
        $notices = Join-Path $stage 'third-party/ripgrep'
        New-Item -ItemType Directory -Path $notices -Force | Out-Null
        foreach ($name in @('LICENSE-MIT', 'UNLICENSE')) {
            # Fixture generation only: reconstruct independently verified archive line endings.
            # Production validators always hash the exact bytes, without normalization.
            $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "legal/ripgrep/$name")).Replace("`r`n", "`n")
            if ($rid.StartsWith('win-')) { $text = $text.Replace("`n", "`r`n") }
            [IO.File]::WriteAllText((Join-Path $notices $name), $text, [Text.UTF8Encoding]::new($false))
        }
        $source = [ordered]@{
            schemaVersion = 1; runtimeIdentifier = $rid; product = $manifest.product; version = $manifest.version
            sourceRepository = $manifest.sourceRepository; licenseExpression = $manifest.licenseExpression; selectedLicense = $manifest.selectedLicense
            archive = $asset.archive; archiveSha256 = $asset.sha256; licenseFiles = $asset.licenseFiles
        }
        $sourcePath = Join-Path $notices 'SOURCE.json'
        $validSource = $source | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($sourcePath, $validSource)
        & (Join-Path $PSScriptRoot 'Test-RipgrepPayload.ps1') -StageDirectory $stage -RuntimeIdentifier $rid

        $mitPath = Join-Path $notices 'LICENSE-MIT'
        $validMit = [IO.File]::ReadAllBytes($mitPath)
        [IO.File]::AppendAllText($mitPath, 'tampered')
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-RipgrepPayload.ps1') -StageDirectory $stage -RuntimeIdentifier $rid }
        # A coherently changed file/SOURCE digest still cannot override the repository pin.
        $source.licenseFiles = [ordered]@{ 'LICENSE-MIT' = (Get-FileHash -LiteralPath $mitPath -Algorithm SHA256).Hash.ToLowerInvariant(); UNLICENSE = $asset.licenseFiles.UNLICENSE }
        [IO.File]::WriteAllText($sourcePath, ($source | ConvertTo-Json -Depth 5))
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-RipgrepPayload.ps1') -StageDirectory $stage -RuntimeIdentifier $rid }
        [IO.File]::WriteAllBytes($mitPath, $validMit)
        [IO.File]::WriteAllText($sourcePath, $validSource)
        $otherRid = if ($rid -eq 'win-x64') { 'linux-x64' } else { 'win-x64' }
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-RipgrepPayload.ps1') -StageDirectory $stage -RuntimeIdentifier $otherRid }
        Remove-Item -LiteralPath (Join-Path $notices 'UNLICENSE')
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-RipgrepPayload.ps1') -StageDirectory $stage -RuntimeIdentifier $rid }
    }
    Write-Output 'PASS six-RID exact ripgrep licenses, tamper/source override rejection, wrong-RID rejection, and missing-license rejection.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $expectedParent -or [IO.Path]::GetFileName($resolved) -notmatch '^threadsmith-ripgrep-licenses-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the owned ripgrep fixture root.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
