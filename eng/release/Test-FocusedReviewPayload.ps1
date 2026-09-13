[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory)
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$sourceRoot = Join-Path $PSScriptRoot '../../src/Threadsmith.Skills'
$pinSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'FocusedReviewRecipePin.cs') -Raw
$recipePin = [regex]::Match($pinSource, 'const string Sha256 = "([a-f0-9]{64})"').Groups[1].Value
$recipePath = Join-Path $stage 'ReviewSkills/recipe.json'
if ((Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $recipePin) { throw 'Focused review recipe integrity mismatch.' }
$recipe = Get-Content -LiteralPath $recipePath -Raw | ConvertFrom-Json
$packages = @([pscustomobject]@{ Path='MaintainedSkills/review'; Hash=$recipe.publicManifestSha256 })
$packages += @($recipe.dependencies | ForEach-Object { [pscustomobject]@{ Path="ReviewSkills/$($_.directory)"; Hash=$_.manifestSha256 } })
foreach ($package in $packages) {
    $root = Join-Path $stage $package.Path
    $manifestPath = Join-Path $root 'skill.json'
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.Hash) { throw 'Focused review manifest integrity mismatch.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($asset in $manifest.assets) {
        $path = Join-Path $root $asset.path
        if ((Get-Item -LiteralPath $path).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw 'Focused review asset integrity mismatch.' }
    }
}
if (@(Get-ChildItem -LiteralPath (Join-Path $stage 'MaintainedSkills') -Directory | Where-Object Name -like '*private*').Count -gt 0) { throw 'Private review dependency entered public discovery.' }
Write-Output 'Focused review public/private payload verified.'
