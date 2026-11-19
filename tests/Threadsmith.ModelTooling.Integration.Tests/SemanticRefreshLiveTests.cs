namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Validation;
using Xunit;

/// <summary>Collects real repository refresh and diagnostic timings without changing source files.</summary>
public static class SemanticRefreshLiveTests
{
    private sealed class UnusedProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Semantic-only validation must not start a process.");
        }
    }
}
