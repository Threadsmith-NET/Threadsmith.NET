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
    'Threadsmith.McpTransports.Tests.SdkStdioTransportTests.Real_stdio_server_connects_imports_invokes_and_disconnects' = 1
    'Threadsmith.McpTransports.Tests.SdkStdioTransportTests.Hung_stdio_server_process_is_killed_within_drain_timeout' = 1
    'Threadsmith.McpTransports.Tests.SdkStdioTransportTests.Failed_stdio_handshake_terminates_spawned_process' = 1
    'Threadsmith.McpTransports.Tests.SdkStdioTransportTests.StopAsync_RemainingDeadlineOverridesOriginalSdkShutdownTimeout' = 1
    'Threadsmith.ModelTooling.Tests.ToolRuntimeTests.SearchTextTool_RipgrepFastPath_ReturnsMatchesAfterProcessSanitization' = 1
    'Threadsmith.ModelTooling.Tests.ToolRuntimeTests.SearchTextTool_RealRipgrepTruncation_PreservesCompleteMatches' = 1
    'Threadsmith.ModelTooling.Tests.ToolRuntimeTests.SearchTextTool_ProhibitedPaths_FilterRipgrepResultsAuthoritatively' = 1
    'Threadsmith.ModelTooling.Tests.WriteFileTests.Scratchpad_RipgrepSearchFindsIgnoredInRepositoryFiles' = 1
    'Threadsmith.ParallelAgents.Tests.Plan91DelegationToolTests.IntelligenceInvestigators_AreNarrowAndCannotSupplyParentReceipts' = 1
    'Threadsmith.NativeTools.Tests.Plan41InventoryToolTests.GitQueries_LogicalRootAlias_PreservesWorktreeBoundary' = 2
    'Threadsmith.ModelTooling.Tests.SemanticRefreshCoordinatorTests.SemanticEngine_SemanticRefreshInventoryIsRepositoryConfined' = 1
    'Threadsmith.ModelTooling.Tests.Milestone3Tests.SemanticFixtures_BuildOutputLocksRemainLocalToOwner' = 1
    'Threadsmith.Architecture.Tests.CompositionBoundariesTests.SubRecord_HasExpectedResponsibilitySize' = 5
    'Threadsmith.SessionStatus.Tests.SessionStatusTests.UsageProjection_Overflow_Saturates' = 1
    'Threadsmith.Extensions.Tests.ExtensionRuntimeTests.LoadAsync_loads_sample_extension_and_collects_capabilities' = 1
    'Threadsmith.Extensions.Tests.UnloadTests.Clean_extension_unloads_and_the_ALC_is_dead' = 1
    'Threadsmith.Extensions.Tests.UnloadTests.Leaking_extension_is_diagnosed_as_UnloadBlocked_and_host_remains_functional' = 1
}
if ($RunnerOS -eq 'Windows') {
    $required['Threadsmith.RepositoryLifecycle.Tests.RepositoryLifecycleTests.OpenRepository_WindowsReservedNameEntry_SkipsEntryAndFindsSolution'] = 1
    $required['Threadsmith.SecretResolution.Tests.SecretResolutionTests.UserProvider_AcceptsSharedWindowsAclWithoutChangingItAsync'] = 1
    $required['Threadsmith.SecretResolution.Tests.SecretResolutionTests.UserProvider_RespectsWindowsReadDenialAsync'] = 1
}
else {
    foreach ($method in @(
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.RepositoryProvider_RejectsUnixFifoWithoutBlockingAsync',
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.UserProvider_AcceptsSharedUnixModeWithoutChangingItAsync',
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.RepositoryProvider_UsesHostPathCaseSemanticsAsync',
        'Threadsmith.SecretResolution.Tests.SecretResolutionTests.RepositoryProvider_RejectsLinkReplacementAfterValidationAsync',
        'Threadsmith.McpOAuth.Tests.McpOAuthFlowTests.Token_cache_file_has_private_unix_permissions',
        'Threadsmith.Skills.Tests.Milestone17CompatibilityTests.ClaudeCatalog_Activate_RejectsNestedDirectoryLink',
        'Threadsmith.Skills.Tests.Milestone17CompatibilityTests.ClaudeCatalog_Activate_RejectsLinkedResourceFile',
        'Threadsmith.Skills.Tests.Milestone17CompatibilityTests.ActiveModels_LinkedSettingsDirectory_IsRejected',
        'Threadsmith.ModelTooling.Tests.SemanticRefreshCoordinatorTests.SemanticEngine_SemanticRefreshInventoryRejectsLinkedDirectory'
    )) {
        $required[$method] = 1
    }
}
if ($RunnerOS -eq 'macOS') {
    $required['Threadsmith.Architecture.Tests.SecurityPolicyConfigurationTests.MacVolumesEnforceProhibitionsAcrossCaseBoundaries'] = 2
    $required['Threadsmith.ModelTooling.Tests.MacFileHandlePathTests.GetPath_FollowsHeldFileAfterRenameAsync'] = 1
}
if ($RunnerOS -eq 'Linux') {
    $required['Threadsmith.ContextCaching.Tests.DeployedPromptLoaderTests.LoadAsync_CaseInsensitivePhysicalCollision_FailsSafely'] = 1
}

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.xunit' -File -Recurse)
if ($reports.Count -eq 0) { throw "No xUnit reports found in $ResultsDirectory." }
$cases = @{}
foreach ($report in $reports) {
    [xml] $document = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($case in $document.SelectNodes('/assemblies/assembly/collection/test')) {
        $method = "$($case.type).$($case.method)"
        if ($required.ContainsKey($method)) {
            if (-not $cases.ContainsKey($method)) { $cases[$method] = @() }
            $cases[$method] += $case
        }
    }
}
$failures = @()
foreach ($method in ($required.Keys | Sort-Object)) {
    $observed = @($cases[$method] | Where-Object { $null -ne $_ })
    $names = @($observed.name | Sort-Object -Unique)
    if ($names.Count -ne $required[$method] -or @($observed | Where-Object result -ne 'Pass').Count -gt 0) {
        $failures += "$method expected $($required[$method]) passing cases; observed $($names.Count) unique cases, results: $($observed.result -join ', ')."
    }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host "Required CI cases executed successfully for $RunnerOS ($($required.Count) methods)."
