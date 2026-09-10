namespace Threadsmith.Execution;

using System.Security;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Builds provider-neutral child messages without parent or sibling transcript content.</summary>
internal sealed class ChildAgentPrompt
{
    private readonly IPromptLoader _prompts;
    private readonly string _rolePromptFileName;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentPrompt"/> class.</summary>
    public ChildAgentPrompt(
        IPromptLoader prompts,
        AgentRole role = AgentRole.Explorer)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
        _rolePromptFileName = role switch
        {
            AgentRole.Explorer => PromptFileNames.SystemChildAgentExplorer,
            AgentRole.Implementer => PromptFileNames.SystemChildAgentImplementer,
            AgentRole.SecurityReviewer => PromptFileNames.SystemChildAgentSecurityReviewer,
            AgentRole.TestReviewer => PromptFileNames.SystemChildAgentTestReviewer,
            AgentRole.PerformanceReviewer => PromptFileNames.SystemChildAgentPerformanceReviewer,
            AgentRole.ArchitectureReviewer => PromptFileNames.SystemChildAgentArchitectureReviewer,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    /// <summary>Creates the initial instructions, assignment, and inherited evidence for one child.</summary>
    public List<ModelMessage> CreateMessages(
        AgentContextSnapshot context,
        RepositoryInstructionBundle instructions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(instructions);
        return
        [
            CreateMessage(ModelMessageRole.System, "child-host-policy", _prompts.Get(PromptFileNames.SystemChildAgentHostPolicy)),
            CreateMessage(ModelMessageRole.System, "child-role-amendment", _prompts.Get(_rolePromptFileName)),
            CreateMessage(
                ModelMessageRole.Developer,
                "child-repository-instructions",
                RenderInstructions(instructions)),
            CreateMessage(
                ModelMessageRole.User,
                "child-assignment",
                RenderAssignment(context)),
            CreateMessage(
                ModelMessageRole.User,
                "child-initial-evidence",
                RenderEvidence(context.Evidence)),
        ];
    }

    /// <summary>Creates exact model tool definitions from request-fenced registrations.</summary>
    public static IReadOnlyList<ModelToolDefinition> CreateToolDefinitions(
        IReadOnlyList<ToolRegistration> registrations)
    {
        return registrations.Select(registration => new ModelToolDefinition
        {
            Name = registration.Tool.Definition.Id,
            Description = registration.Tool.Definition.Description,
            ArgumentsJsonSchema = registration.Tool.Definition.InputSchema.JsonSchema,
            PreferStrictArguments = registration.Tool.Definition.PreferStrictArguments,
        }).ToArray();
    }

    /// <summary>Creates one normalized assistant tool-call message.</summary>
    public static ModelMessage CreateToolCallMessage(
        string toolCallId,
        ToolRequestModelOutput request,
        int? modelRound = null)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "child-tool-call",
            ToolCallId = toolCallId,
            ToolName = request.ToolName,
            ModelRound = modelRound,
            Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = request.ArgumentsJson }],
        };
    }

    /// <summary>Creates one normalized tool result message containing its exact evidence citation.</summary>
    public static ModelMessage CreateToolResultMessage(
        string toolCallId,
        string toolName,
        string content,
        int? modelRound = null,
        bool? isError = null)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = "child-tool-result",
            ToolCallId = toolCallId,
            ToolName = toolName,
            ModelRound = modelRound,
            IsError = isError,
            Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = content }],
        };
    }

    /// <summary>Creates technical tool-error feedback without constraining the child's answer format.</summary>
    public ModelMessage CreateCorrectionMessage(string reason)
    {
        return CreateMessage(
            ModelMessageRole.Developer,
            "child-tool-error",
            _prompts.Get(PromptFileNames.CorrectionToolBatchValidationUnavailable) + Environment.NewLine + reason);
    }

    /// <summary>Creates host-authored coverage guidance after one child tool batch.</summary>
    public ModelMessage CreateEvidenceProgressMessage(ChildAgentEvidenceProgress progress)
    {
        var promptFileName = progress.ExpandedAttributedCoverage
            ? PromptFileNames.ContextChildAgentEvidenceProgressExpanded
            : progress.AddedDistinctPayload
                ? PromptFileNames.ContextChildAgentEvidenceProgressPayloadOnly
                : PromptFileNames.ContextChildAgentEvidenceProgressNoProgress;
        var tokens = promptFileName switch
        {
            PromptFileNames.ContextChildAgentEvidenceProgressExpanded => Tokens(
                ("NewFiles", $"{progress.NewFiles}"),
                ("NewSources", $"{progress.NewSources}"),
                ("NewContentPayloads", $"{progress.NewContentPayloads}"),
                ("TotalFiles", $"{progress.TotalFiles}"),
                ("TotalSources", $"{progress.TotalSources}"),
                ("TotalEvidenceItems", $"{progress.TotalEvidenceItems}")),
            PromptFileNames.ContextChildAgentEvidenceProgressPayloadOnly => Tokens(
                ("NewContentPayloads", $"{progress.NewContentPayloads}"),
                ("TotalFiles", $"{progress.TotalFiles}"),
                ("TotalSources", $"{progress.TotalSources}"),
                ("TotalEvidenceItems", $"{progress.TotalEvidenceItems}")),
            _ => Tokens(
                ("TotalFiles", $"{progress.TotalFiles}"),
                ("TotalSources", $"{progress.TotalSources}"),
                ("TotalEvidenceItems", $"{progress.TotalEvidenceItems}")),
        };
        var guidance = _prompts.Render(promptFileName, tokens);
        return CreateMessage(
            ModelMessageRole.Developer,
            "child-evidence-progress",
            guidance);
    }

    /// <summary>Creates one lower-authority user steering message for a still-running child.</summary>
    public ModelMessage CreateSteeringMessage(RunSteeringMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = _prompts.Render(
            PromptFileNames.ContextChildAgentSteering,
            Tokens(
                ("Sequence", $"{message.Sequence}"),
                ("SubmittedAt", $"{message.SubmittedAt:O}"),
                ("Text", message.Text)));
        return CreateMessage(
            ModelMessageRole.User,
            "child-user-steering",
            content);
    }

    /// <summary>Estimates provider-visible input tokens using the repository's conservative estimator.</summary>
    public static int EstimateTokens(IEnumerable<ModelMessage> messages)
    {
        return messages.Sum(message => TokenEstimator.Estimate(message.GetModelVisibleContent()));
    }

    private static ModelMessage CreateMessage(
        ModelMessageRole role,
        string sectionId,
        string content)
    {
        return new ModelMessage
        {
            Role = role,
            SectionId = sectionId,
            Content = [new ModelContentPart { Content = content }],
        };
    }

    private string RenderInstructions(RepositoryInstructionBundle bundle)
    {
        if (bundle.Sources.Count == 0)
        {
            return _prompts.Get(PromptFileNames.ContextChildAgentRepositoryInstructionsNone);
        }

        var builder = new StringBuilder();
        foreach (var source in bundle.Sources.OrderBy(source => source.Position))
        {
            builder.Append("<instruction kind=\"")
                .Append(source.Kind)
                .Append("\" path=\"")
                .Append(SecurityElement.Escape(source.RelativePath))
                .Append("\" version=\"")
                .Append(SecurityElement.Escape(source.Version))
                .AppendLine("\" untrusted=\"true\">");
            builder.AppendLine(SecurityElement.Escape(source.Content));
            builder.AppendLine("</instruction>");
        }

        return builder.ToString();
    }

    private string RenderAssignment(AgentContextSnapshot context)
    {
        var tasks = new StringBuilder();
        foreach (var task in context.Tasks)
        {
            tasks.Append("<task untrusted=\"true\">")
                .Append(SecurityElement.Escape(task))
                .AppendLine("</task>");
        }

        return PromptAssetRenderer.RenderWithPlatformLineEndings(
            _prompts,
            PromptFileNames.ContextChildAgentTask,
            Tokens(
                ("BaselineIdentity", context.BaselineIdentity),
                ("Objective", SecurityElement.Escape(context.Objective)),
                ("SuppliedContext", SecurityElement.Escape(context.InitialContext)),
                ("Tasks", tasks.ToString())));
    }

    private string RenderEvidence(IReadOnlyList<Evidence> evidence)
    {
        if (evidence.Count == 0)
        {
            return _prompts.Get(PromptFileNames.ContextChildAgentInitialEvidenceNone);
        }

        var builder = new StringBuilder();
        foreach (var item in evidence)
        {
            builder.Append("<evidence id=\"")
                .Append(item.EvidenceId.Value.ToString("D"))
                .Append("\" source=\"")
                .Append(SecurityElement.Escape(item.Provenance.Source))
                .AppendLine("\" untrusted=\"true\">");
            builder.AppendLine(SecurityElement.Escape(item.Content));
            builder.AppendLine("</evidence>");
        }

        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, string> Tokens(params (string Name, string Value)[] values)
    {
        return values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
    }
}
