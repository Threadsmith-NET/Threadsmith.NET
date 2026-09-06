[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory)
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$root = Join-Path $stage 'ThreadsmithDocs'
$manifestPath = Join-Path $root 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Staged payload is missing ThreadsmithDocs/manifest.json.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.bundleId -ne 'threadsmith-docs' -or $manifest.bundleVersion -ne 1) {
    throw 'Packaged documentation manifest identity is invalid.'
}

foreach ($relative in $manifest.requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Packaged documentation is missing required file: $relative"
    }
}

$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
if ($files.Count -gt 256 -or ($files | Measure-Object -Property Length -Sum).Sum -gt 5MB) {
    throw 'Packaged documentation exceeds its file-count or byte bound.'
}

foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Recurse) {
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Packaged documentation cannot contain linked directories or reparse points.'
    }
}

foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Packaged documentation cannot contain links or reparse points.'
    }

    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    $allowed = $manifest.allowedFiles -contains $relative
    if (-not $allowed) {
        foreach ($prefix in $manifest.includedPrefixes) {
            if ($relative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and $relative.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
                $allowed = $true
                break
            }
        }
    }
    if (-not $allowed) {
        throw "Packaged documentation contains an unmanifested file: $relative"
    }

    foreach ($prefix in $manifest.excludedPrefixes) {
        if ($relative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Packaged documentation contains excluded content: $relative"
        }
    }
}

$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$pathComparison = if ([OperatingSystem]::IsWindows()) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
foreach ($document in $files | Where-Object Extension -EQ '.md') {
    $insideFence = $false
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $document.FullName) {
        $lineNumber++
        if ($line.TrimStart().StartsWith('```', [StringComparison]::Ordinal) -or $line.TrimStart().StartsWith('~~~', [StringComparison]::Ordinal)) {
            $insideFence = -not $insideFence
            continue
        }
        if ($insideFence) {
            continue
        }

        foreach ($match in [regex]::Matches($line, '!?(?:\[[^\]]*\])\((?<target>[^)]+)\)')) {
            $destination = $match.Groups['target'].Value.Trim().Trim('<', '>')
            if ($destination -match '(?i)^(https?|mailto):' -or $destination.StartsWith('#', [StringComparison]::Ordinal)) {
                continue
            }

            $destination = ($destination -split '#', 2)[0]
            $destination = ($destination -split '\s+["'']', 2)[0]
            $destination = [Uri]::UnescapeDataString($destination)
            if ([string]::IsNullOrWhiteSpace($destination) -or [IO.Path]::IsPathRooted($destination)) {
                throw "Packaged documentation contains an invalid local link at $([IO.Path]::GetRelativePath($root, $document.FullName)):$lineNumber."
            }

            $target = [IO.Path]::GetFullPath((Join-Path $document.DirectoryName $destination))
            if (-not $target.StartsWith($rootPrefix, $pathComparison) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
                throw "Packaged documentation contains a missing or escaping local link '$destination' at $([IO.Path]::GetRelativePath($root, $document.FullName)):$lineNumber."
            }
        }
    }
}

[ordered]@{
    schemaVersion = 1
    bundleId = $manifest.bundleId
    bundleVersion = $manifest.bundleVersion
    fileCount = $files.Count
    totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
}
