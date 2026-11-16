namespace Threadsmith.Skills;

using System.Security.Cryptography;
using Threadsmith.Core;

/// <summary>Reads bounded package bytes and rechecks integrity at the point of use.</summary>
internal static class SkillAssetContent
{
    /// <summary>Reads exactly the declared bytes without accepting growth or changed content.</summary>
    internal static async Task<byte[]> ReadAsync(string root, SkillAssetMetadata asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (asset.Bytes is < 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Skill asset length is outside the supported bound.");
        }

        var path = SkillPathPolicy.ResolveConfined(root, asset.Path);
        ScopedFilePaths.RejectLinks(path, root, allowMissing: false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ScopedFilePaths.RejectLinks(path, root, allowMissing: false);
        if (stream.Length != asset.Bytes)
        {
            throw new InvalidDataException("Skill asset length changed after verification.");
        }

        var bytes = new byte[checked((int)asset.Bytes)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Skill asset content changed after verification.");
        }

        return bytes;
    }
}
