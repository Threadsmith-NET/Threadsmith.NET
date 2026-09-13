# Configurable resource limits

Threadsmith-owned operational limits can be changed in configuration. Omitted values retain their defaults. This includes the formerly fixed conversation tool-call count, workspace baseline and mutation limits, file response windows, semantic search and inventory limits, validation and Git output, MCP transport bounds, memory retrieval, and TUI retention/rendering.

The [complete defaults example](../../.threadsmith/resource-limits.example) is a **merge reference**, not another automatically loaded file. Copy only the settings you want into `~/.threadsmith/config.json` or another eligible layer. The [main example](../../.threadsmith/config.example) shows common settings. Settings and units are identical on Windows, Linux, and macOS.

## Scope and validation

Normal precedence is compiled defaults, machine, user, repository, session, CLI, then environment. For example, `--set:limits:workspace:maximumBaselineContentBytes=1073741824` configures a 1 GiB baseline. `THREADSMITH_limits__workspace__maximumBaselineContentBytes` is the equivalent environment key. **Trusted-only** groups below read machine/user/environment configuration; repository, session, and CLI values do not widen those policies. Web-fetch, prompt-append, and skill catalog, schema, discovery, and installer options may narrow their trusted ceilings. The LCS diff allocation threshold uses the trusted machine/user/environment ceiling; ordinary configuration can narrow it. Larger valid diffs use the existing linear fallback.

New typed limit groups reject unknown members and invalid values. Most new bounds require positive values; there is no arbitrary upper ceiling beyond the storage/runtime type. Exceptions are stated in the tables. Existing settings that support zero as disabled retain that behavior. Byte counts are bytes (KiB = 1,024; MiB = 1,048,576); character counts are .NET UTF-16 code units unless a setting explicitly says otherwise. Counts, milliseconds, and seconds are named accordingly. Related settings must remain consistent: a default cannot exceed its maximum, aggregate prompt budgets cannot be smaller than one file, and extension stability timeout cannot be shorter than its quiet interval.

Most settings are captured when the application starts: restart after editing them. Repository-memory settings retain their existing repository-rebind behavior. Increasing a limit changes resource admission or retained output; it does not grant filesystem, tool, network, or secret authority.

Numeric and downstream API boundaries still apply. Invalid values fail with configuration/argument errors rather than wrapping arithmetic or being silently clamped. `tools.runProcess.maxTimeoutSeconds` cannot exceed 4,294,967 whole seconds because .NET 10 cancellation timers store unsigned 32-bit milliseconds. `model.http.connectTimeoutSeconds` accepts 0 (disabled) through 2,147,483 whole seconds because `SocketsHttpHandler` uses a signed 32-bit millisecond bound. MCP profile startup, request, and drain/kill timeouts must be positive and cannot exceed 4,294,967 whole seconds for the same .NET timer reason. MCP OAuth metadata and callback-line byte ceilings cannot exceed `Array.MaxLength`; readers allocate incrementally from actual input. These are representation limits, not repository admission policies. Defaults remain unchanged.

Large composer drafts with many separate graphemes can be slow in the current TUIKit grapheme splitter, which repeatedly scans preceding characters. Raising `tui.limits.maximumDraftBytes` changes admission but does not change that backend processing cost.

Shipped defaults preserve the previous fixed values. Lowering semantic traversal ceilings also lowers tool-owned request defaults; callers are not required to override hidden request fields. MCP management caps requested result counts at `mcp.limits.maximumCapabilities`, including its normal 256-item request default, without disabling connection or authentication operations. `execution.maxAgentDisplayFragmentCharacters` retains its 4,096 default and accepts values of at least two UTF-16 code units so a complete Unicode scalar can fit. Smaller values produce a configuration error. Skill dependency-cycle validation is iterative and introduces no graph-depth admission ceiling.

Web-search and web-fetch request timeouts must fit the .NET timer range: at most 4,294,967,294 milliseconds (4,294,967 whole seconds). Values above that range are rejected during configuration/provider construction, before a request starts. Their defaults remain 15 seconds.

## Tool runtime overrides

Every registered tool has a deadline, serialized result-byte limit, and source concurrency declaration. `tools.runtime.defaults` overrides those declarations; `tools.runtime.byTool` overrides matching fields for an exact registered tool ID (case-insensitive). Omitted fields retain the declaration or global override. Dynamic MCP/extension registrations use the same policy and preserve replacement identity. IDs for unloaded dynamic tools can be configured before they register. Entries form an array with a `toolId` field so colon-qualified MCP IDs survive configuration binding; duplicate IDs are rejected.

```json
{
  "tools": {
    "runtime": {
      "defaults": { "timeoutMilliseconds": 120000 },
      "byTool": [
        { "toolId": "read_file", "maximumOutputBytes": 2097152 },
        { "toolId": "server:search", "timeoutMilliseconds": 90000, "maximumSourceConcurrency": 8 }
      ]
    },
    "readFile": { "defaultLines": 4000, "maxLines": 4000, "maxContentBytes": 262144 }
  }
}
```

`timeoutMilliseconds` accepts positive values or `-1` to disable the outer tool deadline. `maximumOutputBytes` and `maximumSourceConcurrency` must be positive. Source concurrency is additionally constrained by the global scheduler and trust policy.

