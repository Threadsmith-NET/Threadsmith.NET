namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Immutable;
using System.IO.Pipes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

internal static class TestAnalyzerBinary
{
    internal static byte[] Emit(string assemblyName, string source, CancellationToken cancellationToken)
    {
        var coreLibrary = typeof(object).Assembly.Location;
        var runtime = Path.Combine(Path.GetDirectoryName(coreLibrary)!, "System.Runtime.dll");
        string[] referencePaths = [coreLibrary, runtime, typeof(ImmutableArray).Assembly.Location, typeof(Compilation).Assembly.Location, typeof(NamedPipeClientStream).Assembly.Location];
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken)],
            referencePaths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output, cancellationToken: cancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return output.ToArray();
    }

    internal static byte[] EmitBlockingConstructor(string component, string pipeName, CancellationToken cancellationToken)
    {
        var constructorBody = $$"""
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "{{pipeName}}", System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect();
            _ = pipe.ReadByte();
            pipe.WriteByte(1);
            """;
        var source = component switch
        {
            "analyzer" => $$"""
                using System.Collections.Immutable;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.Diagnostics;
                [DiagnosticAnalyzer(LanguageNames.CSharp)]
                public sealed class Analyzer : DiagnosticAnalyzer
                {
                    public Analyzer() { {{constructorBody}} }
                    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];
                    public override void Initialize(AnalysisContext context) { }
                }
                """,
            "generator" => $$"""
                using Microsoft.CodeAnalysis;
                [Generator(LanguageNames.CSharp)]
                public sealed class Generator : ISourceGenerator
                {
                    public Generator() { {{constructorBody}} }
                    public void Initialize(GeneratorInitializationContext context) { }
                    public void Execute(GeneratorExecutionContext context) { }
                }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
        return Emit("BlockingProbe", source, cancellationToken);
    }
}
