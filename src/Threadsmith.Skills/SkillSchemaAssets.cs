namespace Threadsmith.Skills;

using System.Text;
using Threadsmith.Core;

/// <summary>Reads the same verified schema for inspection and invocation validation.</summary>
internal static class SkillSchemaAssets
{
    /// <summary>Loads a confined schema asset and rechecks its declared length and digest.</summary>
    internal static async Task<string> ReadAsync(
        SkillCatalogCandidate candidate,
        string schemaAssetPath,
        CancellationToken cancellationToken)
    {
        var asset = candidate.Metadata.Assets.Single(item =>
            string.Equals(item.Path, schemaAssetPath, StringComparison.OrdinalIgnoreCase));
        var bytes = await SkillAssetContent.ReadAsync(candidate.Provenance.PackageRoot, asset, cancellationToken);

        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