**Limits compose.** To enlarge a result, raise each relevant layer: source-file/read window, service/projection size, then tool serialization size. For MCP, profile request timeouts and response/frame/line limits also apply. For scripts, worker timeout/output and outer tool timeout/output are separate. Model context budgets remain independent; a large tool result can still require compaction. Truncation, existing fallback behavior, or an explicit failure applies at the enforcing layer.

## Settings and defaults

Each table gives fields beneath the named configuration section. Defaults below retain prior behavior unless noted.

### `limits:workspace`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumSdkConfigurationBytes` | `1048576` | Maximum global.json SDK metadata bytes. |
| `maximumBaselineContentBytes` | `268435456` | Maximum aggregate baseline content retained in bytes. |
| `maximumMutations` | `100` | Maximum mutations admitted in one batch. |
| `maximumMutationCharacters` | `4194304` | Maximum aggregate replacement/content characters in one batch. |
| `maximumRationaleCharacters` | `8192` | Maximum characters in the batch rationale. |
| `maximumDiffLinesForLcs` | `512` | Line-count scale whose square bounds the LCS diff matrix. Ordinary configuration can only narrow the trusted machine/user/environment ceiling. Uses the existing linear diff fallback above that budget or the runtime array capacity. |
| `maximumConcurrentConflictHashes` | `4` | Maximum concurrent hashes during conflict detection. |
| `maximumConcurrentBaselineHashes` | `8` | Maximum concurrent hashes during baseline capture. |
| `maximumBaselineStatusLines` | `1000` | Maximum Git status lines retained with a baseline. |
| `maximumProcessOutputCharacters` | `65536` | Maximum characters captured by repository lifecycle and worktree Git commands. |
| `maximumMutationMetadataItems` | `100` | Maximum affected projects, expected diagnostics, or expected tests. |
| `maximumMutationPathCharacters` | `1024` | Maximum characters in a mutation path. |
| `maximumMutationSymbolIdCharacters` | `4096` | Maximum related-symbol identifier characters. |
| `maximumValidationPolicyCharacters` | `256` | Maximum validation-policy name characters. |

### `limits:semantic`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumFallbackEntries` | `50000` | Maximum fallback directory entries inspected. |
| `maximumFallbackFiles` | `10000` | Maximum fallback files inspected. |
| `maximumFallbackMatches` | `500` | Maximum fallback matches returned. |
| `maximumFallbackFileBytes` | `1048576` | Maximum fallback-search input file size in bytes. |
| `maximumInventoryProjects` | `2000` | Maximum projects returned by inventory. |
| `maximumInventoryXmlBytes` | `1048576` | Maximum project/import XML input bytes for inventory metadata. |
| `maximumGeneratedDocuments` | `100` | Maximum generated documents inspected. |
| `maximumGeneratedContentCharacters` | `16384` | Maximum generated document content characters. |
| `maximumTraversalDepth` | `8` | Maximum requested semantic traversal depth. |
| `maximumTraversalNodes` | `1000` | Maximum requested semantic traversal nodes. |
| `maximumTraversalEdges` | `5000` | Maximum requested semantic traversal edges. |
| `maximumTraversalTimeoutMilliseconds` | `60000` | Maximum requested traversal or pattern-search milliseconds. |
| `maximumPatternMatches` | `1000` | Maximum requested pattern-search matches. |
| `maximumPatternNameCharacters` | `256` | Maximum characters in a pattern predicate. |
| `maximumPatternPredicateValues` | `16` | Maximum pattern modifier or attribute values. |
| `maximumSymbolIdCharacters` | `2048` | Maximum symbol identifier characters. |
| `modelMaximumCallEdges` | `32` | Model-facing maximum call edges. |
| `modelMaximumCallSymbols` | `32` | Model-facing maximum call symbols. |
| `modelMaximumImpactItems` | `32` | Model-facing maximum impact items. |
| `modelMaximumPatternMatches` | `40` | Model-facing maximum pattern matches. |
| `modelMaximumGeneratedDocuments` | `12` | Model-facing maximum generated documents. |
| `modelMaximumGeneratedContentCharacters` | `4096` | Model-facing maximum generated content characters. |
| `modelMaximumOmissions` | `8` | Model-facing maximum omissions. |
| `maximumModelItemsPerProject` | `12` | Inventory maximum model items per project. |
| `maximumModelOmissions` | `20` | Inventory maximum model omissions. |
| `maximumModelProjects` | `25` | Inventory maximum model projects. |
| `maximumModelResultCharacters` | `131072` | Inventory maximum model result characters. |
| `maximumModelTargetFrameworks` | `12` | Inventory maximum model target frameworks. |

### `limits:git`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `timeoutMilliseconds` | `30000` | Maximum elapsed milliseconds per Git query. |
| `maximumCapturedCharacters` | `524288` | Maximum captured text-command characters; also the byte ceiling for raw Git blobs. Truncated UTF-8 blobs end at a complete scalar and retain their truncation flag. |
| `maximumCommits` | `500` | Maximum log commits returned. |
| `maximumBlameLines` | `500` | Maximum blame lines returned. |
| `maximumChangedPaths` | `500` | Maximum changed paths returned. |
| `maximumDiffEntries` | `200` | Maximum diff entries returned. |
| `maximumPatchCharacters` | `131072` | Maximum patch text characters. |
| `maximumShowCharacters` | `131072` | Maximum git-show text characters. |

