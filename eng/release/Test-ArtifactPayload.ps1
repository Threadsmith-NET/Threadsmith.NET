[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArtifactPath, [Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
$artifact = (Resolve-Path -LiteralPath $ArtifactPath).Path
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) "threadsmith-artifact-inspection-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    if ($artifact.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) {
        tar -xzf $artifact -C $temporary
        if ($LASTEXITCODE -ne 0) { throw 'The release archive could not be extracted for compliance inspection.' }
        $payload = $temporary
    } elseif ($artifact.EndsWith('.pkg', [StringComparison]::OrdinalIgnoreCase)) {
        $expanded = Join-Path $temporary 'expanded'
        pkgutil --expand-full $artifact $expanded
        if ($LASTEXITCODE -ne 0) { throw 'The macOS package could not be expanded for compliance inspection.' }
        $hosts = @(Get-ChildItem -LiteralPath $expanded -Filter 'Threadsmith.App' -File -Recurse)
        if ($hosts.Count -ne 1) { throw 'The macOS package does not contain one unambiguous Threadsmith payload.' }
        $payload = $hosts[0].Directory.FullName
    } elseif ($artifact.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
        $sevenZip = (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source
        if (-not $sevenZip) { $sevenZip = (Get-Command 7z -ErrorAction Stop).Source }
        & $sevenZip x -y "-o$temporary" $artifact | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Windows installer could not be expanded for compliance inspection.' }
        $hosts = @(Get-ChildItem -LiteralPath $temporary -Filter 'Threadsmith.App.exe' -File -Recurse)
        if ($hosts.Count -ne 1) { throw 'The Windows installer does not contain one unambiguous Threadsmith payload.' }
        $payload = $hosts[0].Directory.FullName
    } else { throw 'The artifact type is not supported for payload compliance inspection.' }

    foreach ($stagedFile in Get-ChildItem -LiteralPath $stage -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($stage, $stagedFile.FullName)
        $packagedFile = Join-Path $payload $relative
        if (-not (Test-Path -LiteralPath $packagedFile -PathType Leaf) -or ((Get-Item -LiteralPath $packagedFile).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The packaged artifact is missing or links staged file: $($relative.Replace('\', '/'))"
        }
        if ((Get-FileHash -LiteralPath $packagedFile -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $stagedFile.FullName -Algorithm SHA256).Hash) {
            throw "The packaged artifact changed staged file: $($relative.Replace('\', '/'))"
        }
    }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
