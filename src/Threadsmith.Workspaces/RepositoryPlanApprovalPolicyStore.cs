namespace Threadsmith.Workspaces;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Writes repository-owned plan approval policy markers.</summary>
internal interface IRepositoryPlanApprovalPolicyStore
{
    /// <summary>Writes one persistable plan approval policy for the supplied repository binding.</summary>
    /// <param name="binding">Immutable repository binding.</param>
    /// <param name="policy">Policy to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WritePolicyAsync(
        PlanApprovalRepositoryBinding binding,
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>Persists repository-controlled plan approval policy markers in `.threadsmith/config.json`.</summary>
internal sealed class RepositoryPlanApprovalPolicyStore : IRepositoryPlanApprovalPolicyStore
{
    private static readonly JsonNodeOptions JsonNodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <inheritdoc />
    public async Task WritePolicyAsync(
        PlanApprovalRepositoryBinding binding,
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!Enum.IsDefined(policy) || policy == PlanApprovalPolicy.TrustSession)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(
            binding.RepositoryRoot,
            binding.ConfigurationPath);
        Directory.CreateDirectory(binding.ConfigurationDirectory);
        PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(
            binding.RepositoryRoot,
            binding.ConfigurationPath);
        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            binding.ConfigurationPath,
            async token => await WritePolicyCoreAsync(binding, policy, token),
            cancellationToken);
    }

    private static async Task WritePolicyCoreAsync(
        PlanApprovalRepositoryBinding binding,
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(binding.ConfigurationPath, cancellationToken);
        var planning = root["planning"] as JsonObject ?? [];
        root["planning"] = planning;
        if (policy == PlanApprovalPolicy.AlwaysTrustRepo)
        {
            planning["approvalPolicy"] = "alwaysTrustRepo";
            planning["approvalRepositoryIdentity"] = binding.RepositoryIdentity;
        }
        else
        {
            planning["approvalPolicy"] = SerializePolicy(policy);
            planning.Remove("approvalRepositoryIdentity");
        }

        await WriteAtomicAsync(binding, root, cancellationToken);
    }

    private static async Task<JsonObject> ReadRootAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configurationPath))
        {
            return [];
        }

        return JsonNode.Parse(
            await File.ReadAllTextAsync(configurationPath, cancellationToken),
            JsonNodeOptions,
            documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("Repository configuration must contain a JSON object.");
    }

    private static async Task WriteAtomicAsync(
        PlanApprovalRepositoryBinding binding,
        JsonObject root,
        CancellationToken cancellationToken)
    {
        var temporaryPath = binding.ConfigurationPath + $".{Guid.NewGuid():N}.tmp";
        Exception? primaryException = null;
        try
        {
            PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(
                binding.RepositoryRoot,
                binding.ConfigurationPath);
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(JsonOptions) + Environment.NewLine,
                cancellationToken);
            PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(
                binding.RepositoryRoot,
                binding.ConfigurationPath);
            File.Move(temporaryPath, binding.ConfigurationPath, overwrite: true);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(
                        binding.RepositoryRoot,
                        temporaryPath);
                    File.Delete(temporaryPath);
                }
                catch when (primaryException is not null)
                {
                }
            }
        }
    }

    private static string SerializePolicy(PlanApprovalPolicy policy)
    {
        return policy switch
        {
            PlanApprovalPolicy.ReviewAll => "reviewAll",
            PlanApprovalPolicy.ReviewRisky => "reviewRisky",
            PlanApprovalPolicy.AutoApproveAllValid => "autoApproveAllValid",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }
}
