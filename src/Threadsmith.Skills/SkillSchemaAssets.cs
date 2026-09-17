namespace Threadsmith.Skills;

using System.Security.Cryptography;
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
        var path = SkillPathPolicy.ResolveConfined(candidate.Provenance.PackageRoot, asset.Path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != asset.Bytes
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                asset.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Skill schema changed after package verification.");
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
