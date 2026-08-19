namespace Threadsmith.Scripting.Worker;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;

/// <summary>Reads one bounded script request from standard input and writes one JSON result.</summary>
public static class Program
{
    private static readonly string[] _prohibitedNamespacePrefixes =
    [
        "System.Diagnostics",
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "System.Runtime.Loader",
        "Microsoft.Win32",
    ];

    private static readonly string[] _prohibitedTypeNames =
    [
        "System.Activator",
        "System.AppContext",
        "System.AppDomain",
        "System.Console",
        "System.Environment",
        "System.Type",
    ];

    /// <summary>Runs one fresh Roslyn script invocation.</summary>
    public static async Task<int> Main()
    {
        var requestJson = await Console.In.ReadToEndAsync();
        ScriptWorkerResult result;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ScriptWorkerRequest request = JsonSerializer.Deserialize<ScriptWorkerRequest>(
                requestJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The scripting request was empty.");
            if (string.IsNullOrWhiteSpace(request.Code)
                || request.Code.Length > 64 * 1024
                || request.MaximumOutputBytes is < 256 or > 1024 * 1024
                || request.AllowedAssemblies.Count > 32
                || request.Kind is not ("Expression" or "Statement"))
            {
                throw new InvalidOperationException("The scripting request exceeded a worker boundary.");
            }

            var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
                ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                ?? [];
            var platformAssemblies = trustedPlatformAssemblies
                .GroupBy(
                    path => Path.GetFileNameWithoutExtension(path),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            var references = new List<Assembly> { typeof(object).Assembly };
            foreach (var assemblyName in request.AllowedAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(assemblyName)
                    || assemblyName.Length > 128
                    || !assemblyName.StartsWith("System.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Assembly '{assemblyName}' is not allowed or unavailable.");
                }

                var referenceName = assemblyName;
                string? assemblyPath;
                while (!platformAssemblies.TryGetValue(referenceName, out assemblyPath))
                {
                    var separator = referenceName.LastIndexOf('.');
                    if (separator <= "System".Length)
                    {
                        throw new InvalidOperationException($"Assembly '{assemblyName}' is not allowed or unavailable.");
                    }

                    referenceName = referenceName[..separator];
                }

                references.Add(Assembly.LoadFrom(assemblyPath));
            }

            ScriptOptions options = ScriptOptions.Default
                .WithReferences(references.Distinct())
                .WithImports(request.AllowedAssemblies);
            var script = CSharpScript.Create(request.Code, options);
            var compilation = script.GetCompilation();
            var syntaxTree = compilation.SyntaxTrees.Single();
            var syntaxRoot = await syntaxTree.GetRootAsync();
            if (syntaxRoot.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective)
                || syntaxRoot.DescendantTokens().Any(token => token.ValueText is "dynamic" or "unsafe" or "stackalloc"))
            {
                throw new InvalidOperationException(
                    "The script uses a prohibited file, network, process, native, dynamic, or reflection capability.");
            }

            var diagnostics = compilation.GetDiagnostics();
            if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                throw new CompilationErrorException("Script compilation failed.", diagnostics);
            }

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (SimpleNameSyntax nameSyntax in syntaxRoot.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(nameSyntax);
                IEnumerable<ISymbol> symbols = symbolInfo.Symbol is null
                    ? symbolInfo.CandidateSymbols
                    : [symbolInfo.Symbol];
                foreach (ISymbol originalSymbol in symbols)
                {
                    ISymbol symbol = originalSymbol is IAliasSymbol alias ? alias.Target : originalSymbol;
                    var namespaceName = symbol is INamespaceSymbol namespaceSymbol
                        ? namespaceSymbol.ToDisplayString()
                        : symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                    var typeName = (symbol as INamedTypeSymbol ?? symbol.ContainingType)
                        ?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                    if (_prohibitedNamespacePrefixes.Any(prefix => namespaceName.Equals(prefix, StringComparison.Ordinal)
                            || namespaceName.StartsWith(prefix + ".", StringComparison.Ordinal))
                        || (typeName is not null && _prohibitedTypeNames.Contains(typeName, StringComparer.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "The script uses a prohibited file, network, process, native, dynamic, or reflection capability.");
                    }

                    if (namespaceName.StartsWith("System.", StringComparison.Ordinal)
                        && !request.AllowedAssemblies.Any(allowed => namespaceName.Equals(allowed, StringComparison.Ordinal)
                            || namespaceName.StartsWith(allowed + ".", StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "The script references a namespace outside allowed_assemblies.");
                    }
                }
            }

            var value = await CSharpScript.EvaluateAsync(
                request.Code,
                options,
                cancellationToken: CancellationToken.None);
            var output = value?.ToString() ?? string.Empty;
            var outputBytes = Encoding.UTF8.GetBytes(output);
            var truncated = outputBytes.Length > request.MaximumOutputBytes;
            if (truncated)
            {
                var strictUtf8 = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true);
                var retainedBytes = request.MaximumOutputBytes;
                output = string.Empty;
                while (retainedBytes > 0)
                {
                    try
                    {
                        output = strictUtf8.GetString(outputBytes.AsSpan(0, retainedBytes));
                        break;
                    }
                    catch (DecoderFallbackException)
                    {
                        retainedBytes--;
                    }
                }
            }

            result = new ScriptWorkerResult(
                true,
                output,
                null,
                (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                truncated);
        }
        catch (CompilationErrorException exception)
        {
            result = new ScriptWorkerResult(
                false,
                null,
                string.Join(Environment.NewLine, exception.Diagnostics.Select(diagnostic => diagnostic.ToString())),
                (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                false);
        }
        catch (Exception exception)
        {
            result = new ScriptWorkerResult(
                false,
                null,
                exception.Message,
                (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                false);
        }

        await Console.Out.WriteAsync(JsonSerializer.Serialize(result));
        return 0;
    }

    private sealed record ScriptWorkerRequest
    {
        public required string Code { get; init; }

        public required string Kind { get; init; }

        public int MaximumOutputBytes { get; init; }

        public required IReadOnlyList<string> AllowedAssemblies { get; init; }
    }

    private sealed record ScriptWorkerResult(
        bool Success,
        string? Output,
        string? Error,
        int ExecutionMs,
        bool IsTruncated);
}
