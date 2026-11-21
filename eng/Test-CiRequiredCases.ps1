[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Windows', 'Linux', 'macOS')]
    [string] $RunnerOS,

    # Supply the fresh directory used by this invocation, never a historical report aggregate.
    [Parameter(Mandatory)]
    [string] $ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$required = @{
    'Threadsmith.ParallelAgents.Tests.Plan91DelegationToolTests.IntelligenceInvestigators_AreNarrowAndCannotSupplyParentReceipts' = 1
    'Threadsmith.Architecture.Tests.CompositionBoundariesTests.ApplicationCompositionContext_NoLongerExists' = 1
    'Threadsmith.Architecture.Tests.CompositionBoundariesTests.ApplicationCompositionInputs_HasFiveCohesiveSubRecords' = 1
    'Threadsmith.Architecture.Tests.CompositionBoundariesTests.InputBundles_AreImmutableSealedRecordsWithInitOnlyProperties' = 1
    'Threadsmith.Architecture.Tests.CompositionBoundariesTests.InputBundles_DoNotImplementDisposal' = 1
    'Threadsmith.Architecture.Tests.CompositionBoundariesTests.InputBundles_DoNotExposeServiceLocatorTypes' = 1
    'Threadsmith.SessionStatus.Tests.SessionStatusTests.UsageProjection_Overflow_Saturates' = 1
}
if ($RunnerOS -eq 'Windows') {
    $required['Threadsmith.SecretResolution.Tests.SecretResolutionTests.UserProvider_AcceptsSharedWindowsAclWithoutChangingItAsync'] = 1
    $required['Threadsmith.SecretResolution.Tests.SecretResolutionTests.UserProvider_RespectsWindowsReadDenialAsync'] = 1
}
else {
    foreach ($method in @(
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.RepositoryProvider_RejectsUnixFifoWithoutBlockingAsync',
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.UserProvider_AcceptsSharedUnixModeWithoutChangingItAsync',
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.RepositoryProvider_UsesHostPathCaseSemanticsAsync',
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.RepositoryProvider_RejectsLinkReplacementAfterValidationAsync',
        'Threadsmith.Skills.Tests.Milestone17CompatibilityTests.ClaudeCatalog_Activate_RejectsNestedDirectoryLink',
        'Threadsmith.Skills.Tests.Milestone17CompatibilityTests.ClaudeCatalog_Activate_RejectsLinkedResourceFile',
        'Threadsmith.Skills.Tests.Milestone17CompatibilityTests.ActiveModels_LinkedSettingsDirectory_IsRejected'
    )) {
        $required[$method] = 1
    }
}
if ($RunnerOS -eq 'macOS') {
    $required['Threadsmith.ModelTooling.Tests.MacFileHandlePathTests.GetPath_FollowsHeldFileAfterRenameAsync'] = 1
}
if ($RunnerOS -eq 'Linux') {
    $required['Threadsmith.ContextCaching.Tests.DeployedPromptLoaderTests.LoadAsync_CaseInsensitivePhysicalCollision_FailsSafely'] = 1
}

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.xunit' -File -Recurse)
if ($reports.Count -eq 0) { throw "No xUnit reports found in $ResultsDirectory." }
$cases = @{}
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($report in $reports) {
    [xml] $document = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($case in $document.SelectNodes('/assemblies/assembly/collection/test')) {
        if ($case.result -ne 'NotRun') {
            $seconds = 0.0
            if (-not [double]::TryParse($case.time, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$seconds) -or
                -not [double]::IsFinite($seconds) -or $seconds -lt 0) {
                $failures.Add("$($case.name) has an invalid xUnit duration: '$($case.time)'.")
            }
            elseif ($seconds -ge 60) {
                $failures.Add("$($case.name) took $($case.time) seconds. Every test must finish in under 60 seconds.")
            }
        }

        $method = "$($case.type).$($case.method)"
        if ($required.ContainsKey($method)) {
            if (-not $cases.ContainsKey($method)) { $cases[$method] = @() }
            $cases[$method] += $case
        }
    }
}
foreach ($method in ($required.Keys | Sort-Object)) {
    $observed = @($cases[$method] | Where-Object { $null -ne $_ })
    $names = @($observed.name | Sort-Object -Unique)
    if ($names.Count -ne $required[$method] -or @($observed | Where-Object result -ne 'Pass').Count -gt 0) {
        $failures.Add("$method expected $($required[$method]) passing cases; observed $($names.Count) unique cases, results: $($observed.result -join ', ').")
    }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host "Required CI cases executed successfully for $RunnerOS ($($required.Count) methods)."
