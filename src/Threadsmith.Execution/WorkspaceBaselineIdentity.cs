namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Creates the canonical immutable identity for one captured workspace baseline.</summary>
internal static class WorkspaceBaselineIdentity
{
    /// <summary>Hashes stable baseline metadata and deterministically ordered file identities.</summary>
    public static string Create(WorkspaceBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "workspace-baseline:v2");
        Append(hash, baseline.WorkspaceId.Value.ToString("D"));
        Append(hash, baseline.CapturedAt.ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, baseline.GitRevision ?? string.Empty);
        Append(hash, NormalizePath(baseline.SelectedSolutionPath ?? string.Empty));
        foreach (var file in baseline.Files
            .OrderBy(file => NormalizePath(file.RelativePath), StringComparer.Ordinal))
        {
            Append(hash, NormalizePath(file.RelativePath));
            Append(hash, file.Sha256.ToLowerInvariant());
            Append(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
