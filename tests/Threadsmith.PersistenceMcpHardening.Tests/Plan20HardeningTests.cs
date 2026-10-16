// Threadsmith.NET Milestone 8 — Plan 20: Operational hardening tests.
//
// Covers: the canary-secret diagnostic bundle gate (M8 exit: "diagnostic bundles exclude secrets"),
// redaction across persisted paths, and bundle size limits.
namespace Threadsmith.PersistenceMcpHardening.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Diagnostic bundle and redaction hardening tests (§23.4, M8 exit).</summary>
public static class DiagnosticBundleTests
{
    /// <summary>Diagnostic bundle generation redacts and excludes a canary secret.</summary>
    [Fact]
    public static async Task Diagnostic_bundle_excludes_canary_secret()
    {
        const string canary = "sk-CANARY-abcdef0123456789";
        var directory = Path.Combine(Path.GetTempPath(), $"m8-bundle-{Guid.NewGuid():N}");
        try
        {
            var generator = new DiagnosticBundleGenerator(new SecretOutputSanitizer());
            var options = new DiagnosticBundleOptions
            {
                Enabled = true,
                Directory = directory,
                IncludeLogs = true,
                IncludeEvents = true,
                IncludeArtifacts = false,
                IncludeConfiguration = true,
                IncludeVersionInfo = true,
            };
            var inputs = new DiagnosticBundleInputs
            {
                HostVersion = "0.1.0-test",
                LogText = $"Running with credential {canary} embedded in a log line.",
                EventJson = "{\"$type\":\"toolInvocationCompleted\",\"ResultJson\":\"error: api_key=" + canary + "\"}",
                SanitizedConfigurationJson = "{\"model\":{\"defaultProfile\":\"default\",\"secretKeyReference\":\"secrets:KEY\",\"endpoint\":\"https://api.example.com?token=" + canary + "\"}}",
            };

            var result = await generator.GenerateAsync(options, inputs, CancellationToken.None);

            Assert.True(result.RedactionCount > 0);
            var excludesCanary = await DiagnosticBundleGenerator.BundleExcludesCanaryAsync(
                result.BundlePath,
                canary,
                CancellationToken.None);
            Assert.True(excludesCanary, "The canary secret leaked into the diagnostic bundle.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Diagnostic bundle generation fails when the feature is disabled.</summary>
    [Fact]
    public static async Task Disabled_bundle_generation_throws()
    {
        var generator = new DiagnosticBundleGenerator(new SecretOutputSanitizer());
        var options = new DiagnosticBundleOptions { Enabled = false };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(options, new DiagnosticBundleInputs { HostVersion = "x" }, CancellationToken.None));
    }

    /// <summary>An oversized diagnostic bundle is refused without leaving a partial archive.</summary>
    [Fact]
    public static async Task Oversized_bundle_is_refused_and_deleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"m8-oversize-{Guid.NewGuid():N}");
        try
        {
            var generator = new DiagnosticBundleGenerator(new SecretOutputSanitizer());
            var options = new DiagnosticBundleOptions
            {
                Enabled = true,
                Directory = directory,
                IncludeLogs = true,
                MaximumBytes = 64,
            };
            var inputs = new DiagnosticBundleInputs
            {
                HostVersion = "0.1.0-test",
                LogText = new string('x', 4096),
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => generator.GenerateAsync(options, inputs, CancellationToken.None));
            var bundles = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.zip")
                : [];
            Assert.Empty(bundles);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

/// <summary>Startup redaction audit tests (plan-20, §19.6).</summary>
public static class RedactionAuditTests
{
    /// <summary>The redaction audit reports persisted artifact content containing a secret.</summary>
    [Fact]
    public static async Task Audit_reports_finding_for_unredacted_artifact()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            // Bypass the sanitizing ArtifactStore to plant an unredacted artifact directly.
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var rawStore = new ArtifactStore(fixture.ConnectionString, dir.Path, new PassThroughSanitizer());
            await rawStore.InitializeAsync();
            var session = SessionId.New();
            const string planted = "Useful output\ntoken=sk-leaked-secret-1234567890";
            var artifact = await rawStore.StoreAsync(planted, "processOutput", session, CancellationToken.None);
            Assert.Equal(planted, await rawStore.ReadAsync(artifact.ContentHash));
            Assert.Equal(planted, await File.ReadAllTextAsync(Path.Combine(dir.Path, artifact.RelativePath), TestContext.Current.CancellationToken));

            var audit = new RedactionAudit(
                store,
                rawStore,
                new SecretOutputSanitizer(),
                new RedactionAuditOptions { Enabled = true, RepairArtifacts = false },
                NullLogger<RedactionAudit>.Instance);
            var outcome = await audit.RunAsync();

            Assert.True(outcome.Findings >= 1);
            Assert.Equal(0, outcome.RepairedArtifacts);
            Assert.Equal(planted, await rawStore.ReadAsync(artifact.ContentHash));
            Assert.Equal(planted, await File.ReadAllTextAsync(Path.Combine(dir.Path, artifact.RelativePath), TestContext.Current.CancellationToken));
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }

    /// <summary>The redaction audit repairs unsafe artifact content when repair is enabled.</summary>
    [Fact]
    public static async Task Audit_repairs_artifact_when_enabled()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var rawStore = new ArtifactStore(fixture.ConnectionString, dir.Path, new PassThroughSanitizer());
            await rawStore.InitializeAsync();
            var session = SessionId.New();
            const string planted = "Useful output\ntoken=sk-leaked-secret-1234567890";
            var artifact = await rawStore.StoreAsync(planted, "processOutput", session, CancellationToken.None);
            Assert.Equal(planted, await rawStore.ReadAsync(artifact.ContentHash));
            Assert.Equal(planted, await File.ReadAllTextAsync(Path.Combine(dir.Path, artifact.RelativePath), TestContext.Current.CancellationToken));

            var audit = new RedactionAudit(
                store,
                rawStore,
                new SecretOutputSanitizer(),
                new RedactionAuditOptions { Enabled = true, RepairArtifacts = true },
                NullLogger<RedactionAudit>.Instance);
            var outcome = await audit.RunAsync();

            Assert.True(outcome.Findings >= 1);
            Assert.Equal(1, outcome.RepairedArtifacts);
            Assert.Null(await rawStore.ReadAsync(artifact.ContentHash));
            var repairedMetadata = Assert.Single(await rawStore.ListAsync(session));
            Assert.NotEqual(artifact.ContentHash, repairedMetadata.ContentHash);
            Assert.Equal(artifact.Kind, repairedMetadata.Kind);
            Assert.Equal(artifact.SessionId, repairedMetadata.SessionId);
            Assert.False(File.Exists(Path.Combine(dir.Path, artifact.RelativePath)));
            var repaired = await rawStore.ReadAsync(repairedMetadata.ContentHash);
            Assert.Equal(new SecretOutputSanitizer().Sanitize(planted), repaired);
            Assert.NotNull(repaired);
            var repairedBytes = Encoding.UTF8.GetBytes(repaired);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(repairedBytes)), repairedMetadata.ContentHash);
            Assert.Equal(repairedBytes.Length, repairedMetadata.Length);
            Assert.Equal(repaired, await File.ReadAllTextAsync(Path.Combine(dir.Path, repairedMetadata.RelativePath), TestContext.Current.CancellationToken));
            Assert.Contains("Useful output", repaired, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-leaked-secret-1234567890", repaired, StringComparison.Ordinal);
            var repeated = await audit.RunAsync();
            Assert.Equal(0, repeated.Findings);
            Assert.Equal(0, repeated.RepairedArtifacts);
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }

    /// <summary>A pass-through sanitizer used only to plant unredacted content for the audit.</summary>
    private sealed class PassThroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string output)
        {
            return output;
        }
    }
}