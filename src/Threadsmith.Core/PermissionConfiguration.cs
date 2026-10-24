namespace Threadsmith.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Permission-bearing configuration properties. Unlisted ordinary preferences remain owned by their existing contracts.</summary>
public sealed record PermissionConfiguration
{
    /// <summary>Explicitly versioned ownership; absence preserves legacy interpretation until consent.</summary>
    [JsonPropertyName("configurationOwnershipVersion")]
    [ConfigurationPermission(Shape = PermissionInputShape.Scalar, Sources = PermissionConfigurationSources.Machine | PermissionConfigurationSources.User)]
    public int? ConfigurationOwnershipVersion { get; init; }

    /// <summary>Machine-enforced restrictions, separate from overridable defaults.</summary>
    [JsonPropertyName("enforced")]
    [ConfigurationPermission(Sources = PermissionConfigurationSources.Machine)]
    public JsonElement? Enforced { get; init; }

    /// <summary>Configuration contract for tools.</summary>
    [JsonPropertyName("tools")]
    public ToolsSettings? Tools { get; init; }

    /// <summary>Provider transport and catalog safety settings.</summary>
    [JsonPropertyName("model")]
    public ModelSettings? Model { get; init; }

    /// <summary>Trusted model transport configuration; model selection remains a preference.</summary>
    public sealed record ModelSettings
    {
        /// <summary>Gets catalog resource bounds.</summary>
        [JsonPropertyName("catalogLimits")]
        [ConfigurationPermission(AdapterOwned = true)]
        public JsonElement? CatalogLimits { get; init; }

        /// <summary>Gets HTTPS enforcement for model endpoints.</summary>
        [JsonPropertyName("enforceModelEndpointHttps")]
        [ConfigurationPermission(AdapterOwned = true)]
        public bool? EnforceModelEndpointHttps { get; init; }

        /// <summary>Gets trusted native Codex transport options.</summary>
        [JsonPropertyName("openAiCodex")]
        public CodexSettings? OpenAiCodex { get; init; }
    }

    /// <summary>Native provider transport settings.</summary>
    public sealed record CodexSettings
    {
        /// <summary>Gets the trusted request deadline.</summary>
        [JsonPropertyName("timeoutSeconds")]
        [ConfigurationPermission(AdapterOwned = true)]
        public int? TimeoutSeconds { get; init; }
    }

    /// <summary>Configuration contract for editableRoots.</summary>
    [JsonPropertyName("editableRoots")]
    [ConfigurationPermission(Shape = PermissionInputShape.List)]
    public string[]? EditableRoots { get; init; }

    /// <summary>Configuration contract for prohibitedPaths.</summary>
    [JsonPropertyName("prohibitedPaths")]
    [ConfigurationPermission(Shape = PermissionInputShape.List)]
    public string[]? ProhibitedPaths { get; init; }

    /// <summary>Configuration contract for mutation.</summary>
    [JsonPropertyName("mutation")]
    public MutationSettings? Mutation { get; init; }

    /// <summary>Configuration contract for repository.</summary>
    [JsonPropertyName("repository")]
    public RepositorySettings? Repository { get; init; }

    /// <summary>Configuration contract for secretResolution.</summary>
    [JsonPropertyName("secretResolution")]
    public SecretResolutionSettings? SecretResolution { get; init; }

    /// <summary>Configuration contract for limits.</summary>
    [JsonPropertyName("limits")]
    public LimitsSettings? Limits { get; init; }

    /// <summary>Configuration contract for context.</summary>
    [JsonPropertyName("context")]
    public ContextSettings? Context { get; init; }

    /// <summary>Configuration contract for mcp.</summary>
    [JsonPropertyName("mcp")]
    public McpSettings? Mcp { get; init; }

    /// <summary>Configuration contract for webSearch.</summary>
    [JsonPropertyName("webSearch")]
    public WebSearchSettings? WebSearch { get; init; }

    /// <summary>Configuration contract for nuget.</summary>
    [JsonPropertyName("nuget")]
    public NugetSettings? Nuget { get; init; }

    /// <summary>Configuration contract for hooks.</summary>
    [JsonPropertyName("hooks")]
    public HooksSettings? Hooks { get; init; }

    /// <summary>Configuration contract for skills.</summary>
    [JsonPropertyName("skills")]
    public SkillsSettings? Skills { get; init; }

    /// <summary>Configuration contract for agents.</summary>
    [JsonPropertyName("agents")]
    public AgentsSettings? Agents { get; init; }

    /// <summary>Configuration contract for webFetch.</summary>
    [JsonPropertyName("webFetch")]
    [ConfigurationPermission(Shape = PermissionInputShape.Subtree)]
    public WebFetchSettings? WebFetch { get; init; }

