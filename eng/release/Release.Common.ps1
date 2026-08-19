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
