namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Threadsmith.Context;
using Threadsmith.Core;
using Xunit;

/// <summary>Guards the complete deployed-prompt catalog and governed literal sinks.</summary>
public static partial class PromptAssetArchitectureTests
{
    private static readonly HashSet<string> GovernedRendererTypes =
    [
        "AdvancedSemanticMarkdownRenderer",
        "CodeExploreMarkdownRenderer",
        "DelegateAgentsResultRenderer",
    ];

    private static readonly HashSet<string> GovernedModelTextBuilderMethods =
    [
        "MutationProposalApplication.FormatPreMutationCorrection",
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> GovernedStructuredTextProperties =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["AgentAssignment"] = ["Tasks"],
            ["CodeExploreAvailability"] = ["Reason", "RecommendedActions"],
            ["CodeExploreAnchorResolution"] = ["Reason"],
            ["CodeExploreAllocationFileSummary"] = ["OmissionReason"],
            ["CodeExploreArtifactContinuationTarget"] = ["Reason"],
            ["CodeExploreAssociatedArtifact"] = ["Omissions"],
            ["CodeExploreBackReference"] = ["Reason"],
            ["CodeExploreNextActionHint"] = ["Message"],
            ["CodeExploreSourceGuarantee"] = ["Message"],
            ["CodeExploreContinuationTarget"] = ["Reason"],
            ["CodeExploreNotShownTarget"] = ["Reason"],
            ["CodeExplorePresentation"] = ["ModelSummary", "NextActions"],
            ["CodeExploreCoverage"] = ["Omissions"],
            ["CodeExploreArtifactCoverage"] = ["Omissions"],
            ["CodeExploreBlastRadius"] = ["Omissions"],
            ["CodeExploreResult"] = ["Omissions"],
            ["CodeExploreFlowBoundary"] = ["Reason"],
            ["CodeExploreDispatchBranch"] = ["Omissions"],
            ["CodeExploreFlowEdge"] = ["Proof"],
            ["CodeExploreFlowPath"] = ["Reason"],
            ["CodeExploreFileRelevanceSummary"] = ["Reason"],
            ["CodeExploreDedupSummary"] = ["Reasons"],
            ["SemanticTraversalSummary"] = ["Omissions"],
            ["CSharpPatternSearchResult"] = ["Omissions"],
            ["GeneratedCodeResult"] = ["Omissions"],
            ["MalformedInvocationDiagnostic"] = ["SafeMessage"],
            ["MutationCorrectionContext"] = ["SafeReason"],
            ["SkillWorkflowCheckpoint"] = ["NextAction"],
            ["WebFetchResponse"] = ["TrustBoundary"],
            ["WebSearchResponse"] = ["TrustBoundary"],
        };

