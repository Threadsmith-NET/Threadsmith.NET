namespace Threadsmith.App;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Resolves exact deployed provider instructions for one configured model profile.</summary>
internal sealed class ModelProviderInstructionResolver : IModelProviderInstructionResolver
{
    private const string OpenAiCodexProvider = "openai-codex";
    private const string OpenAiCodexSection = "provider-openai-codex-instructions";

    private readonly ConfiguredModelCatalog _catalog;
    private readonly ModelProviderInstructions _openAiCodexInstructions;

    /// <summary>Initializes a new instance of the <see cref="ModelProviderInstructionResolver"/> class.</summary>
    internal ModelProviderInstructionResolver(
        ConfiguredModelCatalog catalog,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(prompts);
        _catalog = catalog;
        _openAiCodexInstructions = new ModelProviderInstructions
        {
            SectionId = OpenAiCodexSection,
            Content = prompts.Get(PromptFileNames.ProviderOpenAiCodexInstructions),
        };
    }

    /// <inheritdoc />
    public ModelProviderInstructions? Resolve(ModelProfileId profileId)
    {
        var profile = _catalog.Get(profileId);
        return string.Equals(profile.Provider, OpenAiCodexProvider, StringComparison.Ordinal)
            ? _openAiCodexInstructions
            : null;
    }
}
