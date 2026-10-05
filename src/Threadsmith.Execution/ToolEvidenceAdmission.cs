namespace Threadsmith.Execution;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Admits tool results and source dependencies into the governed evidence store.</summary>
internal static class ToolEvidenceAdmission
{
    /// <summary>Admits tool output with every declared source dependency and available file digest.</summary>
    public static async Task<EvidenceId> AdmitEvidenceAsync(
        IEvidenceStore store, SessionId sessionId, RunId runId, string repositoryPath, ToolInvocationResult result, string content, CancellationToken cancellationToken)
    {
        var source = result.Sources.FirstOrDefault();
        var dependencies = GetFileDependencies(result, repositoryPath);
        var range = dependencies.FirstOrDefault()?.Range;
        var evidenceId = EvidenceId.New();
        await store.AddAsync(
            new Evidence
            {
                EvidenceId = evidenceId,
                SessionId = sessionId,
                RunId = runId,
                Kind = result.Succeeded ? EvidenceKind.ToolResult : EvidenceKind.Failure,
                Content = content,
                Provenance = new EvidenceProvenance
                {
                    RepositoryPath = repositoryPath,
                    SourcePath = source?.Identifier,
                    SourceRange = range,
                    ToolInvocationId = result.ToolInvocationId,
                    SemanticConfidence = ReadSemanticConfidence(result.ToolId, result.ResultJson ?? content),
                    Source = $"tool:{result.ToolId}",
                },
                FileDependencies = dependencies,
                CollectedAt = DateTimeOffset.UtcNow,
                Relevance = result.Succeeded ? 0.8 : 1,
                EstimatedTokens = Math.Max(1, (content.Length + 3) / 4),
                InvalidationKeys = IsSemanticEvidenceTool(result.ToolId) ? ["repository", "semantic"] : ["repository"],
            },
            cancellationToken);
        return evidenceId;
    }

    /// <summary>Extracts every declared file dependency for parent and child evidence admission.</summary>
    public static IReadOnlyList<EvidenceFileDependency> GetFileDependencies(ToolInvocationResult result, string repositoryPath)
    {
        ReadFileOutput? read = null;
        if (result.Succeeded && result.ToolId == "read_file" && result.ResultJson is { } json)
        {
            try
            {
                read = JsonSerializer.Deserialize<ReadFileOutput>(json);
            }
            catch (JsonException)
            {
                // Bounded/omitted tool output cannot establish a source identity.
            }
        }

        if (string.IsNullOrWhiteSpace(read?.Path))
        {
            read = null;
        }

        var range = read?.EndLine is { } end ? new SourceRange(read.StartLine, 1, end, 1) : null;
        var comparer = RepositoryPathPolicy.GetPathComparer(repositoryPath);
        return result.Sources.Where(item => item.Kind.Equals("file", StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var path = SourceEvidence.NormalizePath(repositoryPath, item.Identifier);
                var digest = read is not null && comparer.Equals(SourceEvidence.NormalizePath(repositoryPath, read.Path), path)
                    ? read.FileSha256 ?? read.ContentDigest : null;
                return new EvidenceFileDependency(path, digest, range);
            }).ToArray();
    }

    private static SemanticConfidenceLevel ReadSemanticConfidence(string toolId, string content)
    {
        if (!IsSemanticEvidenceTool(toolId))
        {
            return SemanticConfidenceLevel.None;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadSemanticConfidence(document.RootElement);
        }
        catch (JsonException)
        {
            return SemanticConfidenceLevel.None;
        }
    }

    private static bool IsSemanticEvidenceTool(string toolId)
    {
        return toolId is "code_explore"
            or "find_symbol"
            or "find_references"
            or "find_implementations"
            or "call_hierarchy"
            or "symbol_impact"
            or "csharp_pattern_search"
            or "generated_code_query";
    }

    private static SemanticConfidenceLevel ReadSemanticConfidence(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryReadConfidenceProperty(element, "confidence", out var confidence)
                || TryReadConfidenceProperty(element, "semanticConfidence", out confidence))
            {
                return confidence;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var confidence = ReadSemanticConfidence(item);
                if (confidence != SemanticConfidenceLevel.None)
                {
                    return confidence;
                }
            }
        }

        return SemanticConfidenceLevel.None;
    }

    private static bool TryReadConfidenceProperty(
        JsonElement element,
        string propertyName,
        out SemanticConfidenceLevel confidence)
    {
        confidence = SemanticConfidenceLevel.None;
        JsonElement? matched = null;
        foreach (var propertyItem in element.EnumerateObject())
        {
            if (string.Equals(propertyItem.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                matched = propertyItem.Value;
                break;
            }
        }

        if (matched is not { } property)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String
            && Enum.TryParse(property.GetString(), ignoreCase: true, out confidence)
            && Enum.IsDefined(confidence))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(SemanticConfidenceLevel), numeric))
        {
            confidence = (SemanticConfidenceLevel)numeric;
            return true;
        }

        return false;
    }
}
