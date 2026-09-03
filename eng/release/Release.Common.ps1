Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ReleaseVersion {
    param([Parameter(Mandatory)][string] $Version)
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$') {
        throw "Unsupported release version '$Version'. Supply SemVer without a leading v."
    }
}

function Assert-ReleaseRid {
    param([Parameter(Mandatory)][string] $RuntimeIdentifier)
    $supported = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
    if ($RuntimeIdentifier -notin $supported) { throw "Unsupported runtime identifier '$RuntimeIdentifier'." }
}

function Get-RepositoryRoot { return (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path }

function Initialize-CleanDirectory {
    param([Parameter(Mandatory)][string] $Path)
    if (Test-Path -LiteralPath $Path) {
        if ((Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)) {
            throw "Output directory must be absent or empty: $Path"
        }
    } else { New-Item -ItemType Directory -Path $Path | Out-Null }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-FileDigestRecord {
    param([Parameter(Mandatory)][System.IO.FileInfo] $File, [Parameter(Mandatory)][string] $BasePath, [string] $Component = 'payload')
    $relative = [IO.Path]::GetRelativePath($BasePath, $File.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
    [ordered]@{ path = $relative; size = $File.Length; sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); executable = -not $IsWindows -and (($File.UnixFileMode -band [IO.UnixFileMode]::UserExecute) -ne 0); component = $Component }
}

function Get-DirectoryDigest {
    param([Parameter(Mandatory)][string] $Path, [string[]] $ExcludeRelativePaths = @())
    $excluded = @($ExcludeRelativePaths | ForEach-Object { $_.Replace('\', '/') })
    $lines = Get-ChildItem -LiteralPath $Path -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
        if ($relative -notin $excluded) {
            "$relative`0$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
        }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $lines))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-ReleasePromptPayload {
    param(
        [Parameter(Mandatory)][string] $PayloadDirectory,
        [Parameter(Mandatory)][string] $RuntimeIdentifier,
        [string] $SourceRoot = (Get-RepositoryRoot)
    )

    Assert-ReleaseRid $RuntimeIdentifier
    $source = (Resolve-Path -LiteralPath $SourceRoot).Path
    $payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
    $ownerDirectories = @(
        'src/Threadsmith.Context/Prompts',
        'src/Threadsmith.Execution/Prompts',
        'src/Threadsmith.Tools/Prompts',
        'src/Threadsmith.DotNet/Prompts',
        'src/Threadsmith.Skills/Prompts',
        'src/Threadsmith.Models/Prompts',
        'src/Threadsmith.Models.OpenAiCodex/Prompts',
        'src/Threadsmith.Mcp/Prompts'
    )
    $expected = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::Ordinal)
    $expectedCaseInsensitive = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeDirectory in $ownerDirectories) {
        $directory = Join-Path $source $relativeDirectory
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "Prompt source owner directory is missing: $relativeDirectory"
        }
        $directoryInfo = Get-Item -LiteralPath $directory -Force
        if (($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Prompt source owner directory is linked: $relativeDirectory"
        }
        $ownerFiles = [Collections.Generic.List[IO.FileInfo]]::new()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $entry.Name -notmatch '^[A-Za-z0-9_-]+\.md$') {
                throw "Prompt source owner directory contains an undeclared entry: $relativeDirectory/$($entry.Name)"
            }
            $file = [IO.FileInfo]$entry
            $ownerFiles.Add($file)
            if (-not $expected.TryAdd($file.Name, $file)) {
                throw "Prompt source filename is duplicated: $($file.Name)"
            }
            if (-not $expectedCaseInsensitive.TryAdd($file.Name, $file)) {
                throw "Prompt source filenames collide case-insensitively: $($file.Name)"
            }
        }
        if ($ownerFiles.Count -eq 0) {
            throw "Prompt source owner directory is empty: $relativeDirectory"
        }
    }

    $promptDirectory = Join-Path $payload 'prompts'
    if (-not (Test-Path -LiteralPath $promptDirectory -PathType Container)) {
        throw "Published prompt directory is missing for ${RuntimeIdentifier}: prompts"
    }
    $promptDirectoryInfo = Get-Item -LiteralPath $promptDirectory -Force
    if (($promptDirectoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Published prompt directory is linked for ${RuntimeIdentifier}: prompts"
    }
    $actual = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::Ordinal)
    $actualCaseInsensitive = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-ChildItem -LiteralPath $promptDirectory -Force) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $entry.Name -notmatch '^[A-Za-z0-9_-]+\.md$') {
            throw "Published prompt directory contains an undeclared entry for ${RuntimeIdentifier}: prompts/$($entry.Name)"
        }
        $file = [IO.FileInfo]$entry
        if (-not $actual.TryAdd($file.Name, $file)) {
            throw "Published prompt filename is duplicated for ${RuntimeIdentifier}: $($file.Name)"
        }
        if (-not $actualCaseInsensitive.TryAdd($file.Name, $file)) {
            throw "Published prompt filenames collide case-insensitively for ${RuntimeIdentifier}: $($file.Name)"
        }
    }

    foreach ($name in $expected.Keys) {
        if (-not $actual.ContainsKey($name)) {
            throw "Published prompt payload is missing a required asset for ${RuntimeIdentifier}: prompts/$name"
        }
        if ((Get-FileHash -LiteralPath $actual[$name].FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $expected[$name].FullName -Algorithm SHA256).Hash) {
            throw "Published prompt asset differs from its source for ${RuntimeIdentifier}: prompts/$name"
        }
    }
    foreach ($name in $actual.Keys) {
        if (-not $expected.ContainsKey($name)) {
            throw "Published prompt payload contains an undeclared asset for ${RuntimeIdentifier}: prompts/$name"
        }
    }
    if ($actual.Count -ne $expected.Count) {
        throw "Published prompt payload has the wrong asset count for ${RuntimeIdentifier}."
    }
}
