namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies configurable operational bounds independently of delegation authority.</summary>
public sealed partial class AgentOperationalLimitTests
{
    /// <summary>Every integer option accepts zero and larger values, but rejects negative configuration.</summary>
    [Fact]
    public void Options_AllIntegerLimitsAreDisableableAndRejectNegatives()
    {
        foreach (var property in typeof(DelegateAgentsOptions).GetProperties()
            .Where(item => item.PropertyType == typeof(int) && item.CanWrite))
        {
            foreach (var value in new[] { 0, int.MaxValue })
            {
                var options = BindOptions(property.Name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                options.Validate();
                Assert.Equal(value, Assert.IsType<int>(property.GetValue(options)));
            }

            var negative = BindOptions(property.Name, "-1");
            Assert.Throws<InvalidOperationException>(negative.Validate);
        }
    }

    /// <summary>The global switch disables request, runtime, summary, result, and child-duration limits.</summary>
    [Fact]
    public void Options_GlobalDisablePreservesConfiguredDefaultsButClearsEffectiveLimits()
    {
        var options = BindOptions(nameof(DelegateAgentsOptions.EnforceOperationalLimits), "false");
        options.Validate();

        Assert.Equal(3, options.MaximumAgents);
        Assert.Equal(0, options.EffectiveLimit(options.MaximumAgents));
        Assert.Equal(0, options.MaximumSummaryCharacters);
        Assert.False(options.ResultLimits.EnforceLimits);
        Assert.False(options.CreateAssignmentLimits().EnforceLimits);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ChildBudget.WallTime);
        Assert.Equal(TimeSpan.Zero, options.EffectiveChildBudget.WallTime);
        Assert.Equal(DateTimeOffset.MaxValue, options.EffectiveChildBudget.CreateDeadline(DateTimeOffset.UtcNow));
        foreach (var property in typeof(DelegateAgentsOptions).GetProperties().Where(item => item.PropertyType == typeof(int)))
        {
            Assert.Equal(0, options.EffectiveLimit(Assert.IsType<int>(property.GetValue(options))));
        }

        Assert.Throws<InvalidOperationException>(() => (options with { MaximumAgents = -1 }).Validate());
    }

    /// <summary>Model input uses trusted configurable count and text limits rather than compiled ceilings.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Input_RaisedOrDisabledLimitsAllowValuesBeyondPriorCeilings(bool disabled)
    {
        var options = new DelegateAgentsOptions
        {
            MaximumAgents = disabled ? 0 : 17,
            MaximumTaskCharacters = disabled ? 0 : 4_097,
            MaximumContextCharacters = disabled ? 0 : 8_193,
        };
        var input = new DelegateAgentsInput
        {
            Agents = Enumerable.Range(0, 17).Select(_ => new DelegateAgentRequest
            {
                Task = new string('t', 4_097),
                Context = new string('c', 8_193),
                ToolAccess = DelegateAgentToolAccess.ReadOnly,
            }).ToArray(),
        };

        options.Validate();
        DelegateAgentsInputValidator.Validate(input, options);
        Assert.Throws<ToolArgumentValidationException>(() => DelegateAgentsInputValidator.Validate(input, new DelegateAgentsOptions()));
        Assert.Throws<ToolArgumentValidationException>(() => DelegateAgentsInputValidator.Validate(input with { Agents = [] }, options));
        Assert.Throws<ToolArgumentValidationException>(() => DelegateAgentsInputValidator.Validate(
            input with { Agents = [input.Agents[0] with { Task = " " }] }, options));
        Assert.Throws<ToolArgumentValidationException>(() => DelegateAgentsInputValidator.Validate(
            input with { Agents = [input.Agents[0] with { Role = (AgentRole)99 }] }, options));
    }

