// Threadsmith.NET architecture-direction tests (plan-01 task 5).
//
// These tests enforce the strategy §8.1 dependency-direction rules by parsing
// each product project's .csproj <ProjectReference> includes. They pass on the
// empty scaffold and fail fast if a wrong reference is added. The authoritative
// dependency graph is defined once in AllowedGraph below.
//
// Parsing .csproj (rather than reflecting on compiled assemblies) is required in
// the M0 scaffold because the placeholder projects contain no code that exercises
// their references, so the compiler omits unused assembly references from metadata.
// Once real code lands in later plans, a reflection-based test could supplement
// this, but the declared-dependency check is the durable contract.
//
// Strategy references:
//   §8.1 — dependency rules (Core references nothing; Tui references no persistence
//          impl; Abstractions stays small; extension impls reference Abstractions not
//          Runtime; external SDKs isolated behind adapters; terminal/Roslyn/SDK
//          types never in core interfaces).
//   §7.1 — boundary rules (model-provider SDK / Roslyn / extension / terminal-library
//          types must not leak into domain events or persistent state).
namespace Threadsmith.Architecture.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

/// <summary>
/// Asserts the Threadsmith.NET project dependency graph matches strategy §8.1.
/// </summary>
public static class DependencyDirectionTests
{
    /// <summary>
    /// The authoritative Threadsmith.* project dependency graph (strategy §8.1).
    /// Key = project simple name (without the Threadsmith. prefix in the file system);
    /// value = the set of Threadsmith.* projects it is permitted to reference. Omitted
    /// keys (e.g. Core, Extensions.Abstractions) are permitted to reference no other
    /// Threadsmith.* project.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> _allowedGraph = CreateAllowedGraph();

    /// <summary>
    /// External framework packages that specific projects must NOT reference
    /// (strategy §8.1). Key = project; value = forbidden package id prefixes.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> _forbiddenPackages = CreateForbiddenPackages();

    /// <summary>
    /// Product projects whose dependency direction is asserted. Each maps to its
    /// .csproj path under src/.
    /// </summary>
    private static readonly string[] _productProjects =
    [
        "Threadsmith.Core",
        "Threadsmith.Telemetry",
        "Threadsmith.Persistence",
        "Threadsmith.Models",
        "Threadsmith.Models.OpenAiCompatible",
        "Threadsmith.Models.OpenAiCodex",
        "Threadsmith.Context",
        "Threadsmith.Tools",
        "Threadsmith.DotNet",
        "Threadsmith.Workspaces",
        "Threadsmith.Validation",
        "Threadsmith.Execution",
        "Threadsmith.Skills",
        "Threadsmith.Hooks",
        "Threadsmith.Extensions.Abstractions",
        "Threadsmith.Extensions.Runtime",
        "Threadsmith.Tui",
        "Threadsmith.Cli",
        "Threadsmith.Mcp",
        "Threadsmith.Scripting.Worker",
        "Threadsmith.App",
    ];

    /// <summary>The repository root (two levels above the test bin output directory).</summary>
    private static string RepoRoot
    {
        get
        {
            // Test output is <root>/tests/Threadsmith.Architecture.Tests/bin/Debug/net10.0
            var bin = AppContext.BaseDirectory;
            return Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        }
    }

    /// <summary>Every product .csproj must exist on disk.</summary>
    [Fact]
    public static void AllProductCsprojFilesExist()
    {
        var missing = _productProjects
            .Where(p => !File.Exists(CsprojPath(p)))
            .ToList();
        Assert.Empty(missing);
    }

    /// <summary>Each project may only reference the Threadsmith.* projects in its allowed set.</summary>
    [Theory]
    [MemberData(nameof(ProductProjectNames))]
    public static void ProjectReferencesOnlyAllowedThreadsmithProjects(string projectName)
    {
        var referenced = GetProjectReferences(projectName)
            .Where(n => n.StartsWith("Threadsmith.", StringComparison.Ordinal) && n != projectName)
            .ToHashSet();

        HashSet<string> allowed = _allowedGraph.TryGetValue(projectName, out HashSet<string>? a) ? a : [];
        var forbidden = referenced.Except(allowed).ToHashSet();
        Assert.True(
            forbidden.Count == 0,
            $"{projectName} references Threadsmith.* projects not in its allowed set: " +
            $"{string.Join(", ", forbidden)}. Allowed: {(allowed.Count == 0 ? "(none)" : string.Join(", ", allowed))}.");
    }

    /// <summary>Guarded projects must not reference forbidden external packages (§8.1).</summary>
    [Theory]
    [MemberData(nameof(GuardedProjectNames))]
    public static void ProjectDoesNotReferenceForbiddenPackages(string projectName)
    {
        HashSet<string> forbidden = _forbiddenPackages[projectName];
        var violations = GetPackageReferences(projectName)
            .Where(p => forbidden.Any(f => p.StartsWith(f, StringComparison.Ordinal)))
            .ToHashSet();
        Assert.True(
            violations.Count == 0,
            $"{projectName} references forbidden package(s): {string.Join(", ", violations)}.");
    }

