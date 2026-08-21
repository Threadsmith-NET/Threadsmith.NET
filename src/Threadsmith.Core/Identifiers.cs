namespace Threadsmith.Core;

/// <summary>Provides creation and formatting shared by stable identifiers.</summary>
public interface IStableIdentifier
{
    /// <summary>Gets the identifier value.</summary>
    Guid Value { get; }
}

/// <summary>Identifies a session.</summary>
public readonly record struct SessionId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static SessionId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a run.</summary>
public readonly record struct RunId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static RunId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a step.</summary>
public readonly record struct StepId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static StepId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a tool invocation.</summary>
public readonly record struct ToolInvocationId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ToolInvocationId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies one semantic check activity.</summary>
public readonly record struct SemanticCheckId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static SemanticCheckId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a mutation set.</summary>
public readonly record struct MutationSetId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static MutationSetId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a mutation.</summary>
public readonly record struct MutationId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static MutationId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies evidence.</summary>
public readonly record struct EvidenceId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static EvidenceId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies one archived visible conversation message.</summary>
public readonly record struct ConversationMessageId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ConversationMessageId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies one governed conversation memory item.</summary>
public readonly record struct ConversationMemoryId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ConversationMemoryId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies one repository-scoped memory item.</summary>
public readonly record struct RepositoryMemoryId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static RepositoryMemoryId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies an approval.</summary>
public readonly record struct ApprovalId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ApprovalId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies an extension.</summary>
public readonly record struct ExtensionId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ExtensionId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies an extension generation.</summary>
public readonly record struct ExtensionGenerationId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ExtensionGenerationId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a capability.</summary>
public readonly record struct CapabilityId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static CapabilityId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a model profile.</summary>
public readonly record struct ModelProfileId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static ModelProfileId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies a workspace.</summary>
public readonly record struct WorkspaceId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static WorkspaceId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies one bounded delegation.</summary>
public readonly record struct DelegationId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static DelegationId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Identifies one assignment within a delegation.</summary>
public readonly record struct AgentAssignmentId(Guid Value) : IStableIdentifier
{ /// <summary>Creates an identifier.</summary>
    public static AgentAssignmentId New()
    {
        return new(Guid.NewGuid());
    }
}
