namespace Threadsmith.App;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Resolves exact deployed provider instructions for one configured model profile.</summary>
internal sealed class ModelProviderInstructionResolver : IModelProviderInstructionResolver
{
    private readonly ConfiguredModelCatalog _catalog;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="ModelProviderInstructionResolver"/> class.</summary>
    internal ModelProviderInstructionResolver(
        ConfiguredModelCatalog catalog,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(prompts);
        _catalog = catalog;
        _prompts = prompts;
    }

    /// <inheritdoc />
    public ModelProviderInstructions? Resolve(ModelProfileId profileId)
    {
        var asset = _catalog.Get(profileId).ProviderInstructionAsset;
        return asset is null
            ? null
            : new ModelProviderInstructions
            {
                SectionId = asset.SectionId,
                Content = _prompts.Get(asset.PromptFileName),
            };
    }
}
