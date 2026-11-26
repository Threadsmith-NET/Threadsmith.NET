namespace Threadsmith.Scripting.Worker;

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Threadsmith.Core;

/// <summary>Reads one bounded script request from standard input and writes one JSON result.</summary>
public static class Program
{
    private const string _textNamespace = "System.Text";
    private const string _cryptographyNamespace = "System.Security.Cryptography";
    private static readonly JsonSerializerOptions RequestJsonOptions = new() { PropertyNameCaseInsensitive = true };
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
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--warm", StringComparer.Ordinal))
        {
            // Warm compiler infrastructure without executing code or receiving file authority.
            var options = CreateOptions(
                PermissionConfiguration.ToolsConfigCsharpScriptSettings.DefaultAllowedAssemblies.Split(','),
                128);
            var diagnostics = CSharpScript.Create("0", options, typeof(ScriptGlobals)).Compile();
            if (diagnostics.Any(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                return 1;
            }

            await Console.Out.WriteAsync("THREADSMITH_SCRIPT_READY\n");
            await Console.Out.FlushAsync();
        }

        await using var input = Console.OpenStandardInput();
        await using var outputPipe = Console.OpenStandardOutput();
        var start = await ScriptWorkerProtocol.ReadAsync(input, 0, kind => kind == ScriptFrameKind.Start ? 64 * 1024 * 1024 : -1, CancellationToken.None);
        var requestJson = start.Payload;
        ScriptTools? tools = null;
        var maximumFinalBytes = 4096;
        ScriptWorkerResult result;
        var deadlineToken = CancellationToken.None;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = JsonSerializer.Deserialize<ScriptWorkerRequest>(
                requestJson,
                RequestJsonOptions)
                ?? throw new InvalidOperationException("The scripting request was empty.");
            if (string.IsNullOrWhiteSpace(request.Code)
                || request.MaximumCodeCharacters <= 0
                || request.MaximumAssemblyNameCharacters <= 0
                || request.Code.Length > request.MaximumCodeCharacters
                || request.MaximumOutputBytes <= 0
                || request.Kind is not ("Expression" or "Statement"))
            {
                throw new InvalidOperationException("The scripting request exceeded a worker boundary.");
            }

            var scope = request.FileScope ?? throw new InvalidOperationException("Scratchpad file authority is required.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
            deadlineToken = deadline.Token;
            maximumFinalBytes = checked((request.MaximumOutputBytes * 6) + 4096);
            tools = new ScriptTools(request.Available, input, outputPipe, deadline.Token);
            var globals = new ScriptGlobals(scope, tools, deadline.Token);

            // Warm workers start without invocation-specific environment or working directory.
            Directory.SetCurrentDirectory(scope.ScratchpadPath);
            var temporaryPath = Path.Combine(scope.ScratchpadPath, "tmp");
            foreach (var variable in new[] { "TMP", "TEMP", "TMPDIR" })
            {
                Environment.SetEnvironmentVariable(variable, temporaryPath);
            }

            var options = CreateOptions(request.AllowedAssemblies, request.MaximumAssemblyNameCharacters);
            var script = CSharpScript.Create(request.Code, options, typeof(ScriptGlobals));
            var compilation = script.GetCompilation();
            var syntaxTree = compilation.SyntaxTrees.Single();
            var syntaxRoot = await syntaxTree.GetRootAsync(deadline.Token);
            if (syntaxRoot.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective))
            {
                throw new InvalidOperationException(
                    "Script directives are prohibited. Use the session-configured scripting APIs without directives.");
            }

            var prohibitedToken = syntaxRoot.DescendantTokens()
                .FirstOrDefault(token => token.ValueText is "dynamic" or "unsafe" or "stackalloc");
            if (prohibitedToken.RawKind != 0)
            {
                throw new InvalidOperationException($"The '{prohibitedToken.ValueText}' construct is prohibited in scripts.");
            }

            var diagnostics = compilation.GetDiagnostics();
            if (diagnostics.Any(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                throw new CompilationErrorException("Script compilation failed.", diagnostics);
            }

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var nameSyntax in syntaxRoot.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(nameSyntax);
                IEnumerable<ISymbol> symbols = symbolInfo.Symbol is null
                    ? symbolInfo.CandidateSymbols
                    : [symbolInfo.Symbol];
                foreach (var originalSymbol in symbols)
                {
                    var symbol = originalSymbol is IAliasSymbol alias ? alias.Target : originalSymbol;
                    var namespaceName = symbol is INamespaceSymbol namespaceSymbol
                        ? namespaceSymbol.ToDisplayString()
                        : symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                    var typeName = (symbol as INamedTypeSymbol ?? symbol.ContainingType)
                        ?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                    if (namespaceName.StartsWith("Threadsmith", StringComparison.Ordinal)
                        && !(symbol is IPropertySymbol { Name: "Scratchpad" or "Tools", ContainingType.Name: "ScriptGlobals" }
                            || symbol is IPropertySymbol { Name: "Available", ContainingType.Name: "ScriptTools" }
                            || symbol is IMethodSymbol { Name: "InvokeAsync", ContainingType.Name: "ScriptTools" }
                            || symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { Name: "InvokeAsync", ContainingType.Name: "ScriptTools" } }
                            || symbol is IPropertySymbol { Name: "Succeeded" or "ResultJson" or "IsTruncated" or "ErrorClassification" or "Error", ContainingType.Name: "ScriptToolReply" }
                            || symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary, ContainingType.Name: "ScratchpadFiles" }
                            || symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Ordinary, ContainingType.Name: "ScratchpadFiles" } }
                            || symbol is INamedTypeSymbol { Name: "ScratchpadFiles" or "ScriptToolReply" or "ScriptTools" }))
                    {
                        throw new InvalidOperationException("Scripts may use only the supplied Scratchpad and Tools APIs, not host implementation types.");
                    }

                    if (typeName == "System.Console")
                    {
                        throw new InvalidOperationException(
                            "Console access is prohibited. Return the result as the script's final expression.");
                    }

                    if (_prohibitedNamespacePrefixes.Any(prefix => namespaceName.Equals(prefix, StringComparison.Ordinal)
                            || namespaceName.StartsWith(prefix + ".", StringComparison.Ordinal))
                        || (typeName is not null && _prohibitedTypeNames.Contains(typeName, StringComparer.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"Access to '{typeName ?? namespaceName}' is prohibited in scripts. For files, use Scratchpad.");
                    }

                    // A configured reference enables binding, not arbitrary cryptography APIs.
                    if ((namespaceName == _cryptographyNamespace
                            || namespaceName.StartsWith(_cryptographyNamespace + ".", StringComparison.Ordinal))
                        && !IsAllowedCryptographySymbol(symbol, namespaceName, typeName))
                    {
                        throw new InvalidOperationException(
                            $"Access to '{typeName ?? namespaceName}' is prohibited in scripts. Only SHA256.HashData(byte[]) is permitted for cryptography.");
                    }

                    if (namespaceName.StartsWith("System.", StringComparison.Ordinal)
                        && !request.AllowedAssemblies.Any(allowed => namespaceName.Equals(allowed, StringComparison.Ordinal)
                            || (allowed != _textNamespace && namespaceName.StartsWith(allowed + ".", StringComparison.Ordinal))
                            || (symbol is INamespaceSymbol && allowed.StartsWith(namespaceName + ".", StringComparison.Ordinal))))
                    {
                        throw new InvalidOperationException(
                            $"Namespace '{namespaceName}' is not permitted by this session's scripting configuration (allowed_assemblies).");
                    }
                }
            }

            var state = await script.RunAsync(globals, cancellationToken: deadline.Token);
            var output = state.ReturnValue?.ToString() ?? string.Empty;
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
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            result = new ScriptWorkerResult(false, null, "The script execution deadline expired.", (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue), false)
            {
                TimedOut = true,
            };
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

        var finalSequence = tools?.Close() ?? 1;
        var finalJson = JsonSerializer.Serialize(result);
        if (Encoding.UTF8.GetByteCount(finalJson) > maximumFinalBytes)
        {
            finalJson = JsonSerializer.Serialize(result with { Success = false, Output = null, Error = "Worker result exceeded its output limit.", IsTruncated = true });
        }

        await ScriptWorkerProtocol.WriteAsync(outputPipe, ScriptFrameKind.Final, finalSequence, finalJson, maximumFinalBytes, CancellationToken.None);
        return 0;
    }

    private static bool IsAllowedCryptographySymbol(ISymbol symbol, string namespaceName, string? typeName)
    {
        if (namespaceName != _cryptographyNamespace)
        {
            return false;
        }

        if (symbol is INamespaceSymbol)
        {
            return true;
        }

        if (typeName != typeof(SHA256).FullName)
        {
            return false;
        }

        // Named arguments bind to the parameter rather than to the method.
        var member = symbol is IParameterSymbol parameter ? parameter.ContainingSymbol : symbol;
        return member is INamedTypeSymbol or IMethodSymbol
        {
            Name: nameof(SHA256.HashData),
            IsStatic: true,
            Parameters: [{ Type: IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte } }],
        };
    }

    private static ScriptOptions CreateOptions(IReadOnlyList<string> allowedAssemblies, int maximumAssemblyNameCharacters)
    {
        var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var platformAssemblies = trustedPlatformAssemblies
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var references = new List<Assembly> { typeof(object).Assembly, typeof(ScriptGlobals).Assembly, typeof(ScriptToolReply).Assembly };
        foreach (var assemblyName in allowedAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(assemblyName)
                || assemblyName.Length > maximumAssemblyNameCharacters
                || !assemblyName.StartsWith("System.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Assembly '{assemblyName}' is not allowed or unavailable.");
            }

            // System.Text is an exact namespace import from the core library, not a separate assembly.
            if (assemblyName == _textNamespace)
            {
                continue;
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

        return ScriptOptions.Default.WithReferences(references.Distinct()).WithImports(allowedAssemblies.Prepend("System"));
    }

    private sealed record ScriptWorkerRequest
    {
        public IReadOnlyList<string> Available { get; init; } = [];

        public required string Code { get; init; }

        public required string Kind { get; init; }

        public int MaximumOutputBytes { get; init; }

        public ScriptFileScope? FileScope { get; init; }

        public int TimeoutMilliseconds { get; init; } = PermissionConfiguration.ToolsConfigCsharpScriptSettings.DefaultTimeoutMilliseconds;

        public int MaximumCodeCharacters { get; init; } = 65536;

        public int MaximumAssemblyNameCharacters { get; init; } = 128;

        public required IReadOnlyList<string> AllowedAssemblies { get; init; }
    }

    private sealed record ScriptWorkerResult(
        bool Success,
        string? Output,
        string? Error,
        int ExecutionMs,
        bool IsTruncated)
    {
        public bool TimedOut { get; init; }
    }
}
