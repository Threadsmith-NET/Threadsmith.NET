namespace Threadsmith.App;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;

/// <summary>Reports and asserts startup routing without invoking a provider or persisting preferences.</summary>
internal sealed class EffectiveConfigurationPreflight
{
    private readonly ActiveModelSelectionService? _parent;
    private readonly AgentModelSelector _children;
    private readonly AgentResourceBudget _budget;

    /// <summary>Initializes a new instance of the <see cref="EffectiveConfigurationPreflight"/> class.</summary>
    internal EffectiveConfigurationPreflight(
        ActiveModelSelectionService? parent,
        AgentModelSelector children,
        AgentResourceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(budget);
        _parent = parent;
        _children = children;
        _budget = budget;
    }

    /// <summary>Any preflight settings opt a headless request into the assertion gate.</summary>
    internal static bool IsRequested(IConfiguration configuration)
    {
        var section = configuration.GetSection("headless:preflight");
        return section.Exists();
    }

    /// <summary>Writes one bounded JSON report; a nonzero result prevents request submission.</summary>
    internal async Task<int> RunAsync(
        IConfiguration configuration,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selections = new List<EffectiveConfigurationSelection>();
        var errors = new List<string>();
        var section = configuration.GetSection("headless:preflight");
        var roles = new HashSet<AgentRole>();
        if (section.Value is not null || section.GetSection("expect").Value is not null
            || section.GetSection("roles").GetChildren().Any())
        {
            errors.Add("Preflight requires a roles string and an expect object.");
        }

        foreach (var name in (section["roles"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AgentRoleNames.TryParse(name, out var role))
            {
                roles.Add(role);
            }
            else
            {
                errors.Add("Unknown requested child role.");
            }
        }

        var expectations = section.GetSection("expect").GetChildren().ToArray();
        foreach (var expectation in expectations)
        {
            if (AgentRoleNames.TryParse(expectation.Key, out var role))
            {
                roles.Add(role);
            }
            else if (!string.Equals(expectation.Key, "parent", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Unknown expectation target.");
            }
        }

        if (section.GetChildren().Any(field =>
            !string.Equals(field.Key, "roles", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(field.Key, "expect", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Preflight allows only roles and expect settings.");
        }

        if (_parent is null)
        {
            errors.Add("No active configured parent model is available.");
        }
        else
        {
            var parent = _parent.Current;
            selections.Add(new EffectiveConfigurationSelection(
                "parent",
                parent.ProviderId,
                parent.Profile.Id.Value,
                parent.Profile.Name,
                parent.ReasoningLevel.ToString(),
                parent.Source.ToString(),
                false,
                null));
            foreach (var role in roles.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var child = _children.Preview(role, parent.Profile.Id, parent.ReasoningLevel, _budget);
                    var provenance = child.Provenance
                        ?? throw new InvalidOperationException("Child selection provenance is missing.");
                    selections.Add(new EffectiveConfigurationSelection(
                        AgentRoleNames.GetName(role),
                        provenance.EffectiveProviderId,
                        child.ProfileId.Value,
                        null,
                        child.ReasoningLevel.ToString(),
                        provenance.Source.ToString(),
                        provenance.UsesTrustedCatalog,
                        provenance.FallbackReason));
                }
                catch (InvalidOperationException exception)
                {
                    errors.Add($"{AgentRoleNames.GetName(role)}: {exception.Message}");
                }
            }
        }

        foreach (var expectation in expectations)
        {
            var actual = selections.FirstOrDefault(item => string.Equals(item.Target, expectation.Key, StringComparison.OrdinalIgnoreCase));
            if (actual is null)
            {
                errors.Add("An expected selection is unavailable.");
                continue;
            }

            if (expectation.Value is not null || !expectation.GetChildren().Any())
            {
                errors.Add($"{actual.Target}: expected selection must contain assertion fields.");
            }

            foreach (var field in expectation.GetChildren())
            {
                var value = field.Key.ToLowerInvariant() switch
                {
                    "providerid" => actual.ProviderId,
                    "profileid" => actual.ProfileId.ToString("D"),
                    "reasoninglevel" => actual.ReasoningLevel,
                    "source" => actual.Source,
                    _ => null,
                };
                if (value is null || field.GetChildren().Any() || string.IsNullOrWhiteSpace(field.Value)
                    || !string.Equals(value, field.Value, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(value is null
                        ? $"{actual.Target}: unknown assertion field (allowed: providerId, profileId, reasoningLevel, source)."
                        : $"{actual.Target}: {field.Key} expectation did not match the effective selection.");
                }
            }
        }

        var report = new EffectiveConfigurationReport(
            "effective-configuration/1", errors.Count == 0, selections, errors);
        await output.WriteLineAsync(JsonSerializer.Serialize(report).AsMemory(), cancellationToken);
        return report.Passed ? 0 : 2;
    }
}

/// <summary>Safe routing identity only; no endpoints, credentials, or prompt text.</summary>
internal sealed record EffectiveConfigurationSelection(
    string Target,
    string ProviderId,
    Guid ProfileId,
    string? ProfileName,
    string ReasoningLevel,
    string Source,
    bool UsesTrustedCatalog,
    string? FallbackReason);

/// <summary>Machine-readable pre-dispatch routing assertion outcome.</summary>
internal sealed record EffectiveConfigurationReport(
    string Schema,
    bool Passed,
    IReadOnlyList<EffectiveConfigurationSelection> Selections,
    IReadOnlyList<string> Errors);
