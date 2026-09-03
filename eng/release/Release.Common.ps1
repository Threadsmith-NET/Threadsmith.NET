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


function Get-CodeDeclaredPromptNames {
    param([Parameter(Mandatory)][string] $SourceRoot)
    $contractPath = Join-Path $SourceRoot 'src/Threadsmith.Core/PromptContracts.cs'
    if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
        throw 'The code-owned prompt catalog contract is missing.'
    }

    $contract = Get-Content -LiteralPath $contractPath -Raw
    $constants = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($match in [Text.RegularExpressions.Regex]::Matches(
        $contract,
        'public const string (?<name>[A-Za-z0-9_]+) = "(?<file>[A-Za-z0-9_-]+\.md)";')) {
        $constants.Add($match.Groups['name'].Value, $match.Groups['file'].Value)
    }

    $allMatch = [Text.RegularExpressions.Regex]::Match(
        $contract,
        'public static IReadOnlyList<string> All \{ get; \} = Array\.AsReadOnly\(\s*\[(?<items>.*?)\]\);',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $allMatch.Success) { throw 'The code-owned prompt filename catalog could not be read.' }

    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in [Text.RegularExpressions.Regex]::Matches($allMatch.Groups['items'].Value, '\b[A-Za-z][A-Za-z0-9_]*\b')) {
        $symbol = $match.Value
        if (-not $constants.ContainsKey($symbol)) {
            throw "The code-owned prompt catalog references an unknown filename symbol: $symbol"
        }
        if (-not $names.Add($constants[$symbol])) {
            throw "The code-owned prompt catalog declares a duplicate filename: $($constants[$symbol])"
        }
    }
    if ($names.Count -eq 0) { throw 'The code-owned prompt filename catalog is empty.' }
    return ,$names
}

function Assert-CodeDeclaredPromptTokenContracts {
    param(
        [Parameter(Mandatory)][string] $SourceRoot,
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, IO.FileInfo]] $SourceFiles
    )
    $contract = Get-Content -LiteralPath (Join-Path $SourceRoot 'src/Threadsmith.Core/PromptContracts.cs') -Raw
    $constants = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($match in [Text.RegularExpressions.Regex]::Matches(
        $contract,
        'public const string (?<name>[A-Za-z0-9_]+) = "(?<file>[A-Za-z0-9_-]+\.md)";')) {
        $constants.Add($match.Groups['name'].Value, $match.Groups['file'].Value)
    }
    $declaredTokens = [Collections.Generic.Dictionary[string, Collections.Generic.HashSet[string]]]::new([StringComparer]::Ordinal)
    foreach ($match in [Text.RegularExpressions.Regex]::Matches(
        $contract,
        '\[PromptFileNames\.(?<name>[A-Za-z0-9_]+)\] = Set\((?<tokens>.*?)\),',
        [Text.RegularExpressions.RegexOptions]::Singleline)) {
        $fileName = $constants[$match.Groups['name'].Value]
        if (-not $declaredTokens.ContainsKey($fileName)) {
            $declaredTokens.Add($fileName, [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal))
        }
        foreach ($tokenMatch in [Text.RegularExpressions.Regex]::Matches($match.Groups['tokens'].Value, '"(?<token>[A-Za-z][A-Za-z0-9]*)"')) {
            $declaredTokens[$fileName].Add($tokenMatch.Groups['token'].Value) | Out-Null
        }
    }

    foreach ($fileName in $SourceFiles.Keys) {
        $markers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $content = Get-Content -LiteralPath $SourceFiles[$fileName].FullName -Raw
        foreach ($marker in [Text.RegularExpressions.Regex]::Matches($content, '\{\{(?<token>[A-Za-z][A-Za-z0-9]*)\}\}')) {
            $markers.Add($marker.Groups['token'].Value) | Out-Null
        }
        $tokens = if ($declaredTokens.ContainsKey($fileName)) { $declaredTokens[$fileName] } else { [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
        if ($markers.Count -ne $tokens.Count -or @($markers | Where-Object { -not $tokens.Contains($_) }).Count -ne 0) {
            throw "Prompt source does not match its code-owned token contract: $fileName"
        }
    }
}

function Assert-ReleasePromptPayload {
    param(
        [Parameter(Mandatory)][string] $PayloadDirectory,
        [Parameter(Mandatory)][string] $RuntimeIdentifier,
        [string] $SourceRoot = (Get-RepositoryRoot),
        [Collections.Generic.HashSet[string]] $ExpectedPromptNames
    )

    Assert-ReleaseRid $RuntimeIdentifier
    $source = (Resolve-Path -LiteralPath $SourceRoot).Path
    if ($null -eq $ExpectedPromptNames) {
        $ExpectedPromptNames = Get-CodeDeclaredPromptNames -SourceRoot $source
    }
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
    foreach ($name in $ExpectedPromptNames) {
        if (-not $expected.ContainsKey($name)) {
            throw "The code-owned prompt catalog is missing its source asset: $name"
        }
    }
    foreach ($name in $expected.Keys) {
        if (-not $ExpectedPromptNames.Contains($name)) {
            throw "Prompt source is not declared by the code-owned catalog: $name"
        }
    }
    if ($expected.Count -ne $ExpectedPromptNames.Count) {
        throw 'Prompt sources do not match the code-owned catalog.'
    }
    if (Test-Path -LiteralPath (Join-Path $source 'src/Threadsmith.Core/PromptContracts.cs') -PathType Leaf) {
        Assert-CodeDeclaredPromptTokenContracts -SourceRoot $source -SourceFiles $expected
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
