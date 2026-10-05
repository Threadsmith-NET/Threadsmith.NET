[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string] $RuntimeIdentifier,
    [string] $OutputRoot = (Join-Path $PSScriptRoot '../artifacts/spellfix-assets')
)
$ErrorActionPreference = 'Stop'
$source = New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'source')
$assets = @(
    @{ Name = 'spellfix.c'; Url = 'https://raw.githubusercontent.com/sqlite/sqlite/version-3.50.4/ext/misc/spellfix.c'; Hash = '203a44811a57954a5fea64d207b559923099fb29331abb20f8d553f1df7dbcc4' },
    @{ Name = 'amalgamation.zip'; Url = 'https://www.sqlite.org/2025/sqlite-amalgamation-3500400.zip'; Hash = '1d3049dd0f830a025a53105fc79fd2ab9431aea99e137809d064d8ee8356b032' }
)
foreach ($asset in $assets) {
    $path = Join-Path $source $asset.Name
    if (-not (Test-Path -LiteralPath $path)) { Invoke-WebRequest $asset.Url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Spellfix source hash mismatch: $($asset.Name)" }
}
Expand-Archive -LiteralPath (Join-Path $source 'amalgamation.zip') -DestinationPath $source -Force
$include = Join-Path $source 'sqlite-amalgamation-3500400'
$output = New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot "$RuntimeIdentifier/native/spellfix")
$c = Join-Path $source 'spellfix.c'
if ($RuntimeIdentifier.StartsWith('win-')) {
    if (-not $IsWindows) { throw 'Windows spellfix builds require Visual Studio C++ tools on Windows.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Install Visual Studio C++ build tools (including ARM64 for win-arm64).' }
    Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    $arch = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'amd64' }
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$arch -host_arch=amd64" | Out-Null
    $library = Join-Path $output 'spellfix.dll'
    & cl.exe /nologo /O2 /MT /LD /Brepro "/I$include" $c "/Fo$(Join-Path $output 'spellfix.obj')" /link "/OUT:$library" "/IMPLIB:$(Join-Path $output 'spellfix.lib')"
} elseif ($RuntimeIdentifier.StartsWith('osx-')) {
    if (-not $IsMacOS) { throw 'macOS spellfix builds require the Apple SDK.' }
    $arch = if ($RuntimeIdentifier -eq 'osx-arm64') { 'arm64' } else { 'x86_64' }
    $library = Join-Path $output 'spellfix.dylib'
    & cc -O2 -dynamiclib -arch $arch -I $include $c -o $library
} else {
    if (-not $IsLinux) { throw 'Linux spellfix builds require a Linux compiler.' }
    $compiler = if ($RuntimeIdentifier -eq 'linux-arm64' -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64') { 'aarch64-linux-gnu-gcc' } else { 'gcc' }
    $library = Join-Path $output 'spellfix.so'
    & $compiler -O2 -fPIC -shared -I $include $c -o $library
}
if ($LASTEXITCODE -ne 0) { throw "Spellfix compilation failed for $RuntimeIdentifier." }
(Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath "$library.sha256" -Encoding ascii
# Preserve the upstream public-domain notice with the application-owned native payload.
Get-Content -LiteralPath $c -TotalCount 15 | Set-Content -LiteralPath (Join-Path $output 'LICENSE.txt') -Encoding utf8

@{ version = '3.50.4'; runtimeIdentifier = $RuntimeIdentifier; sources = $assets; library = [IO.Path]::GetFileName($library); sha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant(); license = 'SQLite public domain source notice in LICENSE.txt' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'SOURCE.json') -Encoding utf8
