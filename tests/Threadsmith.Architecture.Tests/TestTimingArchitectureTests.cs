namespace Threadsmith.Architecture.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

/// <summary>Rejects explicit wall-clock waits and deadlines in executable test code.</summary>
public static class TestTimingArchitectureTests
{
    /// <summary>Checks executable test sources, excluding generated outputs and literal fixture programs.</summary>
    [Fact]
    public static void TestsDoNotUseWallClockCompletionLimits()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var violations = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .SelectMany(path => FindViolations(File.ReadAllText(path)).Select(node =>
                $"{Path.GetRelativePath(root, path)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {node}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>Exercises both prohibited timing and permitted controlled synchronization.</summary>
    [Theory]
    [InlineData("await Task.Delay(10);", true)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(1), token);", true)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(1), clock, token);", false)]
    [InlineData("await Task.Delay(Timeout.InfiniteTimeSpan, token);", false)]
    [InlineData("Thread.Sleep(10);", true)]
    [InlineData("Assert.True(process.WaitForExit(5000));", true)]
    [InlineData("await Task.Delay(duration, TimeProvider.System, token);", true)]
    [InlineData("await task.WaitAsync(duration, TimeProvider.System, token);", true)]
    [InlineData("var deadline = DateTimeOffset.UtcNow.AddSeconds(5); while (DateTimeOffset.UtcNow < deadline) {}", true)]
    [InlineData("using var cts = new System.Threading.CancellationTokenSource(500);", true)]
    [InlineData("Thread.Sleep(Timeout.Infinite);", false)]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(2));", true)]
    [InlineData("var limit = TimeSpan.FromSeconds(2); await task.WaitAsync(limit, token);", true)]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(2), clock, token);", false)]
    [InlineData("await task.WaitAsync(token);", false)]
    [InlineData("using var cts = new CancellationTokenSource(500);", true)]
    [InlineData("CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));", true)]
    [InlineData("using var cts = new CancellationTokenSource(duration, clock);", false)]
    [InlineData("using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1), TimeProvider.System);", true)]
    [InlineData("cts.CancelAfter(500);", true)]
    [InlineData("var watch = Stopwatch.StartNew(); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));", true)]
    [InlineData("var elapsed = Stopwatch.GetElapsedTime(start); Assert.InRange(elapsed.TotalSeconds, 0, 5);", true)]
    [InlineData("Console.WriteLine(watch.Elapsed);", false)]
    [InlineData("var provider = new Provider { Observe = () => Console.WriteLine(Stopwatch.GetElapsedTime(start)) }; Assert.NotNull(provider);", false)]
    [InlineData("var source = \"await Task.Delay(500);\";", false)]
    public static void GuardRecognizesExecutableTiming(string body, bool prohibited)
    {
        Assert.Equal(prohibited, FindViolations("class Fixture { async Task Run() { " + body + " } }").Any());
    }

    /// <summary>Test-runner attributes must not encode performance assumptions.</summary>
    [Fact]
    public static void GuardRejectsTestAttributeDeadlines()
    {
        Assert.Single(FindViolations("class Fixture { [Fact(Timeout = 500)] void Run() {} }"));
    }

    private static IEnumerable<SyntaxNode> FindViolations(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var initializers = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Initializer is not null)
            .GroupBy(variable => variable.Identifier.ValueText)
            .ToDictionary(group => group.Key, group => group.Select(variable => variable.Initializer!.Value).ToArray());

        bool ContainsClockMeasurement(SyntaxNode node) => node.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(member => (member.Name.Identifier.ValueText == "GetElapsedTime"
                && member.Expression.ToString().EndsWith("Stopwatch", StringComparison.Ordinal))
                || (member.Name.Identifier.ValueText is "Elapsed" or "ElapsedMilliseconds" or "ElapsedTicks"
                && member.Expression is IdentifierNameSyntax identifier
                && initializers.TryGetValue(identifier.Identifier.ValueText, out var values)
                && values.Any(value => value.ToString().Contains("Stopwatch.StartNew(", StringComparison.Ordinal))));

        bool IsDuration(ExpressionSyntax expression) => expression.ToString().StartsWith("TimeSpan.", StringComparison.Ordinal)
            || (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
            || (expression is IdentifierNameSyntax identifier && initializers.TryGetValue(identifier.Identifier.ValueText, out var values)
                && values.Any(value => value.ToString().StartsWith("TimeSpan.", StringComparison.Ordinal)));

        bool IsMeasured(SyntaxNode node) => ContainsClockMeasurement(node)
            || node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier =>
                initializers.TryGetValue(identifier.Identifier.ValueText, out var values) && values.Any(value => value is InvocationExpressionSyntax or MemberAccessExpressionSyntax && ContainsClockMeasurement(value)));

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }

            var name = member.Name.Identifier.ValueText;
            var arguments = invocation.ArgumentList.Arguments;
            var first = arguments.FirstOrDefault()?.Expression;
            var infinite = first?.ToString() is "Timeout.Infinite" or "Timeout.InfiniteTimeSpan" or "-1";
            var systemClock = arguments.Any(argument => argument.Expression.ToString() == "TimeProvider.System");
            var prohibited = name switch
            {
                "CancelAfter" => true,
                "Sleep" => !infinite,
                "Delay" => member.Expression.ToString().EndsWith("Task", StringComparison.Ordinal)
                    && (arguments.Count < 3 || systemClock) && !infinite,
                "Wait" or "WaitAsync" or "WaitForExit" or "WaitOne" => first is not null
                    && (IsDuration(first) || systemClock) && (arguments.Count < 3 || systemClock) && !infinite,
                _ => member.Expression.ToString() == "Assert" && IsMeasured(invocation),
            };
            if (prohibited)
            {
                yield return invocation;
            }
        }

        foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var type = creation is ObjectCreationExpressionSyntax explicitCreation
                ? explicitCreation.Type.ToString()
                : creation.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault()?.Type.ToString();
            if (type?.Split('.')[^1] == "CancellationTokenSource"
                && (creation.ArgumentList?.Arguments.Count == 1
                    || creation.ArgumentList?.Arguments.Any(argument => argument.Expression.ToString() == "TimeProvider.System") is true))
            {
                yield return creation;
            }
        }

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (attribute.Name.ToString() is "Fact" or "Theory"
                && attribute.ArgumentList?.Arguments.Any(argument => argument.NameEquals?.Name.Identifier.ValueText == "Timeout") is true)
            {
                yield return attribute;
            }
        }

        foreach (var condition in root.DescendantNodes().OfType<WhileStatementSyntax>())
        {
            if (IsMeasured(condition.Condition)
                || condition.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(member =>
                    member.Name.Identifier.ValueText is "Now" or "UtcNow"
                    && member.Expression.ToString() is "DateTime" or "DateTimeOffset"))
            {
                yield return condition;
            }
        }
    }
}