### `limits:validation`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumProjectXmlBytes` | `1048576` | Maximum test-project XML bytes. |
| `timeoutMilliseconds` | `120000` | Maximum elapsed milliseconds per validation operation. |
| `diagnosticPageSize` | `100` | Maximum diagnostic entries per page. |
| `maximumAdvisories` | `200` | Maximum advisories returned. |
| `maximumOutputCharacters` | `524288` | Maximum native-validation subprocess output characters. |
| `maximumDependencies` | `1000` | Maximum dependencies returned. |
| `maximumDiscoveredTests` | `500` | Maximum tests returned by discovery. |
| `maximumAssetsBytes` | `16777216` | Maximum project.assets.json input bytes. |
| `maximumBuildOutputCharacters` | `1048576` | Maximum build-executor captured output characters. |
| `maximumAdvisorySources` | `16` | Maximum configured advisory sources. |
| `maximumModelAdvisories` | `200` | Maximum Model Advisories in validation projections. |
| `maximumModelDependencies` | `100` | Maximum Model Dependencies in validation projections. |
| `maximumModelDiagnostics` | `100` | Maximum Model Diagnostics in validation projections. |
| `maximumModelTests` | `200` | Maximum Model Tests in validation projections. |
| `maximumModelOmissions` | `20` | Maximum Model Omissions in validation projections. |
| `maximumModelAttachments` | `10` | Maximum Model Attachments in validation projections. |
| `maximumModelLabelCharacters` | `64` | Maximum Model Label Characters in validation projections. |
| `maximumModelIdentifierCharacters` | `128` | Maximum Model Identifier Characters in validation projections. |
| `maximumModelNameCharacters` | `256` | Maximum Model Name Characters in validation projections. |
| `maximumModelSummaryCharacters` | `512` | Maximum Model Summary Characters in validation projections. |
| `maximumModelPathCharacters` | `1024` | Maximum Model Path Characters in validation projections. |
| `maximumModelMessageCharacters` | `2048` | Maximum Model Message Characters in validation projections. |
| `maximumModelOutputCharacters` | `16384` | Maximum Model Output Characters in validation projections. |

### `limits:plan`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumSteps` | `100` | Maximum steps in a structured plan. |
| `maximumMetadataItems` | `100` | Maximum risks, questions, file intents, or validation expectations. |
| `maximumSummaryCharacters` | `4096` | Maximum summary, risk, question, or expected-outcome characters. |
| `maximumTitleCharacters` | `256` | Maximum step title characters. |
| `maximumDescriptionCharacters` | `8192` | Maximum step description characters. |
| `maximumPathCharacters` | `1024` | Maximum plan path characters. |

### `limits:process`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumEnvironmentVariables` | `16` | Maximum child environment additions. |
| `maximumEnvironmentNameCharacters` | `128` | Maximum environment-variable name characters. |
| `maximumEnvironmentValueCharacters` | `16384` | Maximum environment-variable value characters. |
| `drainTimeoutMilliseconds` | `5000` | Deadline for draining a terminated child process in milliseconds. |

### `limits:policyStores`

