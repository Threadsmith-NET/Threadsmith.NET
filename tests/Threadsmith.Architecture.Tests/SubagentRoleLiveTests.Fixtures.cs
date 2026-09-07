namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;

public sealed partial class SubagentRoleLiveTests
{
    private static LiveFixture CreateFixture(AgentRole role, bool control)
    {
        return role switch
        {
            AgentRole.Explorer => new LiveFixture(
                "Trace Checkout.Quote through Discount.Apply. State the final amount for subtotal 100 and eligible=true. Explain the eligibility branch; do not infer taxes or external callers.",
                new Dictionary<string, string>
                {
                    ["Checkout.cs"] = "public static class Checkout { public static decimal Quote(decimal subtotal, bool eligible) => Discount.Apply(subtotal, eligible); }",
                    ["Discount.cs"] = "public static class Discount { public static decimal Apply(decimal subtotal, bool eligible) => eligible ? subtotal * "
                        + (control ? "0.80m" : "0.90m") + " : subtotal; }",
                },
                ["Discount", control ? "80" : "90", "eligible"]),
            AgentRole.Implementer => new LiveFixture(
                "Plan the smallest change so LoadAsync forwards the caller's cancellation token to fetch. Include a validation case proving token propagation and pre-cancelled behavior. This is a proposal only; do not claim files changed or tests ran.",
                new Dictionary<string, string>
                {
                    ["Loader.cs"] = "using System; using System.Threading; using System.Threading.Tasks; public static class Loader { "
                        + (control
                            ? "public static Task<string> LoadAsync(Func<CancellationToken, Task<string>> fetch, CancellationToken cancellationToken) => fetch(CancellationToken.None);"
                            : "public static Task<string> LoadAsync(Func<CancellationToken, Task<string>> fetch) => fetch(CancellationToken.None);") + " }",
                    ["Contract.md"] = "LoadAsync must forward cancellation without substituting a new token. A fetch delegate owns cancellation observation; a pre-cancelled token must reach it. Exceptions propagate unchanged.",
                },
                ["Loader.cs", "cancellation", "token"]),
            AgentRole.SecurityReviewer => new LiveFixture(
                "Review the Login method only for credential disclosure through its logging. Assess the code against Contract.md; no other authentication internals are in scope.",
                new Dictionary<string, string>
                {
                    ["Login.cs"] = "using Microsoft.Extensions.Logging; public static class LoginService { public static bool Login(string user, string password, ILogger logger, IAuthenticator auth) { "
                        + (control ? "logger.LogInformation(\"Login attempt\"); " : "logger.LogInformation(\"Login password {Password}\", password); ")
                        + "return auth.Verify(user, password); } } public interface IAuthenticator { bool Verify(string user, string password); }",
                    ["Contract.md"] = "Passwords are secrets and must not appear in application logs. The authenticator implementation is external and out of scope. Generic login-attempt events without user inputs are permitted.",
                },
                ["password", "log"]),
            AgentRole.TestReviewer => new LiveFixture(
                "Review whether the new negative-value rejection behavior in Calculator.Discount is tested. Focus only on the documented rejection boundary and existing tests; do not request unrelated coverage.",
                new Dictionary<string, string>
                {
                    ["Calculator.cs"] = "using System; public static class Calculator { public static decimal Discount(decimal amount) { if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount)); return amount * 0.9m; } }",
                    ["CalculatorTests.cs"] = "using System; using Xunit; public class CalculatorTests { [Fact] public void Positive() => Assert.Equal(90m, Calculator.Discount(100m)); [Fact] public void Zero() => Assert.Equal(0m, Calculator.Discount(0m)); "
                        + (control ? "[Fact] public void Negative() => Assert.Throws<ArgumentOutOfRangeException>(() => Calculator.Discount(-1m)); " : string.Empty) + "}",
                },
                ["negative", "ArgumentOutOfRangeException"]),
            AgentRole.PerformanceReviewer => new LiveFixture(
                "Review repeated membership checks in Counter.Count for unnecessary work as rows and allowed grow. Do not report measurements: none were provided or collected. A once-per-call lookup allocation is acceptable.",
                new Dictionary<string, string>
                {
                    ["Counter.cs"] = "using System.Collections.Generic; using System.Linq; public static class Counter { public static int Count(IReadOnlyList<int> rows, IReadOnlyList<int> allowed) { var count = 0; "
                        + (control ? "var set = allowed.ToHashSet(); " : string.Empty)
                        + "foreach (var row in rows) { " + (control ? string.Empty : "var set = allowed.ToHashSet(); ")
                        + "if (set.Contains(row)) count++; } return count; } }",
                    ["Contract.md"] = "Inputs are stable for the duration of Count. Repeated rows count separately. Only membership throughput is under review, not other API design. No timing or allocation measurements exist.",
                },
                ["ToHashSet", "plausible"]),
            AgentRole.ArchitectureReviewer => new LiveFixture(
                "Review the Core project dependency against docs/ADR.md. Cite the actual project or source and the architectural rule. Do not impose stylistic preferences beyond this written rule.",
                new Dictionary<string, string>
                {
                    ["Core/Core.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                        + (control ? string.Empty : "<ItemGroup><ProjectReference Include=\"../UI/UI.csproj\" /></ItemGroup>") + "</Project>",
                    ["Core/Status.cs"] = control
                        ? "namespace Product.Core; public interface IStatusSink { void Report(string text); } public static class Status { public static void Report(IStatusSink sink) => sink.Report(\"Ready\"); }"
                        : "namespace Product.Core; public static class Status { public static void Report(Product.UI.StatusView view) => view.Show(\"Ready\"); }",
                    ["docs/ADR.md"] = "Core must not reference UI projects or UI types. UI depends on Core. A framework-neutral interface declared in Core may be implemented by UI. This is the only architecture constraint for this fixture.",
                },
                ["Core", "UI", "ADR.md"]),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }
}
