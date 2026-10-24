namespace Threadsmith.Core;

/// <summary>Resolves host storage roots, optionally isolated by the launching process.</summary>
public static class HostStoragePaths
{
    /// <summary>Gets a normal OS folder or its equivalent beneath THREADSMITH_STATE_ROOT.</summary>
    /// <remarks>The launch-only override is never read from repository or model configuration.</remarks>
    public static string GetFolderPath(Environment.SpecialFolder folder)
    {
        var root = Environment.GetEnvironmentVariable("THREADSMITH_STATE_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return Environment.GetFolderPath(folder);
        }

        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException("THREADSMITH_STATE_ROOT must be an absolute host storage directory.");
        }

        var name = folder switch
        {
            Environment.SpecialFolder.UserProfile => "profile",
            Environment.SpecialFolder.LocalApplicationData => "local",
            Environment.SpecialFolder.CommonApplicationData => "machine",
            _ => throw new ArgumentOutOfRangeException(nameof(folder)),
        };
        return Path.Combine(Path.GetFullPath(root), name);
    }
}