Ordinary configuration. [Implementation](../../src/Threadsmith.Core/OperationalLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumApprovalEntries` | `4096` | Maximum persisted MCP tool approvals. |
| `maximumPolicyFileBytes` | `1048576` | Maximum MCP approval or skill trust-policy file bytes, enforced before replacing a file as well as during load. |
| `maximumSkillPolicyEntries` | `2048` | Maximum items per skill policy selector/package list; rejected updates preserve the previous policy. |
| `maximumSkillPolicyItemCharacters` | `1024` | Maximum skill policy item characters. |

### `tui:limits`

Ordinary configuration. [Implementation](../../src/Threadsmith.Interaction/Contracts/TuiResourceLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumBufferedKeys` | `100000` | Maximum buffered keystrokes in the PrettyPrompt frontend. |
| `maximumDraftBytes` | `1048576` | Maximum editable draft and pasted UTF-8 bytes. |
| `maximumUndoBytes` | `1048576` | Maximum retained undo text bytes. |
| `maximumUndoEntries` | `200` | Maximum retained undo operations. |
| `maximumHistoryBytes` | `1048576` | Maximum submitted history UTF-8 bytes per composer. |
| `maximumHistoryEntries` | `1000` | Maximum submitted entries per composer. |
| `clipboardTimeoutMilliseconds` | `2000` | Clipboard helper timeout across Windows, Linux, and macOS. |
| `maximumClipboardCopyBytes` | `65536` | Maximum UTF-8 bytes copied to the terminal clipboard. |
| `maximumToggleOptions` | `2048` | Maximum selectable entries in an enable/disable modal. |
| `maximumTranscriptBytes` | `524288` | Maximum retained source or projected text bytes per transcript. |
| `maximumTranscriptLines` | `1024` | Maximum retained logical transcript chunks. |
| `maximumTranscriptLineCharacters` | `16384` | Maximum characters per projected line chunk. |
| `maximumGlyphCacheRows` | `512` | Maximum cached glyph rows per transcript. |
| `maximumLinks` | `512` | Maximum links offered by the output link selector. |
| `maximumChildTranscriptBytes` | `4194304` | Aggregate child source and projected text bytes shared among agent tabs. |
| `maximumChildTranscriptLines` | `8192` | Aggregate projected child transcript chunks. |
| `maximumRetainedDelegations` | `64` | Maximum recently closed delegation records. |
| `maximumPendingAgentUpdates` | `256` | Maximum child output updates pending agent registration. |
| `maximumAgentProgressCharacters` | `240` | Maximum child progress summary characters. |
| `maximumFilterCharacters` | `256` | Maximum modal search/filter characters. |
| `maximumCommandTitleCharacters` | `512` | Maximum command palette title characters. |
| `maximumAgentNames` | `128` | Maximum configured names per role or shared list. |
| `maximumAgentNameCharacters` | `32` | Maximum normalized agent-name UTF-16 units. |
| `maximumThemes` | `32` | Maximum configured themes. |
| `maximumThemeNameCharacters` | `80` | Maximum theme display-name characters. |
| `maximumThemeUiCharacters` | `40` | Maximum theme spinner, marker, or separator characters. |
| `maximumTrackedRefreshStarts` | `128` | Maximum retained semantic-refresh start identities. |
| `maximumDisplayedDelegations` | `12` | Maximum delegation summaries displayed. |
| `maximumTrackedDelegations` | `64` | Maximum retained delegation status records. |
| `maximumToolInspectionCharacters` | `98304` | Maximum expanded tool or memory output characters. |

### `tui:limits:markdown`

Ordinary configuration. [Implementation](../../src/Threadsmith.Interaction/Markdown/MarkdownRenderingLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumSourceBytes` | `262144` | Maximum UTF-8 answer bytes parsed as Markdown; longer answers stream as escaped source. |
| `maximumNodes` | `10000` | Maximum parser and semantic nodes. |
| `maximumDepth` | `32` | Maximum semantic nesting depth, from 1 through 32. The recursive parser/validator/layout retain a host-owned stack-safety ceiling; deeper content uses escaped-source fallback. |
| `maximumListItems` | `1000` | Maximum items per list. |
| `maximumTableRows` | `200` | Maximum rows per table. |
| `maximumTableColumns` | `20` | Maximum columns per table. |
| `maximumCellCharacters` | `4096` | Maximum text characters per table cell. |
| `maximumCodeCharacters` | `131072` | Maximum code block characters. |
| `maximumLinkCharacters` | `2048` | Maximum link target characters. |
| `maximumLanguageCharacters` | `64` | Maximum code language identifier characters. |

### `mcp:limits`

Trusted machine/user/environment configuration. [Implementation](../../src/Threadsmith.Mcp/McpResourceLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumContentItems` | `64` | Maximum resource, prompt, or tool content blocks retained. |
| `maximumIdentityJsonDepth` | `16` | Maximum OAuth identity metadata JSON depth. |
| `maximumResourceLabelCharacters` | `1024` | Maximum resource label characters. |
| `maximumHeaderValueCharacters` | `8192` | Maximum configured HTTP header value characters. |
| `maximumArguments` | `32` | Maximum Arguments. |
| `maximumArgumentCharacters` | `16384` | Maximum Argument Characters. |
| `maximumCapabilities` | `256` | Maximum Capabilities. |
| `maximumFailureCharacters` | `1024` | Maximum Failure Characters. |
| `maximumProfiles` | `64` | Maximum Profiles. |
| `maximumRecentLatencySamples` | `32` | Maximum Recent Latency Samples. |
| `maximumCapabilitiesPerKind` | `256` | Maximum Capabilities Per Kind. |
| `maximumContentCharacters` | `262144` | Maximum Content Characters. |
| `maximumDescriptionCharacters` | `2048` | Maximum Description Characters. |
| `maximumIdentityCharacters` | `4096` | Maximum Identity Characters. |
| `maximumPromptArguments` | `32` | Maximum Prompt Arguments. |
| `maximumSchemaCharacters` | `65536` | Maximum Schema Characters. |
| `maximumResponseBytes` | `1048576` | Maximum Response Bytes. |
| `maximumLineBytes` | `1048576` | Maximum Line Bytes. |
| `maximumNameCharacters` | `256` | Maximum Name Characters. |
| `maximumArgumentNameCharacters` | `128` | Maximum Argument Name Characters. |
| `maximumArgumentDescriptionCharacters` | `1024` | Maximum prompt argument description characters, in transport mapping and inspection. |
| `maximumProfileIdCharacters` | `128` | Maximum Profile Id Characters. |
| `maximumCommandCharacters` | `4096` | Maximum Command Characters. |
| `maximumProfileArguments` | `64` | Maximum Profile Arguments. |
| `maximumProfileArgumentCharacters` | `8192` | Maximum Profile Argument Characters. |
| `maximumEnvironmentVariables` | `64` | Maximum Environment Variables. |
| `maximumEnvironmentValueCharacters` | `32768` | Maximum Environment Value Characters. |
| `maximumHeaders` | `64` | Maximum Headers. |
| `maximumSecretReferences` | `64` | Maximum Secret References. |
| `maximumSecretReferenceCharacters` | `512` | Maximum Secret Reference Characters. |
| `maximumScopes` | `64` | Maximum Scopes. |
| `maximumScopeCharacters` | `256` | Maximum Scope Characters. |
| `maximumClientIdCharacters` | `1024` | Maximum Client Id Characters. |
| `maximumStandardErrorLineCharacters` | `8192` | Maximum Standard Error Line Characters. |
| `maximumOAuthAuthorizationServers` | `4` | Maximum advertised OAuth authorization servers. |
| `maximumOAuthMetadataBytes` | `65536` | Maximum OAuth metadata or identity-response bytes; at most `Array.MaxLength`. |
| `maximumCallbackHeaderBytes` | `32768` | Maximum loopback OAuth request-header bytes. |
| `maximumCallbackHeaders` | `64` | Maximum loopback OAuth request headers. |
| `maximumCallbackLineBytes` | `8192` | Maximum loopback OAuth request-line bytes; at most `Array.MaxLength`. |

### `hooks:limits`

Trusted machine/user/environment configuration. [Implementation](../../src/Threadsmith.Hooks/HookResourceLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumHandlers` | `64` | Maximum configured hook handlers. |
| `maximumHookPoints` | `16` | Maximum declared points per handler. |
| `maximumAggregateTimeoutMilliseconds` | `120000` | Maximum sum of enabled hook timeouts including retries. |
| `maximumTargetCharacters` | `2048` | Maximum hook target characters. |
| `maximumIdCharacters` | `128` | Maximum handler identifier characters. |
| `maximumVersionCharacters` | `64` | Maximum version characters. |
| `maximumSecretReferences` | `16` | Maximum secret references per handler. |
| `maximumSecretReferenceCharacters` | `128` | Maximum secret reference characters. |
| `maximumFindings` | `32` | Maximum advice findings. |
| `maximumFindingCharacters` | `1024` | Maximum advice or failure explanation characters. |
| `maximumCodeCharacters` | `64` | Maximum failure/denial code characters. |
| `maximumPayloadEntries` | `32` | Maximum lifecycle metadata entries. |
| `maximumPayloadKeyCharacters` | `64` | Maximum lifecycle metadata key characters. |
| `maximumPayloadValueCharacters` | `2048` | Maximum lifecycle metadata value characters. |

### `secretResolution:limits`

Trusted machine/user/environment configuration. [Implementation](../../src/Threadsmith.Tools/SecretResourceLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumStoreBytes` | `65536` | Maximum bytes in a JSON secret store. |
| `maximumProperties` | `512` | Maximum total JSON secret-store properties. |
| `maximumJsonDepth` | `16` | Maximum JSON secret-store nesting depth. Validation is iterative so raising this does not consume the process call stack. |
| `maximumValueCharacters` | `65536` | Maximum resolved secret-value characters, for every provider. |
| `providerTimeoutMilliseconds` | `5000` | Default deadline for each secret-provider attempt. Explicit request deadlines must fit the runtime timer range (at most 4,294,967,294 ms). |

### `model:catalogLimits`

Trusted machine/user/environment configuration. [Implementation](../../src/Threadsmith.Models/ModelProviderConfiguration.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumFileBytes` | `1048576` | Maximum bytes accepted from one catalog file. |
| `maximumDepth` | `32` | Maximum JSON nesting depth. |
| `maximumProviders` | `32` | Maximum provider count. |
| `maximumModelsPerProvider` | `128` | Maximum models beneath one provider. |
| `maximumModels` | `512` | Maximum aggregate model count. |
| `maximumStringLength` | `4096` | Maximum length of any JSON string or property name. |
| `maximumPropertiesPerObject` | `128` | Maximum properties accepted in one JSON object. |

### `skills:catalogLimits`

Trusted machine/user/environment ceilings; repository settings may narrow them. [Implementation](../../src/Threadsmith.Skills/SkillCatalog.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumManifestBytes` | `262144` | Maximum manifest bytes. |
| `maximumPackages` | `512` | Maximum packages across all sources. |
| `maximumAssetsPerPackage` | `64` | Maximum assets declared by one package. |
| `maximumAssetBytes` | `4194304` | Maximum bytes in one declared asset. |
| `maximumTextCharacters` | `4096` | Maximum metadata text length. |
| `maximumRequiredTools` | `64` | Maximum required or optional tools. |
| `maximumContractVersions` | `128` | Maximum required tool contracts. |
| `maximumApprovalCategories` | `32` | Maximum approval disclosures. |
| `maximumWorkloads` | `16` | Maximum model workloads. |
| `maximumModelProfiles` | `128` | Maximum allowed or denied profiles. |
| `maximumIdentifierCharacters` | `128` | Maximum requirement identifiers. |
| `maximumVersionCharacters` | `64` | Maximum contract version or workload-name characters. |
| `maximumPathCharacters` | `512` | Maximum package-relative asset path characters. |

### `skills:schemaLimits`

Trusted machine/user/environment ceilings; repository settings may narrow them. [Implementation](../../src/Threadsmith.Skills/BoundedJsonSchema.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumSchemaBytes` | `131072` | Maximum schema bytes. |
| `maximumValueBytes` | `1048576` | Maximum value bytes. |
| `maximumDepth` | `16` | Maximum schema/value depth; values above the original 64-level recursive-validation ceiling are rejected to prevent stack overflow. |
| `maximumProperties` | `256` | Maximum object properties across a schema. |
| `maximumArrayItems` | `1024` | Maximum array items. |
| `maximumPropertyNameCharacters` | `128` | Maximum schema property-name characters. |
| `maximumEnumValues` | `256` | Maximum enum members. |
| `maximumMetadataCharacters` | `4096` | Maximum schema title/description characters. |

### `skills:claudeLimits`

Trusted machine/user/environment ceilings; repository settings may narrow them. [Implementation](../../src/Threadsmith.Skills/ClaudeSkillCompatibilityCatalog.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumRoots` | `16` | Maximum configured roots. |
| `maximumCandidates` | `256` | Maximum candidates. |
| `maximumFrontmatterBytes` | `32768` | Maximum frontmatter bytes read during discovery. |
| `maximumInstructionBytes` | `262144` | Maximum SKILL.md bytes loaded on activation. |
| `maximumFiles` | `64` | Maximum files eligible for one immutable snapshot. |
| `maximumAggregateBytes` | `1048576` | Maximum aggregate eligible bytes. |

### `skills:installerLimits`

Trusted machine/user/environment ceilings; repository settings may narrow them. [Implementation](../../src/Threadsmith.Skills/SkillPackageSecurity.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumArchiveBytes` | `16777216` | Maximum archive bytes. |
| `maximumExtractedBytes` | `67108864` | Maximum extracted bytes. |
| `maximumFiles` | `256` | Maximum archive entries. |

### `skills:runtimeLimits`

Trusted machine/user/environment configuration. [Implementation](../../src/Threadsmith.Skills/SkillRuntimeLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumInputCharacters` | `1048576` | Maximum invocation input JSON characters. |
| `maximumSelectorCharacters` | `1024` | Maximum invocation selector characters. |
| `maximumModelOutputCharacters` | `1048576` | Maximum accumulated model procedure output characters. |

### `tools:config:memories`

Ordinary configuration; repository rebinding supported. [Implementation](../../src/Threadsmith.Core/ManagedMemoryContracts.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maxNumberOfRepoMemories` | `20` | Maximum number of stored entries, shared by manual and model origins. |
| `maxRepoMemoriesInContext` | `3` | Maximum number of relevant entries injected into automatic context. |
| `standingPreferenceWarningThreshold` | `3` | Committed standing-preference count at which callers may warn about accumulating durable guidance. |
| `semanticMinimum` | `0.47` | Strict minimum cosine similarity for semantic matches; lexical matches qualify independently. |
| `rerankerCandidateLimit` | `8` | Maximum qualified candidates sent to the local reranker. |
| `maximumTextCharacters` | `2000` | Maximum complete sanitized memory characters. |
| `maximumQueryCharacters` | `8000` | Maximum retrieval query characters. |
| `maximumQueryTerms` | `32` | Maximum distinct lexical query terms. |
| `maximumCacheEntries` | `64` | Maximum entries in each retrieval cache. |
| `maximumDiagnostics` | `64` | Maximum retrieval diagnostics retained. |
| `maximumListBytes` | `49152` | Maximum serialized entry bytes in a memories tool list. |

### `tools:runtime:presentation`

Ordinary configuration. [Implementation](../../src/Threadsmith.Tools/ToolRuntimeOptions.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumActivityDetailCharacters` | `240` | Maximum compact tool activity characters. |
| `maximumTransientActivityDetailCharacters` | `8192` | Maximum transient activity-detail characters. |
| `maximumPreflightReasonCharacters` | `512` | Maximum preflight diagnostic characters. |

### `context:promptAppends:limits`

Trusted machine/user/environment ceilings; repository settings may narrow them. The effective per-file bound also cannot exceed the aggregate bound. [Implementation](../../src/Threadsmith.Context/PromptAppendLoader.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumFileBytes` | `32768` | Maximum bytes per prompt append file. |
| `maximumTotalBytes` | `65536` | Maximum aggregate prompt append bytes. |

### `context:instructions:limits`

Ordinary configuration. [Implementation](../../src/Threadsmith.Context/RepositoryInstructionResolver.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumDepth` | `32` | Maximum hierarchical instruction discovery depth. |
| `maximumFiles` | `32` | Maximum AGENTS.md files. |
| `maximumFileBytes` | `65536` | Maximum bytes per instruction file. |
| `maximumTotalBytes` | `262144` | Maximum aggregate instruction bytes. |

### `context:deployedPrompts:limits`

Trusted machine/user/environment configuration only. Repository, session, and CLI overrides cannot change admission of the host's shipped prompt assets. [Implementation](../../src/Threadsmith.Context/DeployedPromptLoader.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumFileBytes` | `131072` | Maximum bytes per shipped prompt asset. |
| `maximumCatalogBytes` | `4194304` | Maximum aggregate shipped prompt bytes. |

### `semanticRefresh:limits`

Ordinary configuration. [Implementation](../../src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumPendingPaths` | `1024` | Maximum pending filesystem paths before coalescing to broader refresh. |
| `maximumRecentHostEchoIdentities` | `1024` | Maximum remembered host mutation identities. |
| `maximumSafeReasonLength` | `256` | Maximum failure reason characters. |
| `maximumStableFileReadAttempts` | `2` | Maximum attempts to read a file without concurrent modification. |

### `execution`

Ordinary configuration. [Implementation](../../src/Threadsmith.Execution/ExecutionLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maxRetainedToolCalls` | `256` | Conversation tool-call count; zero disables this cap. |
| `maxSourceFrontierEntries` | `256` | Maximum model-visible source references. |
| `maxPlanSanityIssues` | `32` | Maximum plan sanity findings. |
| `maxSteeringCharacters` | `100000` | Maximum steering input characters. |
| `maxAgentDisplayFragments` | `256` | Maximum pending child display fragments. |
| `maxAgentDisplayFragmentCharacters` | `4096` | Maximum characters per display fragment. |
| `maxAgentDisplayLineCharacters` | `16384` | Maximum characters per child sanitizer line. |

### `agents:delegation`

Ordinary configuration. [Implementation](../../src/Threadsmith.Execution/DelegateAgentsContracts.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumDisagreements` | `8` | Maximum disagreement summaries; zero disables this bound. |
| `maximumDisagreementSubjectCharacters` | `256` | Maximum characters per disagreement subject; zero disables this bound. |

### `events`

Trusted-only machine/user/environment configuration. Repository, session, and CLI settings cannot shorten durable event delivery. [Implementation](../../src/Threadsmith.Execution/DomainEventStream.cs).

| Field | Default | Purpose |
|---|---:|---|
| `committedDeliveryTimeoutMilliseconds` | `5000` | Positive deadline for committed event delivery. Expiry removes a subscriber; choose a value that accommodates persistence latency. |

### `embeddings`

Ordinary configuration. [Implementation](../../src/Threadsmith.Embeddings.Local/LocalTextEmbeddingGenerator.cs).

| Field | Default | Purpose |
|---|---:|---|
| `disposalTimeoutMilliseconds` | `10000` | Maximum wait for active inference during shutdown. |

### `reranking`

Ordinary configuration. [Implementation](../../src/Threadsmith.Reranking.Local/LocalTextCrossEncoder.cs).

| Field | Default | Purpose |
|---|---:|---|
| `cpuThreads` | `8` | Requested positive CPU inference thread count; no artificial 32-thread cap. |
| `disposalTimeoutMilliseconds` | `10000` | Maximum wait for active inference during shutdown. |

### `extensions`

Ordinary configuration. [Implementation](../../src/Threadsmith.Extensions.Runtime/ShadowCopier.cs).

| Field | Default | Purpose |
|---|---:|---|
| `stabilityQuietPeriodMilliseconds` | `250` | Minimum quiet interval before shadow copying. |
| `stabilityTimeoutMilliseconds` | `30000` | Maximum wait for a stable extension package. |

### `tools:writeFile`

Ordinary configuration. [Implementation](../../src/Threadsmith.Tools/WriteFileTool.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maxContentBytes` | `1048576` | Maximum write_file content bytes. |
| `maxPathCharacters` | `4096` | Maximum write_file path characters; path authorization still applies. |

### `tools:semantic`

Ordinary configuration. [Implementation](../../src/Threadsmith.Tools/ToolLimits.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maxModelResults` | `100` | Maximum model-facing results from legacy semantic tools. |

### `tools:search`

Ordinary configuration. [Implementation](../../src/Threadsmith.Tools/BuiltInTools.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maxQueryCharacters` | `500` | Maximum file-search query characters. |
| `regexTimeoutMilliseconds` | `250` | Per-match regex timeout; at most 2,147,483,646 milliseconds, the regex engine's finite timeout ceiling. |
| `processTimeoutMilliseconds` | `25000` | Ripgrep process deadline. |

### `tools:config:csharp_script`

Ordinary configuration. [Implementation](../../src/Threadsmith.Tools/NewBuiltInTools.cs).

| Field | Default | Purpose |
|---|---:|---|
| `timeout_ms` | `5000` | Worker execution deadline in milliseconds. |
| `max_output_bytes` | `65536` | Maximum worker output bytes. |
| `max_code_characters` | `65536` | Maximum submitted C# code characters. |
| `max_assemblies` | `32` | Maximum allowed assembly entries. |
| `max_assembly_name_characters` | `128` | Maximum assembly-name characters. |
| `max_assembly_setting_characters` | `4096` | Maximum allowed-assemblies setting characters. |

### `webFetch`

Trusted ceiling; repository settings can narrow it. [Implementation](../../src/Threadsmith.Tools/WebFetch.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumHtmlTokens` | `100000` | Maximum parsed HTML tokens. |
| `maximumHtmlDepth` | `128` | Maximum HTML nesting depth. |
| `maximumJsonTokens` | `100000` | Maximum parsed JSON tokens. |
| `maximumJsonDepth` | `64` | Maximum JSON nesting depth. |
| `maximumStringCharacters` | `131072` | Maximum parsed string characters. |
| `referenceLifetimeSeconds` | `600` | Lifetime of issued retrieval references. |
| `maximumScannedUserCharacters` | `32768` | Maximum user message characters scanned for exact URLs. |
| `maximumUserUrlCandidates` | `8` | Maximum candidate URLs recognized. |
| `maximumReferences` | `100` | Maximum remembered retrieval references. |

### `webSearch:provider`

Trusted provider configuration. [Implementation](../../src/Threadsmith.Tools/WebSearch.cs).

| Field | Default | Purpose |
|---|---:|---|
| `maximumQueryCharacters` | `500` | Maximum query characters; cannot exceed the provider 600-character limit. |
| `maximumJsonDepth` | `16` | Maximum provider response JSON depth. |
| `maximumFreshnessDays` | `365` | Maximum requested freshness in days; cannot exceed the days between the current UTC date and `DateOnly.MinValue`. |
| `maximumTitleCharacters` | `300` | Maximum result title characters. |
| `maximumSnippetCharacters` | `1000` | Maximum result snippet characters. |

## Anthropic provider limits

Put `resourceLimits` on the Anthropic provider entry in `~/.threadsmith/providers.json`, alongside `baseUrl` and `models`; it is not a top-level application configuration section. Stream frame, block, replay, metadata, and discovery limits are shared consistently by provider construction and catalog refresh.

```json
"resourceLimits": {
  "maximumToolCallIdCharacters": 256,
  "maximumSseFrameBytes": 1048576,
  "maximumContentBlocks": 4096,
  "maximumReplayBytes": 8388608,
  "maximumMetadataBytes": 1048576,
  "maximumDiscoveredModels": 128,
  "maximumDiscoveryPages": 10,
  "discoveryTimeoutMilliseconds": 15000,
  "metadataCacheLifetimeSeconds": 86400
}
```

## Existing settings with removed artificial ceilings

- `tools.readFile.defaultLines`, `maxLines`, and `maxContentBytes` can exceed the old 2,000-line / 50 KiB maxima. `tools.readFile.maxBytes` still controls the input file size.
- `tools.config.memories.rerankerCandidateLimit` accepts any positive count. The provider processes candidates in its supported batches; actual model token/shape limits remain in force.
- `repository.configurationBytes` (default 1 MiB, trusted machine/user/environment setting) is used by bootstrap and subsequent model, theme, allowed-host, and repository preference readers/writers.
- `mcp.maximumConcurrentConnections` defaults to 4 with no artificial maximum of 16. MCP profile startup/request/shutdown timeouts require positive values without the previous 30-minute / 5-minute caps.
- Model HTTP pool connections and lifetime/idle settings, hook handler budgets, script budgets, skill package/workflow budgets, and web fetch/search resource settings retain their existing configuration locations. Former arbitrary upper validation ceilings have been removed; positive values and meaningful cross-field relationships still apply.
- Session and hook-audit page counts are caller-supplied positive request values; they no longer have hidden 500/1,000-item ceilings.

## Limits intentionally retained

These are not general resource-capacity knobs:

- **Model and API contracts:** local embedding vector dimensions and model token/batch shapes; provider context/output capabilities; OpenAI-family wire tool names; supported protocol/schema versions. A configuration value cannot make an external model or API support another shape.
- **Brave search:** 600 query characters, 75 words, and 20 results follow the [Brave API request contract](https://api-dashboard.search.brave.com/api-reference/web/search/get). Locale syntax and supported codes are protocol validation.
- **Closed syntax and identity contracts:** enum members, recognized color/style syntax, canonical secret-reference grammar, schema identities, JSON duplicate/member validation, path confinement, and control-character rejection. These checks remain regardless of resource settings.
- **Hook attempt representation:** `maximumRetries` must be at most 2,147,483,646 so the one-based integer attempt field can represent the initial attempt plus every retry. Aggregate timeout validation uses wider arithmetic.
- **Serialized output representation:** C# script `max_output_bytes` cannot exceed 357,913,258 because JSON escaping plus envelope overhead must fit the process capture's 32-bit character count. Web-fetch URL, redirect, and extracted-content limits must together fit the 32-bit serialized tool-output byte count. Invalid combinations fail explicitly before execution.
- **Recursive Markdown traversal:** nesting is configurable from 1 through 32, preserving the original recursive renderer's safety ceiling. This prevents an unrecoverable stack overflow; deeper content falls back to escaped source. Raising the ceiling requires an iterative implementation, not a configuration override.
- **Algorithms and layout:** relevance/ranking thresholds, fuzzy edit distance, diversity sampling, generated-source classification probes, viewport row counts, Markdown heading levels, and fixed synchronization/coalescing channel capacities. These define behavior or screen layout rather than rejecting a repository for its size.
- **Cancellation cleanup:** the short noncooperative Roslyn-compilation backstop remains an internal cancellation mechanism, separate from the configured normal query deadline. Platform interop buffer dimensions and ordinary I/O block sizes remain implementation details.

The removed semantic-load file-count and aggregate 64 MiB restrictions have **not** been reintroduced. Fallback-search limits and transactional baseline capture are separate operations with explicitly named settings.

Semantic loading and refresh therefore have no fixed repository-size resource ceiling. Large contributor-controlled project inputs can consume substantial memory, I/O, and watcher resources even with read-only trust; trust controls execution authority, not a resource sandbox. This is the intended capacity policy, rather than an assurance that arbitrary repositories have bounded resource costs.

Diagnostic query pages use the smaller of `diagnosticPageSize` and `maximumModelDiagnostics`, preserving continuation across all results. Advisory projections report omitted advisories. Inventory projections that cannot fit even their summarized envelope return a bounded omission notice.
