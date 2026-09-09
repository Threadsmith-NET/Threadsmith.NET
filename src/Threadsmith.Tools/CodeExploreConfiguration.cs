namespace Threadsmith.Tools;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Binds the shared code-explore operational snapshot through ordinary host configuration.</summary>
public static class CodeExploreConfiguration
{
    /// <summary>Resolves configured defaults, rejects negative caps, and preserves explicit off switches.</summary>
    public static CodeExploreOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection("tools:codeExplore").Get<CodeExploreOptions>() ?? new CodeExploreOptions();
        return options.Resolve();
    }
}
