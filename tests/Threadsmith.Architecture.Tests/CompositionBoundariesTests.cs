// Threadsmith.NET Milestone 23.1 — Plan 65: Application composition boundary tests.
//
// Guards the cohesive non-owning composition inputs that replaced the flat 32-property
// ApplicationCompositionContext, without requiring a full production composition run.
namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using Threadsmith.App;
using Xunit;

/// <summary>Composition boundary structure tests for Plan 65 (AR-02).</summary>
public static class CompositionBoundariesTests
{
    private static readonly Assembly AppAssembly = typeof(ApplicationCompositionInputs).Assembly;

    /// <summary>The flat aggregate parameter bag no longer exists.</summary>
    [Fact]
    public static void ApplicationCompositionContext_NoLongerExists()
    {
        var flat = AppAssembly.GetType("Threadsmith.App.ApplicationCompositionContext");

        Assert.Null(flat);
    }

    /// <summary>Composition inputs expose exactly five cohesive responsibility-grouped bundles.</summary>
    [Fact]
    public static void ApplicationCompositionInputs_HasFiveCohesiveSubRecords()
    {
        var inputs = typeof(ApplicationCompositionInputs);
        PropertyInfo[] properties = [.. inputs.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic).Where(IsInitOnly)];

        Assert.Equal(
            ["Host", "Integration", "Persistence", "Semantic", "Tools"],
            properties.Select(p => p.Name).Order());
        Assert.All(
            properties,
            p => Assert.True(IsInitOnly(p), $"{p.Name} must be init-only."));
    }

    /// <summary>The five sub-records preserve all 33 already-initialized dependencies.</summary>
    [Fact]
    public static void SubRecords_PreserveThirtyThreeDependencies()
    {
        var total = SubRecordPropertyCount<HostCompositionInputs>()
            + SubRecordPropertyCount<PersistenceCompositionInputs>()
            + SubRecordPropertyCount<ToolPolicyCompositionInputs>()
            + SubRecordPropertyCount<SemanticCompositionInputs>()
            + SubRecordPropertyCount<IntegrationCompositionInputs>();

        Assert.Equal(33, total);
    }

    /// <summary>Each sub-record has one clear responsibility with the expected dependency distribution.</summary>
    [Theory]
    [InlineData(typeof(HostCompositionInputs), 10)]
    [InlineData(typeof(PersistenceCompositionInputs), 11)]
    [InlineData(typeof(ToolPolicyCompositionInputs), 8)]
    [InlineData(typeof(SemanticCompositionInputs), 2)]
    [InlineData(typeof(IntegrationCompositionInputs), 2)]
    public static void SubRecord_HasExpectedResponsibilitySize(Type subRecord, int expected)
    {
        Assert.Equal(expected, SubRecordPropertyCount(subRecord));
    }

    /// <summary>All composition input bundles are immutable sealed records with init-only properties.</summary>
    [Fact]
    public static void InputBundles_AreImmutableSealedRecordsWithInitOnlyProperties()
    {
        Type[] bundles =
        [
            typeof(ApplicationCompositionInputs),
            typeof(HostCompositionInputs),
            typeof(PersistenceCompositionInputs),
            typeof(ToolPolicyCompositionInputs),
            typeof(SemanticCompositionInputs),
            typeof(IntegrationCompositionInputs),
        ];

        Assert.All(
            bundles,
            bundle =>
            {
                Assert.True(bundle.IsSealed, $"{bundle.Name} must be sealed.");
                Assert.True(IsRecord(bundle), $"{bundle.Name} must be a record.");
                Assert.All(
                    bundle.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic),
                    p => Assert.True(IsInitOnly(p) || !p.CanWrite, $"{bundle.Name}.{p.Name} must be init-only or read-only."));
            });
    }

    /// <summary>Passive composition inputs are non-owning and implement no disposal.</summary>
    [Fact]
    public static void InputBundles_DoNotImplementDisposal()
    {
        Type[] bundles =
        [
            typeof(ApplicationCompositionInputs),
            typeof(HostCompositionInputs),
            typeof(PersistenceCompositionInputs),
            typeof(ToolPolicyCompositionInputs),
            typeof(SemanticCompositionInputs),
            typeof(IntegrationCompositionInputs),
        ];

        Assert.All(
            bundles,
            bundle =>
            {
                Assert.False(typeof(IDisposable).IsAssignableFrom(bundle), $"{bundle.Name} must not be disposable.");
                Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(bundle), $"{bundle.Name} must not be async-disposable.");
            });
    }

    /// <summary>No composition input exposes a service locator or DI-container type.</summary>
    [Fact]
    public static void InputBundles_DoNotExposeServiceLocatorTypes()
    {
        Type[] bundles =
        [
            typeof(ApplicationCompositionInputs),
            typeof(HostCompositionInputs),
            typeof(PersistenceCompositionInputs),
            typeof(ToolPolicyCompositionInputs),
            typeof(SemanticCompositionInputs),
            typeof(IntegrationCompositionInputs),
        ];
        string[] forbidden =
        [
            "System.IServiceProvider",
            "Microsoft.Extensions.DependencyInjection.IServiceCollection",
            "Microsoft.Extensions.DependencyInjection.IServiceProviderIsService",
            "Microsoft.Extensions.DependencyInjection.ServiceCollection",
        ];

        Assert.All(
            bundles,
            bundle => Assert.All(
                bundle.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic),
                p => Assert.False(
                    forbidden.Contains(p.PropertyType.FullName, StringComparer.Ordinal),
                    $"{bundle.Name}.{p.Name} must not expose {p.PropertyType.FullName}.")));
    }

    private static bool IsRecord(Type type)
    {
        return Array.Exists(
            type.GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEquatable<>));
    }

    private static bool IsInitOnly(PropertyInfo property)
    {
        var setter = property.GetSetMethod(nonPublic: true);
        if (setter is null)
        {
            return false;
        }

        var modifiers = setter.ReturnParameter.GetRequiredCustomModifiers();
        return Array.Exists(modifiers, type => type.Name == "IsExternalInit");
    }

    private static int SubRecordPropertyCount<T>()
    {
        return SubRecordPropertyCount(typeof(T));
    }

    private static int SubRecordPropertyCount(Type subRecord)
    {
        return subRecord.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic).Count(IsInitOnly);
    }
}