namespace Threadsmith.Core;

/// <summary>Stable role names shared by delegation requests and trusted configuration.</summary>
public static class AgentRoleNames
{
    /// <summary>Returns the public configuration and tool name for a defined role.</summary>
    public static string GetName(AgentRole role)
    {
        return role switch
        {
            AgentRole.Explorer => "explorer",
            AgentRole.Implementer => "implementer",
            AgentRole.SecurityReviewer => "securityReviewer",
            AgentRole.TestReviewer => "testReviewer",
            AgentRole.PerformanceReviewer => "performanceReviewer",
            AgentRole.ArchitectureReviewer => "architectureReviewer",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    /// <summary>Parses exact public names without accepting numeric enum values or aliases.</summary>
    public static bool TryParse(string? name, out AgentRole role)
    {
        role = name switch
        {
            "explorer" => AgentRole.Explorer,
            "implementer" => AgentRole.Implementer,
            "securityReviewer" => AgentRole.SecurityReviewer,
            "testReviewer" => AgentRole.TestReviewer,
            "performanceReviewer" => AgentRole.PerformanceReviewer,
            "architectureReviewer" => AgentRole.ArchitectureReviewer,
            _ => (AgentRole)(-1),
        };
        return Enum.IsDefined(role);
    }
}
