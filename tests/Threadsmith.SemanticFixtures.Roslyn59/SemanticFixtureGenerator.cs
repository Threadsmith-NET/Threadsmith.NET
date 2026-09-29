namespace Threadsmith.SemanticFixtures.Roslyn59;

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>Generates one deterministic type for semantic compatibility coverage.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SemanticFixtureGenerator : IIncrementalGenerator
{
    /// <summary>Initializes a new instance of the <see cref="SemanticFixtureGenerator"/> class.</summary>
    public SemanticFixtureGenerator()
    {
        SemanticFixtureConstruction.WaitForRelease(
            typeof(SemanticFixtureGenerator).Assembly.Location,
            "generator");
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
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