    private static readonly IReadOnlyDictionary<string, int[]> GovernedStructuredTextOrdinals =
        new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["CodeExploreAvailability"] = [1, 6],
            ["CodeExploreAnchorResolution"] = [6],
            ["CodeExploreAllocationFileSummary"] = [5],
            ["CodeExploreArtifactContinuationTarget"] = [5],
            ["CodeExploreAssociatedArtifact"] = [10],
            ["CodeExploreBackReference"] = [7],
            ["CodeExploreNextActionHint"] = [1],
            ["CodeExploreSourceGuarantee"] = [10],
            ["CodeExploreContinuationTarget"] = [9],
            ["CodeExploreNotShownTarget"] = [3],
            ["CodeExplorePresentation"] = [0, 3],
            ["CodeExploreCoverage"] = [4],
            ["CodeExploreArtifactCoverage"] = [12],
            ["CodeExploreBlastRadius"] = [9],
            ["CodeExploreResult"] = [5],
            ["CodeExploreFlowBoundary"] = [3],
            ["CodeExploreDispatchBranch"] = [5],
            ["CodeExploreFlowEdge"] = [8],
            ["CodeExploreFlowPath"] = [5],
            ["CodeExploreFileRelevanceSummary"] = [9],
            ["CodeExploreDedupSummary"] = [6],
            ["SemanticTraversalSummary"] = [7],
            ["CSharpPatternSearchResult"] = [4],
            ["GeneratedCodeResult"] = [4],
            ["MutationCorrectionContext"] = [3],
        };

    private static readonly IReadOnlyDictionary<string, HashSet<string>> GovernedLiteralExclusions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["AdvancedSemanticMarkdownRenderer.AppendCodeBlock"] = ["csharp"],
            ["AdvancedSemanticMarkdownRenderer.FormatImpactLocation"] = [" — project "],
            ["AdvancedSemanticMarkdownRenderer.Pluralize"] = ["s"],
            ["AdvancedSemanticMarkdownRenderer.Render"] = ["host default"],
            ["CodeExploreMarkdownRenderer.AppendAssociatedArtifacts"] = ["artifact", "text"],
            ["CodeExploreMarkdownRenderer.AppendSourceCode"] = ["csharp", "generated", "linked"],
            ["CodeExploreMarkdownRenderer.CreateSelectedEvidenceItems"] = ["section:", "symbol:"],
            ["CodeExploreMarkdownRenderer.AppendHeader"] = ["C# code"],
            ["CodeExploreMarkdownRenderer.FormatOptionalRange"] = [":L", "-L"],
            ["CodeExploreMarkdownRenderer.FormatRange"] = ["L", "-L"],
            ["CodeExploreMarkdownRenderer.PluralSuffix"] = ["s"],
            ["ContextAssembler.BuildStructuredMessages"] =
            [
                "<task_state>",
                "</task_state>",
                "\n<governed_state>",
                "</governed_state>",
                "\n<evidence_set>",
                "</evidence_set>",
                "\n<available_tools>",
                "</available_tools>",
                "\n<required_output>",
                "</required_output>",
            ],
            ["ContextAssembler.BuildModelInput"] =
            [
                "<system_policy>",
                "</system_policy>",
                "<phase_instructions>",
                "</phase_instructions>",
                "<task>",
                "</task>",
                "<current_turn untrusted=\"true\">",
                "</current_turn>",
                "<governed_state>",
                "</governed_state>",
                "<evidence_set>",
                "</evidence_set>",
                "<available_tools>",
                "</available_tools>",
                "<required_output>",
                "</required_output>",
            ],
            ["CorrectiveMessageFactory.CreateToolBatchFailureSummary"] =
            [
                "unknown",
                "unknown tool",
                "tool '",
            ],
            ["DelegateAgentsResultRenderer.RenderFinding"] = [" symbol="],
            ["MalformedInvocationException.CreateCompatibilityDiagnostic"] =
            [
                "The model emitted a malformed invocation.",
            ],
            ["ModelSkillProcedureRunner.RunAsync"] = ["null"],
            ["SessionApplication.CreateReducedToolResultMessage"] = ["{\"isTruncated\":true}"],
        };

    private static readonly string[] PromptOwners =
    [
        "Threadsmith.Context",
        "Threadsmith.DotNet",
        "Threadsmith.Execution",
        "Threadsmith.Mcp",
        "Threadsmith.Models",
        "Threadsmith.Models.OpenAiCodex",
        "Threadsmith.Skills",
        "Threadsmith.Tools",
    ];

    private static readonly string[] GovernedSourceProjects =
    [
        .. PromptOwners,
        "Threadsmith.App",
    ];

    private static string RepositoryRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Constants, metadata, sources, deployment globs, and documentation form one exact catalog.</summary>
    [Fact]
    public static void PromptCatalog_IsAnExactSourceDeploymentDocumentationBijection()
    {
        var constants = PromptFileNames.All.ToArray();
        var reflectedConstants = typeof(PromptFileNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();
        var definitions = PromptAssetCatalog.All.ToArray();
        Assert.Equal(constants.Length, constants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            reflectedConstants.Order(StringComparer.Ordinal),
            constants.Order(StringComparer.Ordinal));
        Assert.Equal(
            constants.Order(StringComparer.Ordinal),
            definitions.Select(definition => definition.FileName).Order(StringComparer.Ordinal));

        var sourceFiles = PromptOwners
            .SelectMany(owner => Directory.GetFiles(
                Path.Combine(RepositoryRoot, "src", owner, "Prompts"),
                "*.md",
                SearchOption.TopDirectoryOnly))
            .ToArray();
        Assert.Equal(constants.Length, sourceFiles.Length);
        Assert.Equal(constants.Length, sourceFiles.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var definition in definitions)
        {
            var path = Path.Combine(
                RepositoryRoot,
                "src",
                definition.Owner,
                "Prompts",
                definition.FileName);
            Assert.True(File.Exists(path), $"Missing source prompt asset: {definition.Owner}/Prompts/{definition.FileName}");
            var bytes = File.ReadAllBytes(path);
            Assert.InRange(bytes.Length, 0, DeployedPromptLoader.MaximumFileBytes);
            var tokens = PromptTokenRegex().Matches(File.ReadAllText(path))
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                definition.RequiredTokens.Concat(definition.OptionalTokens).Order(StringComparer.Ordinal),
                tokens.Order(StringComparer.Ordinal));
        }

        var appProject = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Threadsmith.App",
            "Threadsmith.App.csproj"));
        var deployedOwners = appProject.Descendants("ThreadsmithPromptAsset")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(include => include.Split('\\', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToArray();
        Assert.Equal(PromptOwners.Order(StringComparer.Ordinal), deployedOwners.Order(StringComparer.Ordinal));

        var documented = File.ReadLines(Path.Combine(RepositoryRoot, "docs", "operations", "prompts.md"))
            .Select(line => PromptDocumentationRowRegex().Match(line))
            .Where(match => match.Success)
            .ToArray();
        Assert.Equal(constants.Length, documented.Length);
        Assert.Equal(
            constants.Order(StringComparer.Ordinal),
            documented.Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        foreach (var row in documented)
        {
            var definition = PromptAssetCatalog.Get(row.Groups[1].Value);
            Assert.Equal(definition.Owner, row.Groups[2].Value);
            Assert.Equal("prompts/" + definition.FileName, row.Groups[3].Value);
            var documentedTokens = DocumentationTokenRegex().Matches(row.Groups[4].Value)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            var declaredTokens = definition.RequiredTokens.Concat(definition.OptionalTokens).ToArray();
            Assert.Equal(
                declaredTokens.Order(StringComparer.Ordinal),
                documentedTokens.Order(StringComparer.Ordinal));
            Assert.Equal(declaredTokens.Length == 0, row.Groups[4].Value == "None");
        }
    }

    /// <summary>The categorized user guide covers every prompt and defines every declared placeholder.</summary>
    [Fact]
    public static void PromptFileReference_CoversExactCatalogAndPlaceholderGlossary()
    {
        var definitions = PromptAssetCatalog.All.ToArray();
        var operationsPurposes = File.ReadLines(Path.Combine(
                RepositoryRoot,
                "docs",
                "operations",
                "prompts.md"))
            .Select(line => PromptOperationsPurposeRowRegex().Match(line))
            .Where(match => match.Success)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        var referenceLines = File.ReadLines(Path.Combine(
                RepositoryRoot,
                "docs",
                "prompt-file-reference.md"))
            .ToArray();
        var referenceRows = referenceLines
            .Select(line => PromptReferenceRowRegex().Match(line))
            .Where(match => match.Success)
            .ToArray();

        var categoryPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["System and phase prompts"] = "System-",
            ["Context prompts"] = "Context-",
            ["Correction prompts"] = "Correction-",
            ["Tool prompts"] = "Tool-",
            ["Skill prompts"] = "Skill-",
            ["Provider prompts"] = "Provider-",
            ["Adapter prompts"] = "Adapter-",
        };
        var categoriesByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentCategory = null;
        foreach (var line in referenceLines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal)
                && categoryPrefixes.ContainsKey(line[4..]))
            {
                currentCategory = line[4..];
            }

            var row = PromptReferenceRowRegex().Match(line);
            if (row.Success)
            {
                Assert.NotNull(currentCategory);
                categoriesByFile.Add(row.Groups[1].Value, currentCategory);
            }
        }

        Assert.Equal(definitions.Length, referenceRows.Length);
        Assert.Equal(
            definitions.Select(definition => definition.FileName).Order(StringComparer.Ordinal),
            referenceRows.Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        foreach (var definition in definitions)
        {
            var expectedCategory = categoryPrefixes.Single(category => definition.FileName.StartsWith(
                category.Value,
                StringComparison.Ordinal)).Key;
            Assert.Equal(expectedCategory, categoriesByFile[definition.FileName]);
        }

        var summaryCounts = referenceLines
            .Select(line => PromptReferenceSummaryRowRegex().Match(line))
            .Where(match => match.Success)
            .ToDictionary(
                match => match.Groups[1].Value,
                match => int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        Assert.Equal(
            categoryPrefixes.Keys.Order(StringComparer.Ordinal),
            summaryCounts.Keys.Order(StringComparer.Ordinal));
        foreach (var category in categoryPrefixes.Keys)
        {
            Assert.Equal(
                categoriesByFile.Count(item => item.Value == category),
                summaryCounts[category]);
        }

        var totalRows = referenceLines
            .Select(line => PromptReferenceTotalRowRegex().Match(line))
            .Where(match => match.Success)
            .ToArray();
        var totalRow = Assert.Single(totalRows);
        Assert.Equal(
            definitions.Length,
            int.Parse(totalRow.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        foreach (var row in referenceRows)
        {
            var definition = PromptAssetCatalog.Get(row.Groups[1].Value);
            Assert.Equal(operationsPurposes[definition.FileName], row.Groups[2].Value);

            var tokenSections = row.Groups[3].Value.Split("; optional: ", StringSplitOptions.None);
            Assert.InRange(tokenSections.Length, 1, 2);
            var requiredTokens = row.Groups[3].Value == "`None`"
                ? []
                : DocumentationTokenRegex().Matches(tokenSections[0])
                    .Select(match => match.Groups[1].Value)
                    .ToArray();
            var optionalTokens = tokenSections.Length == 2
                ? DocumentationTokenRegex().Matches(tokenSections[1])
                    .Select(match => match.Groups[1].Value)
                    .ToArray()
                : [];
            Assert.Equal(
                definition.RequiredTokens.Order(StringComparer.Ordinal),
                requiredTokens.Order(StringComparer.Ordinal));
            Assert.Equal(
                definition.OptionalTokens.Order(StringComparer.Ordinal),
                optionalTokens.Order(StringComparer.Ordinal));
            foreach (var token in definition.RequiredTokens.Concat(definition.OptionalTokens))
            {
                var expectedLink = $"[`{token}`](#placeholder-{token.ToLowerInvariant()})";
                Assert.True(
                    row.Groups[3].Value.Contains(expectedLink, StringComparison.Ordinal),
                    $"Prompt reference token '{token}' has a missing or incorrect glossary link in {definition.FileName}.");
            }

            Assert.Equal(
                definition.RequiredTokens.Count == 0 && definition.OptionalTokens.Count == 0,
                row.Groups[3].Value == "`None`");
        }

        var declaredTokens = definitions
            .SelectMany(definition => definition.RequiredTokens.Concat(definition.OptionalTokens))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var glossaryRows = referenceLines
            .Select(line => PromptReferenceGlossaryRowRegex().Match(line))
            .Where(match => match.Success)
            .ToArray();
        Assert.Equal(declaredTokens.Length, glossaryRows.Length);
        Assert.Equal(
            declaredTokens,
            glossaryRows.Select(match => match.Groups[2].Value).Order(StringComparer.Ordinal));
        foreach (var row in glossaryRows)
        {
            Assert.Equal(row.Groups[2].Value.ToLowerInvariant(), row.Groups[1].Value);
        }
    }

    /// <summary>Governed model/tool/provider sinks cannot reintroduce meaningful direct string literals.</summary>
    [Fact]
    public static void GovernedPromptSinks_HaveNoMeaningfulDirectStringLiterals()
    {
        var violations = new List<string>();
        var usedExclusions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in GovernedSourceProjects
            .Select(owner => Path.Combine(RepositoryRoot, "src", owner))
            .SelectMany(directory => Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains(
                Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
        {
            var root = CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                cancellationToken: TestContext.Current.CancellationToken).GetRoot(
                    TestContext.Current.CancellationToken);
            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not IdentifierNameSyntax identifier)
                {
                    continue;
                }

                var objectCreation = assignment.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
                var typeName = objectCreation?.Type.ToString();
                var isDirectSink = (identifier.Identifier.ValueText == "Description"
                        && typeName is "ToolDefinition" or "ModelToolDefinition")
                    || (identifier.Identifier.ValueText == "Content"
                        && typeName is "ModelContentPart" or "ModelMessage" or "ModelProviderInstructions");
                var isStructuredSink = identifier.Identifier.ValueText == "CurrentTurnHostContext"
                    || (typeName is not null
                        && GovernedStructuredTextProperties.TryGetValue(typeName, out var properties)
                        && properties.Contains(identifier.Identifier.ValueText));
                if (isDirectSink && ContainsMeaningfulLiteral(assignment.Right, assignment))
                {
                    AddViolationUnlessExcluded(
                        violations,
                        path,
                        assignment,
                        assignment.Right,
                        usedExclusions);
                }

                if (isStructuredSink)
                {
                    AddStructuredViolationUnlessExcluded(
                        violations,
                        path,
                        assignment,
                        assignment.Right,
                        usedExclusions);
                }
            }

            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.ValueText == "CurrentTurnHostContext")
                {
                    AddStructuredViolationUnlessExcluded(
                        violations,
                        path,
                        assignment,
                        assignment.Right,
                        usedExclusions);
                }
            }

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var typeName = property.Ancestors().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault()?.Identifier.ValueText;
                if (property.Initializer is not null
                    && typeName is not null
                    && GovernedStructuredTextProperties.TryGetValue(typeName, out var properties)
                    && properties.Contains(property.Identifier.ValueText))
                {
                    AddStructuredViolationUnlessExcluded(
                        violations,
                        path,
                        property,
                        property.Initializer.Value,
                        usedExclusions);
                }
            }

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeName = creation.Type.ToString();
                if (creation.ArgumentList is not { } argumentList)
                {
                    continue;
                }

                foreach (var expression in EnumerateGovernedStructuredArguments(
                    typeName,
                    argumentList.Arguments))
                {
                    AddStructuredViolationUnlessExcluded(
                        violations,
                        path,
                        creation,
                        expression,
                        usedExclusions);
                }
            }

            foreach (var creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                var typeName = GetImplicitObjectCreationTypeName(creation);
                if (typeName is null)
                {
                    continue;
                }

                foreach (var expression in EnumerateGovernedStructuredArguments(
                    typeName,
                    creation.ArgumentList.Arguments))
                {
                    AddStructuredViolationUnlessExcluded(
                        violations,
                        path,
                        creation,
                        expression,
                        usedExclusions);
                }
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression switch
                {
                    IdentifierNameSyntax direct => direct.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => string.Empty,
                };
                var arguments = invocation.ArgumentList.Arguments;
                if (IsStructuredFactMarker(invocation))
                {
                    if (!IsWithinGovernedStructuredSink(invocation)
                        || arguments.Count != 1
                        || ContainsObviousModelDirectedAction(arguments[0].Expression))
                    {
                        AddViolation(violations, path, invocation);
                    }

                    continue;
                }

                var governedContentArgument = GetGovernedContentArgument(name, arguments);
                if (governedContentArgument is not null
                    && ContainsMeaningfulLiteral(governedContentArgument, invocation))
                {
                    AddViolationUnlessExcluded(
                        violations,
                        path,
                        invocation,
                        governedContentArgument,
                        usedExclusions);
                }

                if (name == "Create"
                    && invocation.Expression.ToString().StartsWith(
                        "ToolDefinitionFactory.Create",
                        StringComparison.Ordinal)
                    && arguments.Count > 1)
                {
                    AddViolationUnlessExcluded(
                        violations,
                        path,
                        invocation,
                        arguments[1].Expression,
                        usedExclusions);
                }

                if (name is "Add" or "AddRange"
                    && invocation.Expression is MemberAccessExpressionSyntax collectionAccess
                    && collectionAccess.Expression is IdentifierNameSyntax collectionIdentifier
                    && collectionIdentifier.Identifier.ValueText.Contains(
                        "omission",
                        StringComparison.OrdinalIgnoreCase)
                    && arguments.Count > 0)
                {
                    foreach (var argument in arguments)
                    {
                        AddStructuredViolationUnlessExcluded(
                            violations,
                            path,
                            invocation,
                            argument.Expression,
                            usedExclusions);
                    }
                }

                var promptFileArgument = GetPromptFileArgument(invocation, name, arguments);
                if (promptFileArgument is not null)
                {
                    if (ContainsMeaningfulLiteral(promptFileArgument, invocation))
                    {
                        AddViolation(violations, path, invocation);
                    }

                    var promptTokensArgument = GetPromptTokensArgument(name, arguments);
                    if (promptTokensArgument is not null)
                    {
                        foreach (var tokenValue in EnumeratePromptRenderTokenValues(
                            promptTokensArgument,
                            invocation))
                        {
                            AddStructuredViolationUnlessExcluded(
                                violations,
                                path,
                                invocation,
                                tokenValue,
                                usedExclusions);
                        }
                    }
                }

                if (name is "Append" or "AppendLine"
                    && (IsGovernedRenderer(invocation) || IsGovernedModelTextBuilder(invocation))
                    && arguments.Count > 0)
                {
                    AddStructuredViolationUnlessExcluded(
                        violations,
                        path,
                        invocation,
                        arguments[0].Expression,
                        usedExclusions);
                }

                if (name is "Add" or "AddRange"
                    && IsGovernedRenderer(invocation))
                {
                    foreach (var argument in arguments)
                    {
                        AddStructuredViolationUnlessExcluded(
                            violations,
                            path,
                            invocation,
                            argument.Expression,
                            usedExclusions);
                    }
                }
            }

            foreach (var returnStatement in root.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (returnStatement.Expression is not null && IsGovernedRenderer(returnStatement))
                {
                    AddRendererViolationUnlessExcluded(
                        violations,
                        path,
                        returnStatement,
                        returnStatement.Expression,
                        usedExclusions);
                }
            }

            foreach (var arrowExpression in root.DescendantNodes().OfType<ArrowExpressionClauseSyntax>())
            {
                if (IsGovernedRenderer(arrowExpression))
                {
                    AddRendererViolationUnlessExcluded(
                        violations,
                        path,
                        arrowExpression,
                        arrowExpression.Expression,
                        usedExclusions);
                }
            }

            foreach (var coalesceExpression in root.DescendantNodes()
                .OfType<BinaryExpressionSyntax>()
                .Where(expression => expression.IsKind(SyntaxKind.CoalesceExpression)))
            {
                if (IsToolResultFallbackChain(coalesceExpression)
                    && ContainsMeaningfulLiteral(coalesceExpression, coalesceExpression))
                {
                    AddViolationUnlessExcluded(
                        violations,
                        path,
                        coalesceExpression,
                        coalesceExpression,
                        usedExclusions);
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Governed prompt sinks contain direct meaningful literals:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
        var declaredExclusions = GovernedLiteralExclusions
            .SelectMany(entry => entry.Value.Select(fragment => CreateExclusionKey(entry.Key, fragment)))
            .Order(StringComparer.Ordinal);
        Assert.Equal(declaredExclusions, usedExclusions.Order(StringComparer.Ordinal));
    }

    /// <summary>The structured-fact marker is limited to governed factual DTO fields.</summary>
    [Fact]
    public static void StructuredFactMarker_IsLimitedToGovernedFactualFields()
    {
        var markedFact = ParseObjectCreation(
            "new CodeExploreAvailability(Reason: ModelVisibleStructuredFact.Exact(\"The graph bound was reached.\"))");
        var markedFactInvocation = Assert.Single(
            markedFact.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            IsStructuredFactMarker);
        Assert.True(IsWithinGovernedStructuredSink(markedFactInvocation));
        Assert.False(ContainsObviousModelDirectedAction(
            Assert.Single(markedFactInvocation.ArgumentList.Arguments).Expression));
        Assert.Empty(EnumerateGovernedStructuredArguments(
                markedFact.Type.ToString(),
                Assert.IsType<ArgumentListSyntax>(markedFact.ArgumentList).Arguments)
            .SelectMany(expression => GetMeaningfulLiteralFragments(
                expression,
                markedFact,
                traverseInvocationArguments: true)));

        var unmarkedFact = ParseObjectCreation(
            "new CodeExploreAvailability(Reason: \"The graph bound was reached.\")");
        Assert.NotEmpty(EnumerateGovernedStructuredArguments(
                unmarkedFact.Type.ToString(),
                Assert.IsType<ArgumentListSyntax>(unmarkedFact.ArgumentList).Arguments)
            .SelectMany(expression => GetMeaningfulLiteralFragments(
                expression,
                unmarkedFact,
                traverseInvocationArguments: true)));

        var markedAction = ParseObjectCreation(
            "new CodeExploreAvailability(Reason: ModelVisibleStructuredFact.Exact(\"Retry with a smaller graph bound.\"))");
        var markedActionInvocation = Assert.Single(
            markedAction.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            IsStructuredFactMarker);
        Assert.True(IsWithinGovernedStructuredSink(markedActionInvocation));
        Assert.True(ContainsObviousModelDirectedAction(
            Assert.Single(markedActionInvocation.ArgumentList.Arguments).Expression));

        var unmarkedAction = ParseObjectCreation(
            "new CodeExploreAvailability(Reason: \"Retry with a smaller graph bound.\")");
        Assert.NotEmpty(EnumerateGovernedStructuredArguments(
                unmarkedAction.Type.ToString(),
                Assert.IsType<ArgumentListSyntax>(unmarkedAction.ArgumentList).Arguments)
            .SelectMany(expression => GetMeaningfulLiteralFragments(
                expression,
                unmarkedAction,
                traverseInvocationArguments: true)));

        var markerInNonGovernedMember = ParseObjectCreation(
            "new CodeExploreAvailability(Available: ModelVisibleStructuredFact.Exact(\"not a governed field\"))");
        var nonGovernedInvocation = Assert.Single(
            markerInNonGovernedMember.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            IsStructuredFactMarker);
        Assert.False(IsWithinGovernedStructuredSink(nonGovernedInvocation));

        var markerInConcatenation = ParseObjectCreation(
            "new CodeExploreAvailability(Reason: \"prefix \" + ModelVisibleStructuredFact.Exact(\"fact\"))");
        var concatenatedInvocation = Assert.Single(
            markerInConcatenation.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            IsStructuredFactMarker);
        Assert.False(IsWithinGovernedStructuredSink(concatenatedInvocation));

        var nestedMarker = ParseObjectCreation(
            "new CodeExploreAvailability(Reason: Wrap(ModelVisibleStructuredFact.Exact(\"fact\")))");
        var nestedInvocation = Assert.Single(
            nestedMarker.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            IsStructuredFactMarker);
        Assert.False(IsWithinGovernedStructuredSink(nestedInvocation));
    }

    private static ObjectCreationExpressionSyntax ParseObjectCreation(string source)
    {
        return Assert.IsType<ObjectCreationExpressionSyntax>(
            SyntaxFactory.ParseExpression(source));
    }

    private static void AddViolation(List<string> violations, string path, SyntaxNode node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        violations.Add(Path.GetRelativePath(RepositoryRoot, path) + ":" + line);
    }

    private static void AddViolation(
        List<string> violations,
        string path,
        SyntaxNode node,
        IReadOnlyList<string> literalFragments)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var symbol = GetContainingSymbol(node);
        violations.Add(
            Path.GetRelativePath(RepositoryRoot, path)
                + ":"
                + line
                + (symbol is null ? string.Empty : " (" + symbol + ")")
                + " => "
                + string.Join(" | ", literalFragments.Select(fragment => fragment.ReplaceLineEndings("\\n"))));
    }

    private static void AddRendererViolationUnlessExcluded(
        List<string> violations,
        string path,
        SyntaxNode sink,
        ExpressionSyntax expression,
        HashSet<string> usedExclusions)
    {
        AddViolationUnlessExcluded(violations, path, sink, expression, usedExclusions);
    }

    private static void AddViolationUnlessExcluded(
        List<string> violations,
        string path,
        SyntaxNode sink,
        ExpressionSyntax expression,
        HashSet<string> usedExclusions)
    {
        var literalFragments = GetMeaningfulLiteralFragments(expression, sink)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (literalFragments.Length == 0)
        {
            return;
        }

        var symbol = GetContainingSymbol(sink);
        if (AreGovernedLiteralFragmentsExcluded(symbol, literalFragments, usedExclusions))
        {
            return;
        }

        AddViolation(violations, path, sink, literalFragments);
    }

    private static void AddStructuredViolationUnlessExcluded(
        List<string> violations,
        string path,
        SyntaxNode sink,
        ExpressionSyntax expression,
        HashSet<string> usedExclusions)
    {
        var literalFragments = GetMeaningfulLiteralFragments(
                expression,
                sink,
                traverseInvocationArguments: true)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (literalFragments.Length == 0)
        {
            return;
        }

        var symbol = GetContainingSymbol(sink);
        if (!AreGovernedLiteralFragmentsExcluded(symbol, literalFragments, usedExclusions))
        {
            AddViolation(violations, path, sink, literalFragments);
        }
    }

    private static bool AreGovernedLiteralFragmentsExcluded(
        string? symbol,
        IReadOnlyList<string> literalFragments,
        HashSet<string> usedExclusions)
    {
        var areExcluded = symbol is not null
            && GovernedLiteralExclusions.TryGetValue(symbol, out var exclusions)
            && literalFragments.All(exclusions.Contains);
        if (areExcluded && symbol is not null)
        {
            foreach (var fragment in literalFragments)
            {
                _ = usedExclusions.Add(CreateExclusionKey(symbol, fragment));
            }
        }

        return areExcluded;
    }

    private static string CreateExclusionKey(string symbol, string fragment)
    {
        return symbol + "\0" + fragment;
    }

    private static bool ContainsMeaningfulLiteral(ExpressionSyntax expression, SyntaxNode sink)
    {
        return GetMeaningfulLiteralFragments(expression, sink).Any();
    }

    private static IEnumerable<string> GetMeaningfulLiteralFragments(
        ExpressionSyntax expression,
        SyntaxNode sink,
        HashSet<string>? visitedLocals = null,
        bool traverseInvocationArguments = false)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                if (literal.Token.ValueText.Any(char.IsLetter))
                {
                    yield return literal.Token.ValueText;
                }

                yield break;
            case InterpolatedStringExpressionSyntax interpolated:
                foreach (var text in interpolated.Contents.OfType<InterpolatedStringTextSyntax>())
                {
                    if (text.TextToken.ValueText.Any(char.IsLetter))
                    {
                        yield return text.TextToken.ValueText;
                    }
                }

                yield break;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
                || binary.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var fragment in GetMeaningfulLiteralFragments(
                    binary.Left,
                    sink,
                    visitedLocals,
                    traverseInvocationArguments))
                {
                    yield return fragment;
                }

                foreach (var fragment in GetMeaningfulLiteralFragments(
                    binary.Right,
                    sink,
                    visitedLocals,
                    traverseInvocationArguments))
                {
                    yield return fragment;
                }

                yield break;
            case ConditionalExpressionSyntax conditional:
                foreach (var fragment in GetMeaningfulLiteralFragments(
                    conditional.WhenTrue,
                    sink,
                    visitedLocals,
                    traverseInvocationArguments))
                {
                    yield return fragment;
                }

                foreach (var fragment in GetMeaningfulLiteralFragments(
                    conditional.WhenFalse,
                    sink,
                    visitedLocals,
                    traverseInvocationArguments))
                {
                    yield return fragment;
                }

                yield break;
            case ParenthesizedExpressionSyntax parenthesized:
                foreach (var fragment in GetMeaningfulLiteralFragments(
                    parenthesized.Expression,
                    sink,
                    visitedLocals,
                    traverseInvocationArguments))
                {
                    yield return fragment;
                }

                yield break;
            case CollectionExpressionSyntax collection:
                foreach (var item in collection.Elements.OfType<ExpressionElementSyntax>())
                {
                    foreach (var fragment in GetMeaningfulLiteralFragments(
                        item.Expression,
                        sink,
                        visitedLocals,
                        traverseInvocationArguments))
                    {
                        yield return fragment;
                    }
                }

                yield break;
            case InvocationExpressionSyntax invocation:
                var invocationName = GetInvocationName(invocation);
                if (traverseInvocationArguments && IsStructuredFactMarker(invocation))
                {
                    yield break;
                }

                var contentArgument = GetGovernedContentArgument(
                    invocationName,
                    invocation.ArgumentList.Arguments);
                contentArgument ??= invocationName == "PrefixLegacySection"
                    ? invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    : null;
                if (contentArgument is not null)
                {
                    foreach (var fragment in GetMeaningfulLiteralFragments(
                        contentArgument,
                        sink,
                        visitedLocals,
                        traverseInvocationArguments))
                    {
                        yield return fragment;
                    }
                }

                if (contentArgument is null && traverseInvocationArguments)
                {
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        foreach (var fragment in GetMeaningfulLiteralFragments(
                            argument.Expression,
                            sink,
                            visitedLocals,
                            traverseInvocationArguments))
                        {
                            yield return fragment;
                        }
                    }
                }

                yield break;
            case IdentifierNameSyntax identifier:
                visitedLocals ??= new HashSet<string>(StringComparer.Ordinal);
                if (!visitedLocals.Add(identifier.Identifier.ValueText))
                {
                    yield break;
                }

                var initializer = FindLocalInitializer(identifier, sink);
                if (initializer is not null)
                {
                    foreach (var fragment in GetMeaningfulLiteralFragments(
                        initializer,
                        sink,
                        visitedLocals,
                        traverseInvocationArguments))
                    {
                        yield return fragment;
                    }
                }

                yield break;
        }
    }

    private static ExpressionSyntax? FindLocalInitializer(IdentifierNameSyntax identifier, SyntaxNode sink)
    {
        var method = sink.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null)
        {
            return null;
        }

        return method.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.SpanStart < identifier.SpanStart
                && variable.Identifier.ValueText == identifier.Identifier.ValueText)
            .OrderByDescending(variable => variable.SpanStart)
            .Select(variable => variable.Initializer?.Value)
            .FirstOrDefault(value => value is not null);
    }

    private static IEnumerable<ExpressionSyntax> EnumeratePromptRenderTokenValues(
        ExpressionSyntax expression,
        SyntaxNode sink,
        HashSet<string>? visitedLocals = null)
    {
        switch (expression)
        {
            case ObjectCreationExpressionSyntax { Initializer: { } objectInitializer }:
                foreach (var value in EnumeratePromptRenderTokenValues(objectInitializer, sink, visitedLocals))
                {
                    yield return value;
                }

                yield break;
            case ImplicitObjectCreationExpressionSyntax { Initializer: { } implicitObjectInitializer }:
                foreach (var value in EnumeratePromptRenderTokenValues(implicitObjectInitializer, sink, visitedLocals))
                {
                    yield return value;
                }

                yield break;
            case InitializerExpressionSyntax complexInitializer
                when complexInitializer.IsKind(SyntaxKind.ComplexElementInitializerExpression):
                foreach (var value in complexInitializer.Expressions.Skip(1))
                {
                    yield return value;
                }

                yield break;
            case InitializerExpressionSyntax generalInitializer:
                foreach (var item in generalInitializer.Expressions)
                {
                    if (item is AssignmentExpressionSyntax assignment)
                    {
                        yield return assignment.Right;
                        continue;
                    }

                    foreach (var value in EnumeratePromptRenderTokenValues(item, sink, visitedLocals))
                    {
                        yield return value;
                    }
                }

                yield break;
            case InvocationExpressionSyntax invocation when GetInvocationName(invocation) == "Tokens":
                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    foreach (var value in EnumeratePromptRenderTokenValues(
                        argument.Expression,
                        sink,
                        visitedLocals))
                    {
                        yield return value;
                    }
                }

                yield break;
            case TupleExpressionSyntax tuple when tuple.Arguments.Count >= 2:
                yield return tuple.Arguments[1].Expression;
                yield break;
            case CollectionExpressionSyntax collection:
                foreach (var item in collection.Elements.OfType<ExpressionElementSyntax>())
                {
                    foreach (var value in EnumeratePromptRenderTokenValues(
                        item.Expression,
                        sink,
                        visitedLocals))
                    {
                        yield return value;
                    }
                }

                yield break;
            case ArrayCreationExpressionSyntax { Initializer: { } arrayInitializer }:
                foreach (var value in EnumeratePromptRenderTokenValues(arrayInitializer, sink, visitedLocals))
                {
                    yield return value;
                }

                yield break;
            case ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitArrayInitializer }:
                foreach (var value in EnumeratePromptRenderTokenValues(implicitArrayInitializer, sink, visitedLocals))
                {
                    yield return value;
                }

                yield break;
            case ParenthesizedExpressionSyntax parenthesized:
                foreach (var value in EnumeratePromptRenderTokenValues(
                    parenthesized.Expression,
                    sink,
                    visitedLocals))
                {
                    yield return value;
                }

                yield break;
            case IdentifierNameSyntax identifier:
                visitedLocals ??= new HashSet<string>(StringComparer.Ordinal);
                if (!visitedLocals.Add(identifier.Identifier.ValueText))
                {
                    yield break;
                }

                var initializer = FindLocalInitializer(identifier, sink);
                if (initializer is not null)
                {
                    foreach (var value in EnumeratePromptRenderTokenValues(initializer, sink, visitedLocals))
                    {
                        yield return value;
                    }
                }

                yield break;
        }
    }

    private static ExpressionSyntax? GetGovernedContentArgument(
        string invocationName,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var ordinal = invocationName switch
        {
            "CreateDeveloperCorrectionMessage" or "CreateMessage" or "CreateTextMessage"
                or "CreateToolResultMessage" => 2,
            "CreateToolBatchDiagnostic" => 3,
            "CreateJsonContentPart" or "CreateTextContentPart" => 0,
            _ => -1,
        };
        if (ordinal < 0)
        {
            return null;
        }

        var named = arguments.FirstOrDefault(argument =>
            argument.NameColon?.Name.Identifier.ValueText == "content");
        return named?.Expression ?? (arguments.Count > ordinal ? arguments[ordinal].Expression : null);
    }

    private static IEnumerable<ExpressionSyntax> EnumerateGovernedStructuredArguments(
        string typeName,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        if (GovernedStructuredTextProperties.TryGetValue(typeName, out var properties))
        {
            foreach (var argument in arguments.Where(argument => argument.NameColon is not null))
            {
                if (properties.Contains(argument.NameColon!.Name.Identifier.ValueText))
                {
                    yield return argument.Expression;
                }
            }
        }

        if (!GovernedStructuredTextOrdinals.TryGetValue(typeName, out var ordinals))
        {
            yield break;
        }

        foreach (var ordinal in ordinals.Where(index => arguments.Count > index))
        {
            var argument = arguments[ordinal];
            if (argument.NameColon is null)
            {
                yield return argument.Expression;
            }
        }
    }

    private static string? GetImplicitObjectCreationTypeName(ImplicitObjectCreationExpressionSyntax creation)
    {
        if (creation.Parent is EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax
                {
                    Parent: VariableDeclarationSyntax declaration,
                },
            })
        {
            return declaration.Type.ToString();
        }

        var methodReturnType = creation.Ancestors().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault()?.ReturnType;
        return methodReturnType switch
        {
            GenericNameSyntax generic when generic.TypeArgumentList.Arguments.Count == 1 =>
                generic.TypeArgumentList.Arguments[0].ToString(),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
    }

    private static bool IsStructuredFactMarker(InvocationExpressionSyntax invocation)
    {
        return GetInvocationName(invocation) == "Exact"
            && invocation.Expression.ToString().StartsWith(
                "ModelVisibleStructuredFact.Exact",
                StringComparison.Ordinal);
    }

    private static bool IsWithinGovernedStructuredSink(InvocationExpressionSyntax marker)
    {
        foreach (var ancestor in marker.Ancestors())
        {
            if (ancestor is InvocationExpressionSyntax invocation
                && GetInvocationName(invocation) is "Add" or "AddRange"
                && invocation.Expression is MemberAccessExpressionSyntax collectionAccess
                && collectionAccess.Expression is IdentifierNameSyntax collectionIdentifier
                && collectionIdentifier.Identifier.ValueText.Contains(
                    "omission",
                    StringComparison.OrdinalIgnoreCase)
                && invocation.ArgumentList.Arguments.Any(argument =>
                    IsCompleteStructuredFactValue(argument.Expression, marker)))
            {
                return true;
            }

            if (ancestor is ObjectCreationExpressionSyntax creation
                && creation.ArgumentList is { } argumentList
                && EnumerateGovernedStructuredArguments(
                        creation.Type.ToString(),
                        argumentList.Arguments)
                    .Any(expression => IsCompleteStructuredFactValue(expression, marker)))
            {
                return true;
            }

            if (ancestor is ImplicitObjectCreationExpressionSyntax implicitCreation
                && GetImplicitObjectCreationTypeName(implicitCreation) is { } implicitType
                && EnumerateGovernedStructuredArguments(
                        implicitType,
                        implicitCreation.ArgumentList.Arguments)
                    .Any(expression => IsCompleteStructuredFactValue(expression, marker)))
            {
                return true;
            }

            if (ancestor is AssignmentExpressionSyntax assignment)
            {
                var propertyName = assignment.Left switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null,
                };
                if (propertyName == "CurrentTurnHostContext"
                    || (propertyName is not null
                        && assignment.Ancestors().OfType<ObjectCreationExpressionSyntax>()
                            .FirstOrDefault() is { } assignedCreation
                        && GovernedStructuredTextProperties.TryGetValue(
                            assignedCreation.Type.ToString(),
                            out var properties)
                        && properties.Contains(propertyName)))
                {
                    return IsCompleteStructuredFactValue(assignment.Right, marker);
                }
            }

            if (ancestor is MethodDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    private static bool IsCompleteStructuredFactValue(
        ExpressionSyntax expression,
        InvocationExpressionSyntax marker)
    {
        if (ReferenceEquals(expression, marker))
        {
            return true;
        }

        return expression switch
        {
            ParenthesizedExpressionSyntax parenthesized =>
                IsCompleteStructuredFactValue(parenthesized.Expression, marker),
            CastExpressionSyntax cast => IsCompleteStructuredFactValue(cast.Expression, marker),
            ConditionalExpressionSyntax conditional =>
                IsCompleteStructuredFactValue(conditional.WhenTrue, marker)
                    || IsCompleteStructuredFactValue(conditional.WhenFalse, marker),
            CollectionExpressionSyntax collection => collection.Elements
                .OfType<ExpressionElementSyntax>()
                .Any(element => IsCompleteStructuredFactValue(element.Expression, marker)),
            ArrayCreationExpressionSyntax { Initializer: { } initializer } =>
                initializer.Expressions.Any(item => IsCompleteStructuredFactValue(item, marker)),
            ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer } =>
                initializer.Expressions.Any(item => IsCompleteStructuredFactValue(item, marker)),
            _ => false,
        };
    }

    private static bool ContainsObviousModelDirectedAction(ExpressionSyntax expression)
    {
        var fragments = GetMeaningfulLiteralFragments(expression, expression)
            .Select(fragment => fragment.Trim())
            .ToArray();
        return fragments.Any(fragment =>
            fragment.StartsWith("Retry ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Use ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Provide ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Return ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Continue ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Increase ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Inspect ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Resolve ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Execute ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Publish ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Declare ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Replace ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Do not ", StringComparison.OrdinalIgnoreCase)
            || fragment.StartsWith("Rely ", StringComparison.OrdinalIgnoreCase)
            || fragment.Contains(" must ", StringComparison.OrdinalIgnoreCase)
            || fragment.Contains("cannot alter", StringComparison.OrdinalIgnoreCase));
    }

    private static ExpressionSyntax? GetPromptFileArgument(
        InvocationExpressionSyntax invocation,
        string invocationName,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        if (invocationName is "Get" or "Render"
            && invocation.Expression is MemberAccessExpressionSyntax loaderAccess
            && loaderAccess.Expression.ToString().Contains("prompt", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.FirstOrDefault()?.Expression;
        }

        var ordinal = invocationName switch
        {
            "GetPromptValue" or "RenderPrompt" => 0,
            "AppendPromptBlock" or "RenderWithPlatformLineEndings" => 1,
            "CreateDefinition" when arguments.Count > 2 => 2,
            _ => -1,
        };
        return ordinal >= 0 && arguments.Count > ordinal
            ? arguments[ordinal].Expression
            : null;
    }

    private static ExpressionSyntax? GetPromptTokensArgument(
        string invocationName,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var ordinal = invocationName switch
        {
            "Render" or "RenderPrompt" => 1,
            "AppendPromptBlock" or "RenderWithPlatformLineEndings" => 2,
            _ => -1,
        };
        return ordinal >= 0 && arguments.Count > ordinal
            ? arguments[ordinal].Expression
            : null;
    }

    private static string GetInvocationName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax direct => direct.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty,
        };
    }

    private static bool IsGovernedRenderer(SyntaxNode node)
    {
        var type = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return type is not null && GovernedRendererTypes.Contains(type.Identifier.ValueText);
    }

    private static bool IsGovernedModelTextBuilder(SyntaxNode node)
    {
        return GetContainingSymbol(node) is { } symbol
            && GovernedModelTextBuilderMethods.Contains(symbol);
    }

    private static bool IsToolResultFallbackChain(BinaryExpressionSyntax expression)
    {
        var memberNames = expression.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Select(access => access.Name.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        return memberNames.Contains("ModelResultContent")
            && memberNames.Contains("ResultJson")
            && memberNames.Contains("Error");
    }

    private static string? GetContainingSymbol(SyntaxNode node)
    {
        var type = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return type is null || method is null
            ? null
            : type.Identifier.ValueText + "." + method.Identifier.ValueText;
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PromptTokenRegex();

    [GeneratedRegex(
        @"^\| `([^`]+\.md)` \| `([^`]+)` \| `([^`]+)` \| [^|]* \| ([^|]+) \|$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PromptDocumentationRowRegex();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentationTokenRegex();

    [GeneratedRegex(
        @"^\| `([^`]+\.md)` \| `[^`]+` \| `[^`]+` \| ([^|]+) \| [^|]+ \|$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PromptOperationsPurposeRowRegex();

    [GeneratedRegex(
        @"^\| `([^`]+\.md)` \| ([^|]+) \| ([^|]+) \|$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PromptReferenceRowRegex();

    [GeneratedRegex(
        "^\\| <a id=\"placeholder-([a-z0-9]+)\"></a>`([^`]+)` \\| [^|]+ \\|$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PromptReferenceGlossaryRowRegex();

    [GeneratedRegex(@"^\| ([^|]+) \| (\d+) \| [^|]+ \|$", RegexOptions.CultureInvariant)]
    private static partial Regex PromptReferenceSummaryRowRegex();

    [GeneratedRegex(@"^\| \*\*Total\*\* \| \*\*(\d+)\*\* \| [^|]+ \|$", RegexOptions.CultureInvariant)]
    private static partial Regex PromptReferenceTotalRowRegex();
}
