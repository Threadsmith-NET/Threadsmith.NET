namespace Threadsmith.DotNet;

/// <summary>Shared compiler-input policy for refresh admission and edit diagnostics.</summary>
internal static class SemanticRefreshPathPolicy
{
    /// <summary>Classifies compiler inputs using the finite catalog and evaluated membership.</summary>
    public static SemanticInputKind Classify(string repositoryPath, string path, SemanticRefreshInventory? inventory = null)
    {
        if (IsIgnoredGeneratedDocument(repositoryPath, path))
        {
            return SemanticInputKind.None;
        }

        if (inventory?.SourceDocuments.Contains(path) == true)
        {
            return SemanticInputKind.Source;
        }

        if (inventory?.AdditionalDocuments.Contains(path) == true
            || inventory?.AnalyzerConfigDocuments.Contains(path) == true
            || inventory?.FullReloadInputs.Contains(path) == true)
        {
            return SemanticInputKind.Compilation;
        }

        if (IsIgnoredPath(repositoryPath, path))
        {
            return SemanticInputKind.None;
        }

        return IsGraphControlPath(path)
            ? SemanticInputKind.Compilation
            : Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? SemanticInputKind.Source
                : SemanticInputKind.None;
    }

    /// <summary>Recognizes the finite project and build configuration catalog.</summary>
    public static bool IsGraphControlPath(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether the path is generated, transient, or otherwise irrelevant to semantic refresh.</summary>
    public static bool IsIgnoredPath(string repositoryPath, string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetRelativePath(repositoryPath, path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsIgnoredDirectorySegment))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return name.StartsWith('~')
            || name.EndsWith('~')
            || extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether a directory segment represents generated or tool-local churn.</summary>
    public static bool IsIgnoredDirectorySegment(string segment)
    {
        return segment.Equals(".codegraph", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".inbox", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".threadsmith", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vscode", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether a known source document is generated build-output churn.</summary>
    public static bool IsIgnoredGeneratedSourceDocument(string repositoryPath, string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetRelativePath(repositoryPath, path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return true;
        }

        return IsGeneratedSourceDocumentName(Path.GetFileName(path))
            && normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(IsBuildOutputDirectorySegment);
    }

    /// <summary>Recognizes MSBuild-derived analyzer configuration beneath build-output directories.</summary>
    public static bool IsIgnoredGeneratedEditorConfig(string repositoryPath, string path)
    {
        var normalized = Path.GetRelativePath(repositoryPath, path).Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => IsBuildOutputDirectorySegment(segment)
                    || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
            && Path.GetFileName(normalized).EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Excludes known derived text inputs even when Roslyn reports them as explicitly loaded.</summary>
    public static bool IsIgnoredGeneratedDocument(string repositoryPath, string path)
    {
        return IsIgnoredGeneratedSourceDocument(repositoryPath, path)
            || IsIgnoredGeneratedEditorConfig(repositoryPath, path);
    }

    private static bool IsGeneratedSourceDocumentName(string name)
    {
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildOutputDirectorySegment(string segment)
    {
        return segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Semantic work admitted by the compiler input policy.</summary>
internal enum SemanticInputKind
{
    None,
    Source,
    Compilation,
}
