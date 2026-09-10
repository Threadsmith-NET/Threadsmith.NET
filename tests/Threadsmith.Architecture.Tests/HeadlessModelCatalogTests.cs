namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using Threadsmith.App;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

/// <summary>Exercises offline and configured headless catalog command routing.</summary>
public sealed class HeadlessModelCatalogTests
{
    /// <summary>An offline list succeeds without requiring the optional active-selection handler.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListModels_DispatchesOnlyWhenActiveSelectionIsComposed(bool activeModelsAvailable)
    {
        var dispatcher = new CatalogDispatcher { ActiveModelsAvailable = activeModelsAvailable };
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var shell = new HeadlessShell(dispatcher, new InMemoryProjectionStore(), output);

        var exitCode = await ShellRunner.RunModelCatalogCommandAsync(
            shell, ["/models"], activeModelsAvailable, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Array, result.RootElement.ValueKind);
        Assert.Equal(0, result.RootElement.GetArrayLength());
        Assert.Equal(activeModelsAvailable ? 1 : 0, dispatcher.Commands.Count);
    }

    /// <summary>Provider maintenance remains reachable when no model is currently selectable.</summary>
    [Theory]
    [InlineData("status", false, 0)]
    [InlineData("refresh", false, 1)]
    [InlineData("refresh", true, 0)]
    public async Task OfflineCatalog_StillAllowsStatusAndRefresh(string operation, bool refreshed, int expectedExitCode)
    {
        var dispatcher = new CatalogDispatcher { Refreshed = refreshed };
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var shell = new HeadlessShell(dispatcher, new InMemoryProjectionStore(), output);

        var exitCode = await ShellRunner.RunModelCatalogCommandAsync(
            shell, ["/models", operation, "anthropic"], false, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Empty(error.ToString());
        Assert.Single(dispatcher.Commands);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("anthropic", result.RootElement.GetProperty("ProviderId").GetString());
    }

    /// <summary>Malformed command arguments return usage without invoking a model or maintenance handler.</summary>
    [Fact]
    public async Task InvalidCatalogSyntax_ReturnsUsageWithoutDispatch()
    {
        var dispatcher = new CatalogDispatcher();
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var shell = new HeadlessShell(dispatcher, new InMemoryProjectionStore(), output);

        var exitCode = await ShellRunner.RunModelCatalogCommandAsync(
            shell, ["/models", "refresh"], false, output, error, TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(dispatcher.Commands);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class CatalogDispatcher : ICommandDispatcher
    {
        public bool ActiveModelsAvailable { get; init; }

        public bool Refreshed { get; init; }

        public List<object> Commands { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            object result = command switch
            {
                ListActiveModelsCommand when ActiveModelsAvailable => Array.Empty<SelectableModelEntry>(),
                GetModelCatalogStatusCommand status => new ModelCatalogProviderStatus { ProviderId = status.ProviderId },
                RefreshModelCatalogCommand refresh => new ModelCatalogRefreshResult { ProviderId = refresh.ProviderId, Refreshed = Refreshed },
                _ => throw new InvalidOperationException("No handler for this catalog command."),
            };
            return Task.FromResult((TResponse)result);
        }
    }
}
