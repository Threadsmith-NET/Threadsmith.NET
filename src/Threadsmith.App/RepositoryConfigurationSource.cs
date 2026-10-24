namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

/// <summary>Preserves repository ownership on a source parsed by the common configuration reader.</summary>
internal sealed class RepositoryConfigurationSource : IConfigurationSource
{
    /// <summary>Gets the already parsed and filtered source values.</summary>
    public required IEnumerable<KeyValuePair<string, string?>> InitialData { get; init; }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new RepositoryConfigurationProvider(new MemoryConfigurationSource { InitialData = InitialData });

    /// <summary>Retains source identity without another parser or precedence implementation.</summary>
    internal sealed class RepositoryConfigurationProvider : MemoryConfigurationProvider
    {
        /// <summary>Initializes a new instance of the <see cref="RepositoryConfigurationProvider"/> class.</summary>
        internal RepositoryConfigurationProvider(MemoryConfigurationSource source)
            : base(source)
        {
        }
    }
}
