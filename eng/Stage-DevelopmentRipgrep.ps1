[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$RuntimeIdentifier,

    [string]$ArchivePath,

    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $os = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        "win"
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Linux)) {
        "linux"
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        "osx"
    }
    else {
        throw "The current operating system is not supported by the pinned ripgrep development assets."
    }

    $architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        ([System.Runtime.InteropServices.Architecture]::X64) { "x64"; break }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { "arm64"; break }
        default { throw "The current processor architecture is not supported by the pinned ripgrep development assets." }
    }

    $RuntimeIdentifier = "$os-$architecture"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Join-Path $repositoryRoot "artifacts") (Join-Path "dev-tools" $RuntimeIdentifier)
}
else {
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

$stageDirectory = Join-Path $OutputRoot "stage"
$workingDirectory = Join-Path $OutputRoot "work"
$stageScript = Join-Path $PSScriptRoot "release\Stage-Ripgrep.ps1"
$stageArguments = @{
    RuntimeIdentifier = $RuntimeIdentifier
    StageDirectory = $stageDirectory
    WorkingDirectory = $workingDirectory
}
if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
    $stageArguments.ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
}

& $stageScript @stageArguments
Write-Output "Staged verified development ripgrep assets for $RuntimeIdentifier under $stageDirectory."
Write-Output "Rebuild Threadsmith.App to copy them into the app-local tools and third-party directories."