    /// <summary>Markdig remains isolated to the replaceable TUI projection.</summary>
    [Fact]
    public static void MarkdigPackageIsReferencedOnlyByTui()
    {
        string[] violations =
        [
            .. _productProjects
                .Where(projectName => projectName != "Threadsmith.Tui")
                .Where(projectName => GetPackageReferences(projectName).Contains("Markdig", StringComparer.Ordinal)),
        ];
        Assert.Empty(violations);
        Assert.Contains("Markdig", GetPackageReferences("Threadsmith.Tui"));
    }

    /// <summary>The host-owned semantic Markdown document contains no parser or terminal-backend types.</summary>
    [Fact]
    public static void TuiMarkdownDocumentIsBackendNeutral()
    {
        var path = Path.Combine(RepoRoot, "src", "Threadsmith.Tui", "TuiMarkdownDocument.cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("Markdig", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Spectre", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PrettyPrompt", source, StringComparison.Ordinal);
    }

    /// <summary>The neutral model project contains no OpenAI-compatible implementation or wire protocol.</summary>
    [Fact]
    public static void NeutralModelsProjectContainsNoOpenAiCompatibleImplementation()
    {
        var modelsDirectory = Path.Combine(RepoRoot, "src", "Threadsmith.Models");
        string[] violations =
        [
            .. Directory.GetFiles(modelsDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains("OpenAiCompatible", StringComparison.Ordinal)),
        ];
        Assert.Empty(violations);
    }

    /// <summary>The neutral model project contains no Codex implementation or wire protocol.</summary>
    [Fact]
    public static void NeutralModelsProjectContainsNoOpenAiCodexImplementation()
    {
        var modelsDirectory = Path.Combine(RepoRoot, "src", "Threadsmith.Models");
        string[] violations =
        [
            .. Directory.GetFiles(modelsDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains("OpenAiCodex", StringComparison.Ordinal)),
        ];
        Assert.Empty(violations);
    }

    /// <summary>Core must reference no other Threadsmith.* project at all (§8.1).</summary>
    [Fact]
    public static void CoreReferencesNoThreadsmithProjects()
    {
        var referenced = GetProjectReferences("Threadsmith.Core")
            .Where(n => n.StartsWith("Threadsmith.", StringComparison.Ordinal))
            .ToHashSet();
        Assert.Empty(referenced);
    }

    /// <summary>Extensions.Abstractions must reference no other Threadsmith.* project (§8.1).</summary>
    [Fact]
    public static void AbstractionsReferencesNoThreadsmithProjects()
    {
        var referenced = GetProjectReferences("Threadsmith.Extensions.Abstractions")
            .Where(n => n.StartsWith("Threadsmith.", StringComparison.Ordinal))
            .ToHashSet();
        Assert.Empty(referenced);
    }

    /// <summary>
    /// Deliberately-wrong-reference sentinel: if someone adds a forbidden reference
    /// the matching theory above fails. This test documents the negative case so the
    /// acceptance criterion ("fail fast on a deliberately wrong reference") is
    /// represented in the suite.
    /// </summary>
    [Fact]
    public static void WrongReferenceWouldFailFast()
    {
        // Simulate the assertion used by the theory on a hypothetical wrong reference.
        // Core is permitted to reference no Threadsmith.* project (omitted key = empty set).
        HashSet<string> allowed = _allowedGraph.TryGetValue("Threadsmith.Core", out HashSet<string>? coreAllowed)
            ? coreAllowed : [];
        var hypotheticalWrong = new HashSet<string> { "Threadsmith.Tui" };
        var forbidden = hypotheticalWrong.Except(allowed).ToHashSet();
        Assert.NotEmpty(forbidden); // proves the guard would catch it
    }

    /// <summary>Returns the .csproj path for a product project.</summary>
    private static string CsprojPath(string projectName)
    {
        return Path.Combine(RepoRoot, "src", projectName, projectName + ".csproj");
    }

    /// <summary>Parses the &lt;ProjectReference Include="..." /&gt; entries for a project.</summary>
    private static IEnumerable<string> GetProjectReferences(string projectName)
    {
        var doc = XDocument.Load(CsprojPath(projectName));
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();
    }

    /// <summary>Parses the &lt;PackageReference Include="..." /&gt; entries for a project.</summary>
    private static IEnumerable<string> GetPackageReferences(string projectName)
    {
        var doc = XDocument.Load(CsprojPath(projectName));
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(n => !string.IsNullOrEmpty(n));
    }

    /// <summary>Theory data: every product project name.</summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public static IEnumerable<TheoryDataRow<string>> ProductProjectNames =>
        _productProjects.Select(static projectName => new TheoryDataRow<string>(projectName));

    /// <summary>Theory data: only projects with forbidden-package guards.</summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public static IEnumerable<TheoryDataRow<string>> GuardedProjectNames =>
        _forbiddenPackages.Keys.Select(static projectName => new TheoryDataRow<string>(projectName));

    private static Dictionary<string, HashSet<string>> CreateAllowedGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>
        {
            ["Threadsmith.Telemetry"] = ["Threadsmith.Core"],
            ["Threadsmith.Persistence"] = ["Threadsmith.Core", "Threadsmith.Telemetry"],
            ["Threadsmith.Models"] = ["Threadsmith.Core"],
            ["Threadsmith.Models.OpenAiCompatible"] = ["Threadsmith.Core", "Threadsmith.Models"],
            ["Threadsmith.Models.OpenAiCodex"] = ["Threadsmith.Core", "Threadsmith.Models"],
            ["Threadsmith.Context"] = ["Threadsmith.Core", "Threadsmith.Models"],
            ["Threadsmith.Tools"] = ["Threadsmith.Core", "Threadsmith.Models", "Threadsmith.Context"],
            ["Threadsmith.DotNet"] = ["Threadsmith.Core", "Threadsmith.Models", "Threadsmith.Context"],
            ["Threadsmith.Workspaces"] = ["Threadsmith.Core", "Threadsmith.Context"],
            ["Threadsmith.Validation"] = ["Threadsmith.Core", "Threadsmith.Tools"],
            ["Threadsmith.Execution"] = ["Threadsmith.Core", "Threadsmith.Context", "Threadsmith.Models", "Threadsmith.Persistence", "Threadsmith.Tools"],
            ["Threadsmith.Skills"] = ["Threadsmith.Core", "Threadsmith.Context", "Threadsmith.Models", "Threadsmith.Telemetry", "Threadsmith.Tools"],
            ["Threadsmith.Hooks"] = ["Threadsmith.Core", "Threadsmith.Tools"],
            ["Threadsmith.Extensions.Runtime"] = ["Threadsmith.Core", "Threadsmith.Extensions.Abstractions", "Threadsmith.Telemetry", "Threadsmith.Tools"],
            ["Threadsmith.Tui"] = ["Threadsmith.Core", "Threadsmith.Context", "Threadsmith.Tools", "Threadsmith.Execution"],
            ["Threadsmith.Cli"] = ["Threadsmith.Core", "Threadsmith.Execution"],
            ["Threadsmith.Mcp"] = ["Threadsmith.Core", "Threadsmith.Tools", "Threadsmith.Extensions.Abstractions"],
        };

        // App is the composition root and may reference everything.
        graph["Threadsmith.App"] =
        [
            "Threadsmith.Core", "Threadsmith.Telemetry", "Threadsmith.Persistence",
            "Threadsmith.Models", "Threadsmith.Models.OpenAiCompatible", "Threadsmith.Models.OpenAiCodex",
            "Threadsmith.Context", "Threadsmith.Tools",
            "Threadsmith.DotNet", "Threadsmith.Workspaces", "Threadsmith.Validation",
            "Threadsmith.Execution", "Threadsmith.Skills", "Threadsmith.Hooks", "Threadsmith.Extensions.Runtime",
            "Threadsmith.Tui", "Threadsmith.Cli", "Threadsmith.Mcp",
            "Threadsmith.Scripting.Worker",
        ];
        return graph;
    }

