namespace Threadsmith.Workspaces;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Resolves the installed dotnet host and SDK selected for a repository.</summary>
public sealed class DotNetEnvironmentResolver
{
    private const int _maximumCapturedCharacters = 64 * 1024;

    /// <summary>Resolves safe environment facts without evaluating repository projects.</summary>
    /// <param name="repositoryPath">Normalized repository root.</param>
    /// <param name="configuration">Build configuration.</param>
    /// <param name="platform">Build platform.</param>
    /// <param name="allowRepositorySdkResolution">Whether dotnet may resolve the SDK from the repository directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved environment facts, or <see langword="null"/> when dotnet is unavailable.</returns>
    public static async Task<MsBuildEnvironmentSnapshot?> ResolveAsync(
        string repositoryPath,
        string configuration,
        string platform,
        bool allowRepositorySdkResolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? dotNetPath = null;
        var dotNetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotNetRoot))
        {
            var rootedCandidate = Path.Combine(dotNetRoot, executableName);
            if (File.Exists(rootedCandidate))
            {
                dotNetPath = Path.GetFullPath(rootedCandidate);
            }
        }

        if (dotNetPath is null)
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    dotNetPath = Path.GetFullPath(candidate);
                    break;
                }
            }
        }

        if (dotNetPath is null)
        {
            return null;
        }

        string? globalJsonPath = null;
        for (var directory = new DirectoryInfo(repositoryPath); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                globalJsonPath = candidate;
                break;
            }
        }

        var hostDirectory = Path.GetDirectoryName(dotNetPath);
        if (hostDirectory is null)
        {
            return null;
        }

        string? configuredSdkVersion = null;
        if (globalJsonPath is not null && new FileInfo(globalJsonPath).Length <= 1024 * 1024)
        {
            await using var globalJsonStream = new FileStream(
                globalJsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument globalJson = await JsonDocument.ParseAsync(
                globalJsonStream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                },
                cancellationToken);
            if (globalJson.RootElement.TryGetProperty("sdk", out JsonElement sdk)
                && sdk.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.String)
            {
                configuredSdkVersion = version.GetString();
            }
        }

        var versionWorkingDirectory = allowRepositorySdkResolution
            ? repositoryPath
            : hostDirectory;
        DotNetProcessResult versionResult = await RunDotNetAsync(
            dotNetPath,
            versionWorkingDirectory,
            ["--version"],
            cancellationToken);
        var sdkVersion = versionResult.StandardOutput.Trim();
        if (!allowRepositorySdkResolution
            && !string.IsNullOrWhiteSpace(configuredSdkVersion)
            && Directory.Exists(Path.Combine(hostDirectory, "sdk", configuredSdkVersion)))
        {
            sdkVersion = configuredSdkVersion;
        }

        if (versionResult.ExitCode != 0 || string.IsNullOrWhiteSpace(sdkVersion))
        {
            return null;
        }

        var sdkBasePath = Path.Combine(hostDirectory, "sdk", sdkVersion);
        if (!Directory.Exists(sdkBasePath))
        {
            sdkBasePath = null;
        }

        var msBuildPath = sdkBasePath is null
            ? null
            : Path.Combine(sdkBasePath, "MSBuild.dll");
        if (msBuildPath is not null && !File.Exists(msBuildPath))
        {
            msBuildPath = null;
        }

        var relevantEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_MULTILEVEL_LOOKUP", "MSBuildSDKsPath" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                relevantEnvironment[name] = value;
            }
        }

        return new MsBuildEnvironmentSnapshot(
            dotNetPath,
            sdkVersion,
            sdkBasePath,
            msBuildPath,
            globalJsonPath,
            configuration,
            platform,
            RepositoryRestoreState.NotAttempted,
            relevantEnvironment);
    }

    /// <summary>Runs an explicitly trusted restore and records its outcome.</summary>
    public static async Task<MsBuildEnvironmentSnapshot> RestoreAsync(
        MsBuildEnvironmentSnapshot environment,
        string repositoryPath,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        DotNetProcessResult result = await RunDotNetAsync(
            environment.DotNetPath,
            repositoryPath,
            ["restore", solutionPath, "--nologo"],
            cancellationToken);
        return environment with
        {
            RestoreState = result.ExitCode == 0
                ? RepositoryRestoreState.Succeeded
                : RepositoryRestoreState.Failed,
        };
    }

    private static async Task<DotNetProcessResult> RunDotNetAsync(
        string dotNetPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotNetPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            return new DotNetProcessResult(-1, string.Empty, "The dotnet process did not start.");
        }

        Task<string> standardOutput = ReadBoundedAsync(process.StandardOutput);
        Task<string> standardError = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        var captured = await Task.WhenAll(standardOutput, standardError);
        return new DotNetProcessResult(
            process.ExitCode,
            captured[0],
            captured[1]);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var retained = new StringBuilder(_maximumCapturedCharacters);
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0)
            {
                break;
            }

            var remaining = _maximumCapturedCharacters - retained.Length;
            if (remaining > 0)
            {
                retained.Append(buffer, 0, Math.Min(count, remaining));
            }
        }

        return retained.ToString();
    }

    private sealed record DotNetProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
