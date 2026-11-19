namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Search reuses case evidence within its execution boundary.</summary>
public sealed class SearchPathPolicyTests
{
    private sealed class StubProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string record = "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"File.cs\"},\"lines\":{\"text\":\"needle\\n\"},\"line_number\":1,\"submatches\":[{\"start\":0}]}}\n";
            return Task.FromResult(new ProcessExecutionResult(
                42, 0, string.Concat(Enumerable.Repeat(record, 50)), string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