    /// <summary>Configuration shape for tools:writeFile; metadata owns source admission.</summary>
    public sealed record ToolsWriteFileSettings
    {
        /// <summary>Configuration contract for tools:writeFile:allowedFolders.</summary>
        [JsonPropertyName("allowedFolders")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? AllowedFolders { get; init; }
    }

    /// <summary>Configuration shape for tools:runProcess; metadata owns source admission.</summary>
    public sealed record ToolsRunProcessSettings
    {
        /// <summary>Configuration contract for tools:runProcess:requireApproval.</summary>
        [JsonPropertyName("requireApproval")]
        [ConfigurationPermission(Shape = PermissionInputShape.Boolean)]
        public bool? RequireApproval { get; init; }

        /// <summary>Configuration contract for tools:runProcess:defaultTimeoutSeconds.</summary>
        [JsonPropertyName("defaultTimeoutSeconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 30)]
        public int? DefaultTimeoutSeconds { get; init; }

        /// <summary>Configuration contract for tools:runProcess:maxTimeoutSeconds.</summary>
        [JsonPropertyName("maxTimeoutSeconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 60)]
        public int? MaxTimeoutSeconds { get; init; }
    }

    /// <summary>Configuration shape for tools:parallel; metadata owns source admission.</summary>
    public sealed record ToolsParallelSettings
    {
        /// <summary>Configuration contract for tools:parallel:enabled.</summary>
        [JsonPropertyName("enabled")]
        [ConfigurationPermission(Shape = PermissionInputShape.Boolean, StartupBound = true)]
        public bool? Enabled { get; init; }

        /// <summary>Configuration contract for tools:parallel:maximumConcurrency.</summary>
        [JsonPropertyName("maximumConcurrency")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 4, StartupBound = true)]
        public int? MaximumConcurrency { get; init; }
    }

    /// <summary>Configuration shape for tools:config:csharp_script; metadata owns source admission.</summary>
    public sealed record ToolsConfigCsharpScriptSettings
    {
        /// <summary>Configuration contract for tools:config:csharp_script:allowed_assemblies.</summary>
        [JsonPropertyName("allowed_assemblies")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar)]
        public string? AllowedAssemblies { get; init; }

        /// <summary>Configuration contract for tools:config:csharp_script:timeout_ms.</summary>
        [JsonPropertyName("timeout_ms")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 5000)]
        public int? TimeoutMs { get; init; }

        /// <summary>Configuration contract for tools:config:csharp_script:max_output_bytes.</summary>
        [JsonPropertyName("max_output_bytes")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 65536)]
        public int? MaxOutputBytes { get; init; }

