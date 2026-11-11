namespace Threadsmith.Tools;

using System.Collections.Immutable;

/// <summary>Freezes host-owned launch inputs without changing request provenance.</summary>
internal sealed record PreparedProcessLaunch(string FileName, ImmutableArray<string> Arguments, ImmutableDictionary<string, string> Environment, string? CommandLine = null);

/// <summary>Shares the launcher's absolute-PATH algorithm with shell preparation.</summary>
internal sealed class ProcessLaunchResolver
{
    private readonly string[] _directories;
    private readonly string[] _extensions;
    private readonly Dictionary<string, string?> _resolved = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ProcessLaunchResolver"/> class for one preparation.</summary>
    internal ProcessLaunchResolver(IReadOnlyDictionary<string, string> environment)
    {
        _directories = [.. (environment.GetValueOrDefault("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'))
            .Where(Path.IsPathFullyQualified)];
        _extensions = OperatingSystem.IsWindows()
            ? (environment.GetValueOrDefault("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
    }

    /// <summary>Captures exactly the environment the tracked child will receive.</summary>
    internal static ImmutableDictionary<string, string> EnvironmentFor(IReadOnlyDictionary<string, string> additions)
    {
        var environment = ImmutableDictionary.CreateBuilder<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string[] allowed = ["PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "HOME", "DOTNET_ROOT", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramFiles"];
        foreach (var name in allowed)
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        foreach (var (name, value) in additions)
        {
            environment[name] = value;
        }

        return environment.ToImmutable();
    }

    /// <summary>Resolves only absolute PATH entries in the effective child environment.</summary>
    internal static string Resolve(string name, IReadOnlyDictionary<string, string> environment) => new ProcessLaunchResolver(environment).Resolve(name);

    /// <summary>Reuses positive and negative lookup results only within this preparation.</summary>
    internal string Resolve(string name)
    {
        if (!_resolved.TryGetValue(name, out var target))
        {
            target = Find(name);
            _resolved.Add(name, target);
        }

        return target ?? throw new FileNotFoundException("Executable unavailable.");
    }

    private string? Find(string name)
    {
        if (Path.IsPathFullyQualified(name))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        if (name.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidOperationException("Executable paths must be fully qualified.");
        }

        string[] candidates = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? [.. _extensions.Select(extension => name + extension)] : [name];
        foreach (var directory in _directories)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }

        return null;
    }
}
