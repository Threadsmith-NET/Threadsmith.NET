[CmdletBinding()]
param([Parameter(Mandatory)][string] $PackagePath, [Parameter(Mandatory)][string] $KeychainProfile)
if (-not $IsMacOS) { throw 'macOS notarization requires macOS.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
xcrun notarytool submit $package --keychain-profile $KeychainProfile --wait
if ($LASTEXITCODE -ne 0) { throw 'Package notarization failed.' }
xcrun stapler staple $package
if ($LASTEXITCODE -ne 0) { throw 'Notarization stapling failed.' }
xcrun stapler validate $package
if ($LASTEXITCODE -ne 0) { throw 'Notarization validation failed.' }
