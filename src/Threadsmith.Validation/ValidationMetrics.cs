namespace Threadsmith.Validation;

using System.Diagnostics.Metrics;

/// <summary>Shared validation metric instruments.</summary>
internal static class ValidationMetrics
{
    /// <summary>Validation subsystem meter.</summary>
    public static readonly Meter Meter = new("Threadsmith.Validation");
}
