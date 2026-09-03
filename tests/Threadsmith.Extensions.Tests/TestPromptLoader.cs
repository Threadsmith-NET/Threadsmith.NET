global using TestPromptLoader = Threadsmith.Extensions.Tests.TestPromptLoader;

namespace Threadsmith.Extensions.Tests;

using System.Collections.ObjectModel;
using System.Text;
using Threadsmith.Core;

/// <summary>Provides test-only in-memory access to exact source prompt assets.</summary>
internal sealed class TestPromptLoader : IPromptLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyDictionary<string, string> _prompts;

    private TestPromptLoader(IReadOnlyDictionary<string, string> prompts)
    {
        _prompts = prompts;
    }

    /// <summary>Gets the source-asset-backed shared test loader.</summary>
    internal static TestPromptLoader Instance { get; } = LoadSourceAssets();

    /// <inheritdoc />
    public string Get(string promptFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptFileName);
        return _prompts.TryGetValue(promptFileName, out var prompt)
            ? prompt
            : throw new InvalidOperationException(
                $"The test prompt asset '{promptFileName}' was not found in the source tree.");
    }

    /// <inheritdoc />
    public string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var definition = PromptAssetCatalog.Get(promptFileName);
        var supplied = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
        if (definition.RequiredTokens.Any(token => !supplied.ContainsKey(token)))
        {
            throw new ArgumentException("A required prompt token was not supplied.", nameof(tokens));
        }

        if (supplied.Keys.Any(token =>
                !definition.RequiredTokens.Contains(token)
                && !definition.OptionalTokens.Contains(token)))
        {
            throw new ArgumentException("An undeclared prompt token was supplied.", nameof(tokens));
        }

        var template = Get(promptFileName);
        var rendered = new StringBuilder(template.Length);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var start = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                rendered.Append(template, cursor, template.Length - cursor);
                break;
            }

            rendered.Append(template, cursor, start - cursor);
            var close = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new InvalidOperationException("The test prompt contains an invalid token marker.");
            }

            var name = template[(start + 2)..close];
            if (supplied.TryGetValue(name, out var value))
            {
                rendered.Append(value);
            }

            cursor = close + 2;
        }

        return rendered.ToString();
    }

    /// <summary>Creates an immutable test-only copy with one declared prompt body replaced.</summary>
    internal TestPromptLoader WithPrompt(string promptFileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptFileName);
        ArgumentNullException.ThrowIfNull(content);
        _ = PromptAssetCatalog.Get(promptFileName);
        var prompts = new Dictionary<string, string>(_prompts, StringComparer.Ordinal)
        {
            [promptFileName] = content,
        };
        return new TestPromptLoader(new ReadOnlyDictionary<string, string>(prompts));
    }

    private static TestPromptLoader LoadSourceAssets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in PromptAssetCatalog.All)
        {
            var path = Path.Combine(
                repositoryRoot,
                "src",
                definition.Owner,
                "Prompts",
                definition.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            prompts.Add(definition.FileName, File.ReadAllText(path, StrictUtf8));
        }

        return new TestPromptLoader(
            new ReadOnlyDictionary<string, string>(prompts));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                directory.FullName,
                "src",
                "Threadsmith.Core",
                "Threadsmith.Core.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The Threadsmith source root was not found for prompt-backed tests.");
    }
}
