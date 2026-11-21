namespace Threadsmith.NativeTools.Tests;

using System.Diagnostics;
using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class Plan41InventoryToolTests
{
    private static async Task CreateDirectoryAliasAsync(string alias, string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", Path.GetFullPath(alias), Path.GetFullPath(target) })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error).WaitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while termination was being requested.
            }

            // Cleanup owns termination and pipe draining even after the test token is cancelled.
            await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), output, error)
                .WaitAsync(TestContext.Current.CancellationToken);
            throw;
        }

        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