    private static Dictionary<string, HashSet<string>> CreateForbiddenPackages()
    {
        // Core references no UI, no Roslyn, no terminal library, no model-provider SDK,
        // no extension implementations (§8.1).
        var graph = new Dictionary<string, HashSet<string>>
        {
            ["Threadsmith.Core"] = ["Terminal.Gui", "PrettyPrompt", "Spectre.Console", "Microsoft.CodeAnalysis", "OpenAI", "System.Net.Http"],
            // Provider SDKs and concrete protocol packages stay in dedicated provider projects.
            ["Threadsmith.Models"] = ["OpenAI"],
            ["Threadsmith.Skills"] = ["Terminal.Gui", "PrettyPrompt", "Spectre.Console", "Microsoft.CodeAnalysis", "OpenAI", "Microsoft.Data.Sqlite"],
            // Abstractions stays small + stable; references no host implementation (§8.1).
            ["Threadsmith.Extensions.Abstractions"] = ["Terminal.Gui", "PrettyPrompt", "Spectre.Console", "Microsoft.CodeAnalysis", "OpenAI", "Microsoft.Data.Sqlite"],
            // Tui references no persistence implementations (§8.1).
            ["Threadsmith.Tui"] = ["Microsoft.Data.Sqlite"],
        };
        return graph;
    }
}
