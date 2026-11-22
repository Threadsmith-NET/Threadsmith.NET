[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$neo4j = Join-Path $stage 'extensions/Neo4j'
if (-not (Test-Path -LiteralPath $neo4j -PathType Container)) { throw 'Staged payload is missing extensions/Neo4j.' }

# The private driver dependency must travel with the extension; the shared contract assembly must not.
foreach ($name in @('threadsmith.extension.json', 'Threadsmith.Extensions.Neo4j.dll', 'Neo4j.Driver.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $neo4j $name) -PathType Leaf)) {
        throw "Staged Neo4j extension payload is missing $name."
    }
}
if (Test-Path -LiteralPath (Join-Path $neo4j 'Threadsmith.Extensions.Abstractions.dll') -PathType Leaf) {
    throw 'The staged Neo4j extension ships the host contract assembly; the host owns the single copy.'
}

$manifest = Get-Content -LiteralPath (Join-Path $neo4j 'threadsmith.extension.json') -Raw | ConvertFrom-Json
if ($manifest.Id -ne 'threadsmith.neo4j' -or
    $manifest.EntryAssembly -ne 'Threadsmith.Extensions.Neo4j.dll' -or
    $manifest.EntryClass -ne 'Threadsmith.Extensions.Neo4j.Neo4jExtension' -or
    $manifest.RequiredTrust -ne 'FullyTrustedAutomation') {
    throw 'The staged Neo4j extension manifest identity is invalid.'
}

$notices = Join-Path $stage 'third-party/THIRD-PARTY-NOTICES.txt'
if (-not (Select-String -LiteralPath $notices -SimpleMatch -Pattern 'Neo4j.Driver' -Quiet)) {
    throw 'Release notices do not cover the bundled Neo4j extension driver.'
}
