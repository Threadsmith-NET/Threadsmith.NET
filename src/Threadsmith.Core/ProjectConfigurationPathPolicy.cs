namespace Threadsmith.Core;

/// <summary>Identifies conventional project, build, tool, and runtime configuration paths.</summary>
public static class ProjectConfigurationPathPolicy
{
    private static readonly HashSet<string> _configurationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "dotnet-tools.json", "launchSettings.json", "package.json", "package-lock.json",
        "npm-shrinkwrap.json", "bower.json", "tsconfig.json", "jsconfig.json", "composer.json", "composer.lock",
        "action.yml", "action.yaml", "azure-pipelines.yml", "azure-pipelines.yaml",
        "bitbucket-pipelines.yml", ".gitlab-ci.yml", "docker-compose.yml", "docker-compose.yaml",
        "compose.yml", "compose.yaml", "NuGet.Config", "packages.config", "pom.xml", "settings.xml",
    };

    /// <summary>Checks a normalized destination without reading or executing its contents.</summary>
    /// <param name="path">A normalized file path relative to its repository or export authority root. Both slash forms are accepted.</param>
    /// <returns>Whether the path must use the governed configuration-mutation workflow.</returns>
    public static bool IsConfigurationPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var fileName = segments[^1];
        var extension = Path.GetExtension(fileName);
        if (_configurationNames.Contains(fileName)
            || extension.ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".vcxproj"
                or ".props" or ".targets" or ".sln" or ".slnx" or ".config"
            || fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                && extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (extension.ToLowerInvariant() is not (".json" or ".yaml" or ".yml" or ".xml"))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Equals("config", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("settings", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".settings", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".runtimeconfig", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".runtimeconfig.dev", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("compose.", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("docker-compose.", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".deps", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("tsconfig.", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("jsconfig.", StringComparison.OrdinalIgnoreCase)
            || segments.SkipLast(1).Any(segment => segment.Equals(".config", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".vscode", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".devcontainer", StringComparison.OrdinalIgnoreCase))
            || segments.Zip(segments.Skip(1)).Any(pair => pair.First.Equals(".github", StringComparison.OrdinalIgnoreCase)
                && (pair.Second.Equals("workflows", StringComparison.OrdinalIgnoreCase)
                    || pair.Second.Equals("actions", StringComparison.OrdinalIgnoreCase)));
    }
}