        /// <summary>Configuration contract for tools:config:csharp_script:max_code_characters.</summary>
        [JsonPropertyName("max_code_characters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 65536)]
        public int? MaxCodeCharacters { get; init; }

        /// <summary>Configuration contract for tools:config:csharp_script:max_assemblies.</summary>
        [JsonPropertyName("max_assemblies")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 32)]
        public int? MaxAssemblies { get; init; }

        /// <summary>Configuration contract for tools:config:csharp_script:max_assembly_name_characters.</summary>
        [JsonPropertyName("max_assembly_name_characters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 128)]
        public int? MaxAssemblyNameCharacters { get; init; }

        /// <summary>Configuration contract for tools:config:csharp_script:max_assembly_setting_characters.</summary>
        [JsonPropertyName("max_assembly_setting_characters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 4096)]
        public int? MaxAssemblySettingCharacters { get; init; }
    }

    /// <summary>Configuration shape for tools:config; metadata owns source admission.</summary>
    public sealed record ToolsConfigSettings
    {
        /// <summary>Configuration contract for tools:config:csharp_script.</summary>
        [JsonPropertyName("csharp_script")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree)]
        public ToolsConfigCsharpScriptSettings? CsharpScript { get; init; }
    }

    /// <summary>Configuration shape for tools:runtime:defaults; metadata owns source admission.</summary>
    public sealed record ToolsRuntimeDefaultsSettings
    {
        /// <summary>Configuration contract for tools:runtime:defaults:timeoutMilliseconds.</summary>
        [JsonPropertyName("timeoutMilliseconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = -1)]
        public int? TimeoutMilliseconds { get; init; }

        /// <summary>Configuration contract for tools:runtime:defaults:maximumOutputBytes.</summary>
        [JsonPropertyName("maximumOutputBytes")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = int.MaxValue)]
        public int? MaximumOutputBytes { get; init; }

        /// <summary>Configuration contract for tools:runtime:defaults:maximumSourceConcurrency.</summary>
        [JsonPropertyName("maximumSourceConcurrency")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = int.MaxValue)]
        public int? MaximumSourceConcurrency { get; init; }
    }

    /// <summary>Configuration shape for tools:runtime; metadata owns source admission.</summary>
    public sealed record ToolsRuntimeSettings
    {
        /// <summary>Configuration contract for tools:runtime:defaults.</summary>
        [JsonPropertyName("defaults")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, StartupBound = true)]
        public ToolsRuntimeDefaultsSettings? Defaults { get; init; }

        /// <summary>Configuration contract for tools:runtime:byTool.</summary>
        [JsonPropertyName("byTool")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, StartupBound = true)]
        public JsonElement? ByTool { get; init; }
    }

    /// <summary>Configuration shape for tools; metadata owns source admission.</summary>
    public sealed record ToolsSettings
    {
        /// <summary>Gets trusted Jira providers.</summary>
        [JsonPropertyName("jira")]
        public ProviderSettings? Jira { get; init; }

        /// <summary>Gets trusted pull-request providers.</summary>
        [JsonPropertyName("pr")]
        public ProviderSettings? Pr { get; init; }

        /// <summary>Configuration contract for tools:allowedExecutables.</summary>
        [JsonPropertyName("allowedExecutables")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? AllowedExecutables { get; init; }

        /// <summary>Configuration contract for tools:allowedNetworkHosts.</summary>
        [JsonPropertyName("allowedNetworkHosts")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? AllowedNetworkHosts { get; init; }

        /// <summary>Configuration contract for tools:allow.</summary>
        [JsonPropertyName("allow")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? Allow { get; init; }

        /// <summary>Configuration contract for tools:deny.</summary>
        [JsonPropertyName("deny")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? Deny { get; init; }

        /// <summary>Configuration contract for tools:requireApproval.</summary>
        [JsonPropertyName("requireApproval")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? RequireApproval { get; init; }

        /// <summary>Configuration contract for tools:writeFile.</summary>
        [JsonPropertyName("writeFile")]
        public ToolsWriteFileSettings? WriteFile { get; init; }

        /// <summary>Configuration contract for tools:runProcess.</summary>
        [JsonPropertyName("runProcess")]
        public ToolsRunProcessSettings? RunProcess { get; init; }

        /// <summary>Configuration contract for tools:parallel.</summary>
        [JsonPropertyName("parallel")]
        public ToolsParallelSettings? Parallel { get; init; }

        /// <summary>Configuration contract for tools:config.</summary>
        [JsonPropertyName("config")]
        public ToolsConfigSettings? Config { get; init; }

        /// <summary>Configuration contract for tools:runtime.</summary>
        [JsonPropertyName("runtime")]
        public ToolsRuntimeSettings? Runtime { get; init; }
    }

    /// <summary>Trusted integration provider definitions.</summary>
    public sealed record ProviderSettings
    {
        /// <summary>Gets provider connection definitions.</summary>
        [JsonPropertyName("providers")]
        [ConfigurationPermission(AdapterOwned = true)]
        public JsonElement? Providers { get; init; }
    }

    /// <summary>Configuration shape for mutation; metadata owns source admission.</summary>
    public sealed record MutationSettings
    {
        /// <summary>Configuration contract for mutation:approvalPolicy.</summary>
        [JsonPropertyName("approvalPolicy")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar)]
        public string? ApprovalPolicy { get; init; }

        /// <summary>Configuration contract for mutation:largeDiffThreshold.</summary>
        [JsonPropertyName("largeDiffThreshold")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar)]
        public string? LargeDiffThreshold { get; init; }
    }

    /// <summary>Configuration shape for repository; metadata owns source admission.</summary>
    public sealed record RepositorySettings
    {
        /// <summary>Explicit host-owned trust for this local repository.</summary>
        [JsonPropertyName("trust")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, Sources = PermissionConfigurationSources.UserRepository)]
        public string? Trust { get; init; }

        /// <summary>Gets the timestamp of the explicit local trust choice.</summary>
        [JsonPropertyName("trustGrantedAt")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, Sources = PermissionConfigurationSources.UserRepository)]
        public string? TrustGrantedAt { get; init; }

        /// <summary>Configuration contract for repository:configurationBytes.</summary>
        [JsonPropertyName("configurationBytes")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, AdapterOwned = true)]
        public string? ConfigurationBytes { get; init; }
    }

    /// <summary>Configuration shape for secretResolution; metadata owns source admission.</summary>
    public sealed record SecretResolutionSettings
    {
        /// <summary>Configuration contract for secretResolution:limits.</summary>
        [JsonPropertyName("limits")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Limits { get; init; }
    }

    /// <summary>Configuration shape for limits; metadata owns source admission.</summary>
    public sealed record LimitsSettings
    {
        /// <summary>Configuration contract for limits:policyStores.</summary>
        [JsonPropertyName("policyStores")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? PolicyStores { get; init; }
    }

    /// <summary>Configuration shape for context:deployedPrompts; metadata owns source admission.</summary>
    public sealed record ContextDeployedPromptsSettings
    {
        /// <summary>Configuration contract for context:deployedPrompts:limits.</summary>
        [JsonPropertyName("limits")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Limits { get; init; }
    }

    /// <summary>Configuration shape for context; metadata owns source admission.</summary>
    public sealed record ContextSettings
    {
        /// <summary>Configuration contract for context:deployedPrompts.</summary>
        [JsonPropertyName("deployedPrompts")]
        public ContextDeployedPromptsSettings? DeployedPrompts { get; init; }
    }

    /// <summary>Configuration shape for mcp; metadata owns source admission.</summary>
    public sealed record McpSettings
    {
        /// <summary>Configuration contract for mcp:profiles.</summary>
        [JsonPropertyName("profiles")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Profiles { get; init; }

        /// <summary>Configuration contract for mcp:limits.</summary>
        [JsonPropertyName("limits")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Limits { get; init; }

        /// <summary>Configuration contract for mcp:maximumConcurrentConnections.</summary>
        [JsonPropertyName("maximumConcurrentConnections")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? MaximumConcurrentConnections { get; init; }

        /// <summary>Configuration contract for mcp:defaultDrainKillTimeoutSeconds.</summary>
        [JsonPropertyName("defaultDrainKillTimeoutSeconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? DefaultDrainKillTimeoutSeconds { get; init; }
    }

    /// <summary>Configuration shape for webSearch; metadata owns source admission.</summary>
    public sealed record WebSearchSettings
    {
        /// <summary>Configuration contract for webSearch:provider.</summary>
        [JsonPropertyName("provider")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Provider { get; init; }
    }

    /// <summary>Configuration shape for nuget; metadata owns source admission.</summary>
    public sealed record NugetSettings
    {
        /// <summary>Configuration contract for nuget:advisorySources.</summary>
        [JsonPropertyName("advisorySources")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? AdvisorySources { get; init; }
    }

    /// <summary>Configuration shape for hooks; metadata owns source admission.</summary>
    public sealed record HooksSettings
    {
        /// <summary>Configuration contract for hooks:handlers.</summary>
        [JsonPropertyName("handlers")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Handlers { get; init; }

        /// <summary>Configuration contract for hooks:managedGrants.</summary>
        [JsonPropertyName("managedGrants")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? ManagedGrants { get; init; }

        /// <summary>Configuration contract for hooks:limits.</summary>
        [JsonPropertyName("limits")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Limits { get; init; }
    }

    /// <summary>Configuration shape for skills; metadata owns source admission.</summary>
    public sealed record SkillsSettings
    {
        /// <summary>Configuration contract for skills:trustedSigners.</summary>
        [JsonPropertyName("trustedSigners")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? TrustedSigners { get; init; }

        /// <summary>Configuration contract for skills:allowlistedPackages.</summary>
        [JsonPropertyName("allowlistedPackages")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? AllowlistedPackages { get; init; }

        /// <summary>Configuration contract for skills:enabledSelectors.</summary>
        [JsonPropertyName("enabledSelectors")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? EnabledSelectors { get; init; }

        /// <summary>Configuration contract for skills:revokedDigests.</summary>
        [JsonPropertyName("revokedDigests")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? RevokedDigests { get; init; }

        /// <summary>Configuration contract for skills:deniedSkillIds.</summary>
        [JsonPropertyName("deniedSkillIds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? DeniedSkillIds { get; init; }

        /// <summary>Configuration contract for skills:deniedPublishers.</summary>
        [JsonPropertyName("deniedPublishers")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? DeniedPublishers { get; init; }

        /// <summary>Configuration contract for skills:revokedSigners.</summary>
        [JsonPropertyName("revokedSigners")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? RevokedSigners { get; init; }

        /// <summary>Configuration contract for skills:organizationCatalogPath.</summary>
        [JsonPropertyName("organizationCatalogPath")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? OrganizationCatalogPath { get; init; }
    }

    /// <summary>Configuration shape for agents; metadata owns source admission.</summary>
    public sealed record AgentsSettings
    {
        /// <summary>Configuration contract for agents:delegation.</summary>
        [JsonPropertyName("delegation")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? Delegation { get; init; }

        /// <summary>Configuration contract for agents:queueCapacity.</summary>
        [JsonPropertyName("queueCapacity")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? QueueCapacity { get; init; }

        /// <summary>Configuration contract for agents:maxActiveGlobal.</summary>
        [JsonPropertyName("maxActiveGlobal")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? MaxActiveGlobal { get; init; }

        /// <summary>Configuration contract for agents:maxActivePerParent.</summary>
        [JsonPropertyName("maxActivePerParent")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? MaxActivePerParent { get; init; }

        /// <summary>Configuration contract for agents:maxActiveImplementers.</summary>
        [JsonPropertyName("maxActiveImplementers")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? MaxActiveImplementers { get; init; }

        /// <summary>Configuration contract for agents:shutdownTimeoutSeconds.</summary>
        [JsonPropertyName("shutdownTimeoutSeconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? ShutdownTimeoutSeconds { get; init; }

        /// <summary>Configuration contract for agents:delegateAgents.</summary>
        [JsonPropertyName("delegateAgents")]
        [ConfigurationPermission(Shape = PermissionInputShape.Subtree, AdapterOwned = true)]
        public JsonElement? DelegateAgents { get; init; }
    }

    /// <summary>Configuration shape for webFetch; metadata owns source admission.</summary>
    public sealed record WebFetchSettings
    {
        /// <summary>Configuration contract for webFetch:maximumUrlCharacters.</summary>
        [JsonPropertyName("maximumUrlCharacters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 2048)]
        public int? MaximumUrlCharacters { get; init; }

        /// <summary>Configuration contract for webFetch:maximumRedirects.</summary>
        [JsonPropertyName("maximumRedirects")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 3)]
        public int? MaximumRedirects { get; init; }

        /// <summary>Configuration contract for webFetch:timeoutSeconds.</summary>
        [JsonPropertyName("timeoutSeconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 15)]
        public int? TimeoutSeconds { get; init; }

        /// <summary>Configuration contract for webFetch:maximumCompressedBytes.</summary>
        [JsonPropertyName("maximumCompressedBytes")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 1048576)]
        public int? MaximumCompressedBytes { get; init; }

        /// <summary>Configuration contract for webFetch:maximumDecodedBytes.</summary>
        [JsonPropertyName("maximumDecodedBytes")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 2097152)]
        public int? MaximumDecodedBytes { get; init; }

        /// <summary>Configuration contract for webFetch:maximumExtractedCharacters.</summary>
        [JsonPropertyName("maximumExtractedCharacters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 131072)]
        public int? MaximumExtractedCharacters { get; init; }

        /// <summary>Configuration contract for webFetch:maximumHtmlTokens.</summary>
        [JsonPropertyName("maximumHtmlTokens")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 100000)]
        public int? MaximumHtmlTokens { get; init; }

        /// <summary>Configuration contract for webFetch:maximumHtmlDepth.</summary>
        [JsonPropertyName("maximumHtmlDepth")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 128)]
        public int? MaximumHtmlDepth { get; init; }

        /// <summary>Configuration contract for webFetch:maximumJsonTokens.</summary>
        [JsonPropertyName("maximumJsonTokens")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 100000)]
        public int? MaximumJsonTokens { get; init; }

        /// <summary>Configuration contract for webFetch:maximumJsonDepth.</summary>
        [JsonPropertyName("maximumJsonDepth")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 64)]
        public int? MaximumJsonDepth { get; init; }

        /// <summary>Configuration contract for webFetch:maximumStringCharacters.</summary>
        [JsonPropertyName("maximumStringCharacters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 131072)]
        public int? MaximumStringCharacters { get; init; }

        /// <summary>Configuration contract for webFetch:maximumScannedUserCharacters.</summary>
        [JsonPropertyName("maximumScannedUserCharacters")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 32768)]
        public int? MaximumScannedUserCharacters { get; init; }

        /// <summary>Configuration contract for webFetch:maximumUserUrlCandidates.</summary>
        [JsonPropertyName("maximumUserUrlCandidates")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 8)]
        public int? MaximumUserUrlCandidates { get; init; }

        /// <summary>Configuration contract for webFetch:maximumReferences.</summary>
        [JsonPropertyName("maximumReferences")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 100)]
        public int? MaximumReferences { get; init; }

        /// <summary>Configuration contract for webFetch:referenceLifetimeSeconds.</summary>
        [JsonPropertyName("referenceLifetimeSeconds")]
        [ConfigurationPermission(Shape = PermissionInputShape.Scalar, DefaultLimit = 600)]
        public int? ReferenceLifetimeSeconds { get; init; }
    }
}
