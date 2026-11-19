namespace Threadsmith.SemanticFixtures.Roslyn59;

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>Generates one deterministic type for semantic compatibility coverage.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SemanticFixtureGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#pragma warning disable RS1035 // The test-only additional file identifies the physical generation barrier.
        var barrier = context.AdditionalTextsProvider.Where(static file => Path.GetFileName(file.Path) == "generator.block");
        context.RegisterSourceOutput(barrier, static (_, file) => SemanticFixtureConstruction.WaitForRelease(file.Path));
#pragma warning restore RS1035
        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "GeneratedByRoslyn59.g.cs",
            SourceText.From(
                """
                namespace SmallSolution.Contracts;

                public sealed class GeneratedByRoslyn59
                {
                    public static string Value => "Roslyn 5.9";
                }

                """,
                Encoding.UTF8)));
    }
}