    /// <summary>Host validation uses the frozen policy for assignment count, tasks, text, context, and scope length.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_RaisedOrDisabledLimitsSurviveSerialization(bool disabled)
    {
        var limits = new AgentAssignmentLimits
        {
            MaximumAssignments = disabled ? 0 : 17,
            MaximumTasksPerAssignment = disabled ? 0 : 33,
            MaximumTextCharacters = disabled ? 0 : 4_097,
            MaximumContextCharacters = disabled ? 0 : 8_193,
            MaximumScopeCharacters = disabled ? 0 : 1_025,
        };
        var plan = CreatePlan(17) with { AssignmentLimits = limits };
        plan = plan with
        {
            Assignments = plan.Assignments.Select(assignment => assignment with
            {
                Objective = new string('t', 4_097),
                Tasks = Enumerable.Repeat("task", 33).ToArray(),
                InitialContext = new string('c', 8_193),
                Scope = new AgentAssignmentScope { Files = [new string('p', 1_025)] },
            }).ToArray(),
        };

        DelegationPlanValidator.Validate(plan);
        var restored = JsonSerializer.Deserialize<DelegationPlan>(JsonSerializer.Serialize(plan));
        Assert.NotNull(restored);
        Assert.Equal(limits, restored.AssignmentLimits);
        DelegationPlanValidator.Validate(restored);
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan with { AssignmentLimits = new() }));
    }

    /// <summary>Disabling operational limits cannot disable graph, role, path, or tool authority checks.</summary>
    [Fact]
    public void Plan_UnlimitedStillRejectsAuthorityAndGraphViolations()
    {
        var plan = CreatePlan(1);
        var assignment = Assert.Single(plan.Assignments);
        Assert.Throws<UnauthorizedAccessException>(() => DelegationPlanValidator.Validate(plan with
        {
            Assignments = [assignment with { Policy = assignment.Policy with { AllowedToolIds = ["apply_mutation"] } }],
        }));
        Assert.Throws<UnauthorizedAccessException>(() => DelegationPlanValidator.Validate(plan with
        {
            Assignments = [assignment with { Scope = new AgentAssignmentScope { Files = ["../escape"] } }],
        }));
        Assert.Throws<UnauthorizedAccessException>(() => DelegationPlanValidator.Validate(plan with
        {
            Assignments = [assignment with { Mode = AgentRunMode.IsolatedWorktreeMutation }],
        }));
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan with
        {
            Assignments = [assignment with { Dependencies = [assignment.AssignmentId] }],
        }));
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan with { Assignments = [] }));
    }

    /// <summary>Zero means unlimited duration, while very long positive durations remain representable.</summary>
    [Fact]
    public void Budget_ZeroAndVeryLongDurationsAggregateWithoutOverflow()
    {
        var unlimited = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero);
        var longBudget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.MaxValue);
        Assert.Equal(TimeSpan.Zero, AgentResourceBudget.Aggregate([unlimited, longBudget]).WallTime);
        Assert.Equal(TimeSpan.MaxValue, AgentResourceBudget.Aggregate([longBudget, longBudget]).WallTime);
        Assert.Equal(DateTimeOffset.MaxValue, longBudget.CreateDeadline(DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromTicks(-1)));
        new DelegateAgentsOptions { ChildBudget = longBudget }.Validate();
        new DelegateAgentsOptions { ChildBudget = unlimited }.Validate();
        var plan = CreatePlan(1) with { ParentBudget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(1)) };
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan));
    }

    private static DelegateAgentsOptions BindOptions(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"agents:delegation:{key}"] = value })
            .Build();
        using var ownedConfiguration = configuration as IDisposable;
        return configuration.GetSection("agents:delegation").Get<DelegateAgentsOptions>()
            ?? throw new InvalidOperationException("Options were not bound.");
    }

    private static DelegationPlan CreatePlan(int count, TimeSpan? wallTime = null, AgentRole role = AgentRole.Explorer)
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var budget = AgentResourceBudget.CreateTelemetryOnly(wallTime ?? TimeSpan.Zero);
        var assignments = Enumerable.Range(0, count).Select(_ => new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = role,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = "Inspect the assigned source.",
            Tasks = ["Return evidence-backed findings."],
            OutputSchema = DelegateAgentsContract.FindingSchema,
            StoppingCondition = "Stop after the assigned task.",
            Deadline = budget.CreateDeadline(acceptedAt),
            Scope = new AgentAssignmentScope { IsOwnershipProven = true },
            Policy = new AgentPolicySnapshot
            {
                ModelSelectionRationale = "test",
                ContextPolicyVersion = "agent-context/2",
                ToolPolicyVersion = "delegate-agents-read-only/1",
                ResultLimits = new AgentResultLimits { EnforceLimits = false },
            },
            Budget = budget,
        }).ToArray();
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                RepositoryIdentity = "repository",
                BaselineIdentity = "baseline",
                WorkspaceId = WorkspaceId.New(),
                ApprovedPlanIdentity = "approved-test-plan",
                ApprovedPlanRevision = 1,
            },
            Assignments = assignments,
            AssignmentLimits = new AgentAssignmentLimits { EnforceLimits = false },
            ParentBudget = AgentResourceBudget.Aggregate(assignments.Select(item => item.Budget).ToArray()),
            AcceptedAt = acceptedAt,
        };
    }
}
